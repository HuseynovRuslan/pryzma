using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Tests;

/// <summary>
/// The jury's evaluation of every eligible work and its internal ranking (InternalJuryRankingV1, owner, 8 October 2026),
/// end to end: the roster is the ballot's own works, frozen; personal invitations need the link and the code; a juror
/// sees the work and their own score and nothing else; scores are whole 1–10, one record per juror and work whether
/// online or from paper; a submission locks; once every juror has submitted, every work is ranked by its exact average,
/// equal averages share a position, and the ranking stays internal; votes and the public result never change.
/// </summary>
public sealed class JuryEvaluationTests : IDisposable
{
    private const string Slug = "emircan-butun-isler";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Work(int Id, string Reference, string Title, string Author);

    /// <summary>Valid votes per eligible work (1–8); the eighth also has 20 votes set aside — counted, it would win.</summary>
    private static readonly int[] Valid = [9, 7, 7, 5, 4, 3, 2, 1];

    /// <summary>
    /// Eight eligible works (six Submitted, one UnderReview, one Approved), written in a shuffled order; and three that
    /// are not on the ballot: a draft, a rejected work and a work in a switched-off nomination. The vote is closed;
    /// the combined final exists, frozen, with three jurors — the state production is in.
    /// </summary>
    private async Task<(HttpClient Admin, int Competition, Work[] Works, Work[] Ineligible)> ScenarioAsync()
    {
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Münsif sınağı", slug = Slug, shortDescription = "Sınaq", hasCategories = false,
        }));
        var id = c.GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var now = _factory.Clock.Now;
        var works = new Work[8];
        var ineligible = new List<Work>();
        await _factory.WithDbAsync(async db =>
        {
            var umumi = new CompetitionCategory { CompetitionId = id, Name = "Ümumi", Slug = "umumi", IsActive = true };
            var off = new CompetitionCategory { CompetitionId = id, Name = "Köhnə", Slug = "kohne", IsActive = false };
            db.CompetitionCategories.AddRange(umumi, off);
            var competition = await db.Competitions.FirstAsync(x => x.Id == id);
            competition.EndDate = now.AddDays(-4);
            competition.VotingStartsAt = now.AddDays(-3);
            competition.VotingEndsAt = now.AddHours(-1);
            await db.SaveChangesAsync();

            ApplicationUser User(string first, string last)
            {
                var email = $"u-{Guid.NewGuid():N}@example.com";
                var u = new ApplicationUser
                {
                    Id = Guid.NewGuid(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
                    NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, FirstName = first, LastName = last,
                    SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10),
                    TermsAcceptedAt = now.AddDays(-10),
                };
                db.Users.Add(u);
                return u;
            }

            Submission Add(int n, SubmissionStatus status, string? reference, CompetitionCategory category)
            {
                var author = User($"Müəllif{n}", "Soyadlı");
                var s = new Submission
                {
                    UserId = author.Id, CompetitionId = id, CompetitionCategoryId = category.Id, Title = $"Əsər {n}", Status = status,
                    ReferenceNumber = reference, SubmittedAt = reference is null ? null : now.AddDays(-5),
                    CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-5),
                };
                db.Submissions.Add(s);
                return s;
            }

            // Written out of order: the roster must still follow the references.
            var subs = new Submission[8];
            foreach (var n in new[] { 3, 1, 8, 2, 5, 7, 4, 6 })
            {
                var status = n == 4 ? SubmissionStatus.UnderReview : n == 6 ? SubmissionStatus.Approved : SubmissionStatus.Submitted;
                subs[n - 1] = Add(n, status, $"SUB-2099-{n:00000}", umumi);
            }
            var draft = Add(9, SubmissionStatus.Draft, null, umumi);
            var rejected = Add(10, SubmissionStatus.Rejected, "SUB-2099-00010", umumi);
            var switchedOff = Add(11, SubmissionStatus.Submitted, "SUB-2099-00011", off);
            await db.SaveChangesAsync();

            for (var i = 0; i < 8; i++)
            {
                works[i] = new Work(subs[i].Id, subs[i].ReferenceNumber!, subs[i].Title, $"Müəllif{i + 1} Soyadlı");
                var invalid = i == 7 ? 20 : 0;
                for (var v = 0; v < Valid[i] + invalid; v++)
                {
                    var voter = User("Səsverən", $"S{i}-{v}");
                    db.Votes.Add(new Vote
                    {
                        UserId = voter.Id, SubmissionId = subs[i].Id, CompetitionCategoryId = umumi.Id, CreatedAt = now.AddDays(-2),
                        InvalidatedAt = v < invalid ? now.AddDays(-1) : null, InvalidationReason = v < invalid ? "sınaq" : null,
                    });
                }
            }
            foreach (var s in new[] { draft, rejected, switchedOff })
                ineligible.Add(new Work(s.Id, s.ReferenceNumber ?? "", s.Title, ""));

            db.CompetitionFinals.Add(new CompetitionFinal
            {
                CompetitionId = id, Stage = FinalStage.Frozen, FrozenAt = now.AddMinutes(-30), FrozenValidVotes = 38,
                FrozenInvalidatedVotes = 20, FrozenVotesFingerprint = "sınaq", FirstPrizeAzn = 250, SecondPrizeAzn = 150, ThirdPrizeAzn = 100,
                Jurors =
                [
                    new FinalJuror { Label = "Aysel Məmmədova", SortOrder = 1, CreatedAt = now },
                    new FinalJuror { Label = "Rauf Əliyev", SortOrder = 2, CreatedAt = now },
                    new FinalJuror { Label = "Nərmin Qasımova", SortOrder = 3, CreatedAt = now },
                ],
            });
            await db.SaveChangesAsync();
        });
        return (admin, id, works, ineligible.ToArray());
    }

    /// <summary>The scenario switched to the new format, its roster frozen; the jurors' ids in order.</summary>
    private async Task<(HttpClient Admin, int Competition, Work[] Works, int[] Jurors)> ReadyAsync()
    {
        var (admin, id, w, _) = await ScenarioAsync();
        await Json(await admin.PostAsync(Url(id), null));
        var e = await Json(await admin.PostAsync(Url(id, "/roster/freeze"), null));
        return (admin, id, w, e.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("jurorId").GetInt32()).ToArray());
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorText(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"expected {(int)expected}, got {(int)res.StatusCode}: {body}");
        return body;
    }

    private static string Url(int id, string path = "") => $"/api/admin/competitions/{id}/jury-evaluation{path}";

    private sealed record Invitation(string Token, string Code, string Link);

    private static async Task<Invitation> InviteAsync(HttpClient admin, int id, int jurorId)
    {
        var inv = await Json(await admin.PostAsJsonAsync(Url(id, $"/jurors/{jurorId}/invitation"), new { days = 14 }));
        var link = inv.GetProperty("link").GetString()!;
        return new Invitation(link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..], inv.GetProperty("code").GetString()!, link);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient browser, Invitation inv, string? code = null) =>
        browser.PostAsJsonAsync("/api/jury/sign-in", new { token = inv.Token, code = code ?? inv.Code });

    private async Task<HttpClient> JurorAsync(HttpClient admin, int id, int jurorId)
    {
        var juror = Browser();
        await Json(await SignInAsync(juror, await InviteAsync(admin, id, jurorId)));
        await Json(await juror.PostAsync("/api/jury/confirm", null));
        return juror;
    }

    private static Task<HttpResponseMessage> ScoreAsync(HttpClient juror, int submissionId, int score, uint? version = null, string? comment = null) =>
        juror.PutAsJsonAsync($"/api/jury/works/{submissionId}/score", new { score, comment, version });

    /// <summary>As the juror's screen saves: on the version it last loaded.</summary>
    private static async Task ScoreAllAsync(HttpClient juror, Work[] w, params int[] scores)
    {
        for (var i = 0; i < w.Length; i++)
        {
            var seen = (await Json(await juror.GetAsync($"/api/jury/works/{w[i].Id}"))).GetProperty("version");
            await Json(await ScoreAsync(juror, w[i].Id, scores[i], seen.ValueKind == JsonValueKind.Null ? null : seen.GetUInt32()));
        }
    }

    /// <summary>
    /// Every juror scores every work and submits. Averages: w8 29/3, w4 9, w1 26/3, w2 8, w3 23/3, w5 = w6 5, w7 1 —
    /// the jury's order is not the public's (w1 9 votes, w2 = w3 7, …, w8 1 valid).
    /// </summary>
    private async Task<HttpClient[]> EveryoneScoresAsync(HttpClient admin, int id, Work[] w, int[] jurors)
    {
        int[][] sheets =
        [
            [9, 8, 7, 9, 5, 5, 1, 10],
            [8, 8, 8, 9, 5, 5, 1, 10],
            [9, 8, 8, 9, 5, 5, 1, 9],
        ];
        var clients = new HttpClient[jurors.Length];
        for (var j = 0; j < jurors.Length; j++)
        {
            clients[j] = await JurorAsync(admin, id, jurors[j]);
            await ScoreAllAsync(clients[j], w, sheets[j]);
            await Json(await clients[j].PostAsync("/api/jury/submit", null));
        }
        return clients;
    }

    private Task<string> VotesFingerprintAsync(int competitionId) =>
        _factory.QueryDbAsync(db => db.Database.SqlQuery<string>($"""
            SELECT md5(string_agg(concat_ws('|', v."Id", v."SubmissionId", v."InvalidatedAt" IS NULL, v."InvalidationReason"), ';' ORDER BY v."Id")) AS "Value"
            FROM "Votes" v JOIN "Submissions" s ON s."Id" = v."SubmissionId" WHERE s."CompetitionId" = {competitionId}
            """).SingleAsync());

    private static async Task<List<(string Action, string Summary)>> AuditAsync(HttpClient admin, int competitionId) =>
        (await Json(await admin.GetAsync(Url(competitionId, "/audit?take=500")))).EnumerateArray()
        .Select(x => (x.GetProperty("action").GetString()!, x.GetProperty("summary").GetString() ?? "")).ToList();

    private static string[] Refs(JsonElement rows, string property = "reference") =>
        rows.EnumerateArray().Select(x => x.GetProperty(property).GetString()!).ToArray();

    // ---- The roster -----------------------------------------------------------------------------

    [Fact]
    public async Task The_roster_is_exactly_the_ballot_in_reference_order_and_stops_changing_once_a_score_exists()
    {
        var (admin, id, w, ineligible) = await ScenarioAsync();

        var before = await Json(await admin.GetAsync(Url(id)));
        Assert.False(before.GetProperty("exists").GetBoolean());
        Assert.Equal(8, before.GetProperty("eligibleNow").GetInt32());

        var e = await Json(await admin.PostAsync(Url(id), null));
        Assert.Equal("InternalJuryRankingV1", e.GetProperty("format").GetString());
        Assert.Equal("Draft", e.GetProperty("status").GetString());
        // The final's three jurors, the same records — not copies.
        Assert.Equal(["Aysel Məmmədova", "Rauf Əliyev", "Nərmin Qasımova"], Refs(e.GetProperty("jurors"), "label"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(Url(id), null)).StatusCode);

        e = await Json(await admin.PostAsync(Url(id, "/roster/freeze"), null));
        Assert.Equal("Open", e.GetProperty("status").GetString());
        Assert.Equal(8, e.GetProperty("rosterSize").GetInt32());
        Assert.Equal(24, e.GetProperty("expectedScores").GetInt32());

        // The catalog: most valid votes first (here the references' own order), each with its public vote.
        var print = await Json(await admin.GetAsync(Url(id, "/print")));
        Assert.Equal(w.Select(x => x.Reference), Refs(print.GetProperty("works")));
        var printedVotes = print.GetProperty("works").EnumerateArray().Select(x => x.GetProperty("vote")).ToList();
        Assert.Equal([1, 2, 2, 4, 5, 6, 7, 8], printedVotes.Select(v => v.GetProperty("position").GetInt32()));
        Assert.Equal(Valid, printedVotes.Select(v => v.GetProperty("validVotes").GetInt32()));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 20], printedVotes.Select(v => v.GetProperty("invalidatedVotes").GetInt32()));
        Assert.Equal(3, print.GetProperty("jurors").GetArrayLength());
        var printed = print.GetRawText();
        Assert.DoesNotContain("Müəllif", printed);
        Assert.All(ineligible.Where(x => x.Reference.Length > 0), x => Assert.DoesNotContain(x.Reference, printed));

        // Frozen again while nothing is scored: the same list. Once a score exists: never.
        await Json(await admin.PostAsync(Url(id, "/roster/freeze"), null));
        var jurors = e.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("jurorId").GetInt32()).ToArray();
        var juror = await JurorAsync(admin, id, jurors[0]);
        await Json(await ScoreAsync(juror, w[0].Id, 7));
        Assert.Contains("dəyişmir", await ErrorText(await admin.PostAsync(Url(id, "/roster/freeze"), null), HttpStatusCode.Conflict));

        // The ballot changes later (a work rejected): the roster does not follow, and the admin is told.
        await _factory.WithDbAsync(async db =>
        {
            var s = await db.Submissions.FirstAsync(x => x.Id == w[4].Id);
            s.Status = SubmissionStatus.Rejected;
            await db.SaveChangesAsync();
        });
        e = await Json(await admin.GetAsync(Url(id)));
        Assert.Equal(8, e.GetProperty("rosterSize").GetInt32());
        Assert.Equal(7, e.GetProperty("eligibleNow").GetInt32());
        Assert.True(e.GetProperty("rosterDiffers").GetBoolean());

        // Nor can its work or its competition be deleted from under the evaluation.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/submissions/{w[4].Id}/permanent")).StatusCode);
        Assert.True(await _factory.QueryDbAsync(db => db.Submissions.AnyAsync(s => s.Id == w[4].Id)));
    }

    [Fact]
    public async Task Jurors_can_be_added_or_removed_only_before_scoring_begins()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var e = await Json(await admin.PostAsJsonAsync(Url(id, "/jurors"), new { label = "Dördüncü Münsif" }));
        Assert.Equal(4, e.GetProperty("jurorCount").GetInt32());
        var fourth = e.GetProperty("jurors").EnumerateArray().Last().GetProperty("jurorId").GetInt32();
        Assert.Equal(400, (int)(await admin.PostAsJsonAsync(Url(id, "/jurors"), new { label = "rauf əliyev" })).StatusCode);
        e = await Json(await admin.DeleteAsync(Url(id, $"/jurors/{fourth}")));
        Assert.Equal(3, e.GetProperty("jurorCount").GetInt32());

        var juror = await JurorAsync(admin, id, jurors[1]);
        await Json(await ScoreAsync(juror, w[0].Id, 5));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Url(id, "/jurors"), new { label = "Gec Münsif" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(Url(id, $"/jurors/{jurors[2]}"))).StatusCode);

        // Renaming changes no number and stays possible.
        e = await Json(await admin.PutAsJsonAsync(Url(id, $"/jurors/{jurors[1]}"), new { label = "Rauf Əliyev-Həsənov" }));
        Assert.Contains("Rauf Əliyev-Həsənov", Refs(e.GetProperty("jurors"), "label"));
        var session = await Json(await juror.GetAsync("/api/jury/session"));
        Assert.Equal("Rauf Əliyev-Həsənov", session.GetProperty("jurorName").GetString());
    }

    // ---- Invitations and sign-in ----------------------------------------------------------------

    [Fact]
    public async Task An_invitation_needs_both_the_link_and_the_code_stores_only_hashes_and_locks_after_five_wrong_codes()
    {
        var (admin, id, _, jurors) = await ReadyAsync();
        var inv = await InviteAsync(admin, id, jurors[0]);
        Assert.StartsWith("/jury/start#t=", inv.Link);
        Assert.True(inv.Token.Length >= 40);
        Assert.Matches(@"^\d{3} \d{3}$", inv.Code);

        var stored = await _factory.QueryDbAsync(db => db.JuryAssignments.AsNoTracking().SingleAsync(a => a.JurorId == jurors[0]));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inv.Token))).ToLowerInvariant(), stored.InviteTokenHash);
        Assert.StartsWith("pbkdf2-sha256$", stored.InviteCodeHash);
        Assert.DoesNotContain(inv.Code.Replace(" ", ""), stored.InviteCodeHash!);
        Assert.DoesNotContain(inv.Token, stored.InviteTokenHash!);

        // The link alone, a made-up link, the code alone: nothing.
        var browser = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(browser, inv with { Token = inv.Token[..^2] + "xx" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SignInAsync(browser, inv, code: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SignInAsync(browser, inv with { Token = "" })).StatusCode);
        var wrong = inv.Code.Replace(" ", "") == "000000" ? "111111" : "000000";
        for (var i = 1; i < 5; i++)
            Assert.Contains("səhvdir", await ErrorText(await SignInAsync(browser, inv, wrong), HttpStatusCode.Unauthorized));
        Assert.Contains("kilidlənib", await ErrorText(await SignInAsync(browser, inv, wrong), HttpStatusCode.Unauthorized));
        // Locked: even the right code no longer opens it.
        Assert.Contains("kilidlənib", await ErrorText(await SignInAsync(browser, inv), HttpStatusCode.Unauthorized));
        var state = (await Json(await admin.GetAsync(Url(id)))).GetProperty("jurors")[0].GetProperty("invitation");
        Assert.Equal("Locked", state.GetProperty("state").GetString());
        Assert.Equal(5, state.GetProperty("failedAttempts").GetInt32());

        // A new invitation: the old link is dead, the new one works — with the code typed any way.
        var fresh = await InviteAsync(admin, id, jurors[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(browser, inv)).StatusCode);
        var res = await SignInAsync(browser, fresh, fresh.Code.Replace(" ", "-"));
        var session = await Json(res);
        Assert.Equal("Aysel Məmmədova", session.GetProperty("jurorName").GetString());
        Assert.False(session.GetProperty("confirmed").GetBoolean());
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("meydan_jury=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/jury", cookie);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());

        // The audit tells the story without a secret in it.
        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == AuditActions.JuryInvitationIssued);
        Assert.Equal(5, audit.Count(a => a.Action == AuditActions.JurorSignInFailed));
        Assert.Contains(audit, a => a.Action == AuditActions.JurorSignedIn);
        Assert.All(audit, a =>
        {
            Assert.DoesNotContain(inv.Token, a.Summary);
            Assert.DoesNotContain(fresh.Token, a.Summary);
            Assert.DoesNotContain(fresh.Code, a.Summary);
        });
    }

    [Fact]
    public async Task Revoking_ends_the_session_at_once_and_invitations_and_sessions_expire()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        await Json(await juror.GetAsync("/api/jury/dashboard"));
        await Json(await admin.DeleteAsync(Url(id, $"/jurors/{jurors[0]}/invitation")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await juror.GetAsync("/api/jury/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ScoreAsync(juror, w[0].Id, 5)).StatusCode);

        // A session lasts twelve hours; an unused invitation its days.
        var second = await JurorAsync(admin, id, jurors[1]);
        var unused = await InviteAsync(admin, id, jurors[2]);
        _factory.Clock.Now = _factory.Clock.Now.AddHours(13);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/jury/session")).StatusCode);
        _factory.Clock.Now = _factory.Clock.Now.AddDays(14);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(Browser(), unused)).StatusCode);
    }

    [Fact]
    public async Task Juror_and_admin_credentials_never_open_each_others_api()
    {
        var (admin, id, _, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await juror.GetAsync(Url(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await juror.GetAsync(Url(id, "/matrix"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await juror.GetAsync($"/api/admin/competitions/{id}/final")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/jury/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/jury/session")).StatusCode);
    }

    [Fact]
    public async Task Juror_sign_in_is_throttled_per_address()
    {
        using var limited = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:JurySignInsPer15Minutes", "3"));
        var client = limited.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/jury/sign-in", new { token = $"guess{i}", code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/jury/sign-in", new { token = "guess", code = "123456" })).StatusCode);
    }

    // ---- What a juror sees ----------------------------------------------------------------------

    [Fact]
    public async Task A_juror_confirms_their_name_then_sees_every_work_and_only_their_own_scores()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var browser = Browser();
        await Json(await SignInAsync(browser, await InviteAsync(admin, id, jurors[0])));
        Assert.Contains("təsdiqləyin", await ErrorText(await browser.GetAsync("/api/jury/dashboard"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await ScoreAsync(browser, w[0].Id, 5)).StatusCode);
        var session = await Json(await browser.PostAsync("/api/jury/confirm", null));
        Assert.True(session.GetProperty("confirmed").GetBoolean());

        var other = await JurorAsync(admin, id, jurors[1]);
        await Json(await ScoreAsync(other, w[0].Id, 2, comment: "Başqa münsifin qeydi"));
        await Json(await ScoreAsync(browser, w[1].Id, 9));

        var res = await browser.GetAsync("/api/jury/dashboard");
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var d = await Json(res);
        Assert.Equal(8, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("scored").GetInt32());
        Assert.Equal(w.Select(x => x.Reference), Refs(d.GetProperty("works")));
        // Their own score only: the other juror's 2 on the first work is not theirs.
        Assert.Equal(JsonValueKind.Null, d.GetProperty("works")[0].GetProperty("score").ValueKind);
        Assert.Equal(9, d.GetProperty("works")[1].GetProperty("score").GetInt32());
        Assert.Equal(
            ["submissionId", "reference", "title", "image", "vote", "score", "hasComment"],
            d.GetProperty("works")[0].EnumerateObject().Select(p => p.Name));
        // Informed evaluation: the public vote of each work, as counts only.
        var vote = d.GetProperty("works")[0].GetProperty("vote");
        Assert.Equal(["position", "tieSize", "validVotes", "invalidatedVotes"], vote.EnumerateObject().Select(p => p.Name));
        Assert.Equal((1, 1, 9, 0), (vote.GetProperty("position").GetInt32(), vote.GetProperty("tieSize").GetInt32(),
            vote.GetProperty("validVotes").GetInt32(), vote.GetProperty("invalidatedVotes").GetInt32()));
        Assert.Equal(20, d.GetProperty("works")[7].GetProperty("vote").GetProperty("invalidatedVotes").GetInt32());

        var work = await Json(await browser.GetAsync($"/api/jury/works/{w[0].Id}"));
        Assert.Equal(1, work.GetProperty("index").GetInt32());
        Assert.Equal(9, work.GetProperty("vote").GetProperty("validVotes").GetInt32());
        Assert.Equal(JsonValueKind.Null, work.GetProperty("previousId").ValueKind);
        Assert.Equal(w[1].Id, work.GetProperty("nextId").GetInt32());
        Assert.Equal(JsonValueKind.Null, work.GetProperty("score").ValueKind);
        foreach (var text in new[] { d.GetRawText(), work.GetRawText() })
        {
            Assert.DoesNotContain("Müəllif", text);
            Assert.DoesNotContain("Başqa münsifin", text);
            Assert.DoesNotContain("Rauf", text);
            Assert.DoesNotContain("Səsverən", text);
            Assert.DoesNotContain("sınaq", text);
            Assert.DoesNotContain("rank", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("points", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("average", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_work_off_the_roster_is_not_there_for_a_juror()
    {
        var (admin, id, w, ineligible) = await ScenarioAsync();
        await Json(await admin.PostAsync(Url(id), null));
        var e = await Json(await admin.PostAsync(Url(id, "/roster/freeze"), null));
        var juror = await JurorAsync(admin, id, e.GetProperty("jurors")[0].GetProperty("jurorId").GetInt32());
        foreach (var x in ineligible)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await juror.GetAsync($"/api/jury/works/{x.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ScoreAsync(juror, x.Id, 5)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await juror.GetAsync($"/api/jury/works/{w[0].Id}/files/999999/thumb")).StatusCode);
        Assert.False(await _factory.QueryDbAsync(db => db.JuryWorkScores.AnyAsync()));
    }

    // ---- Scores ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_score_is_a_whole_number_from_1_to_10_and_the_database_holds_the_same_rule()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        Assert.Equal(1, (await Json(await ScoreAsync(juror, w[0].Id, 1))).GetProperty("score").GetInt32());
        Assert.Equal(10, (await Json(await ScoreAsync(juror, w[1].Id, 10))).GetProperty("score").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await ScoreAsync(juror, w[2].Id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ScoreAsync(juror, w[2].Id, 11)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ScoreAsync(juror, w[2].Id, -3)).StatusCode);
        foreach (var raw in new[] { """{"score":7.5}""", """{"score":"7.5"}""", """{"score":"yeddi"}""", """{"score":1e1}""", "{}" })
        {
            var res = await juror.PutAsync($"/api/jury/works/{w[2].Id}/score", new StringContent(raw, Encoding.UTF8, "application/json"));
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{raw}: {(int)res.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await juror.PutAsJsonAsync($"/api/jury/works/{w[2].Id}/score",
            new { score = 5, comment = new string('a', 1001) })).StatusCode);
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.JuryWorkScores.CountAsync()));

        // Even a write that skipped the API meets the database's own check.
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            var row = await db.JuryWorkScores.FirstAsync();
            row.Score = 11;
            await db.SaveChangesAsync();
        }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, (ex.InnerException as PostgresException)?.SqlState);
    }

    [Fact]
    public async Task Saving_is_idempotent_and_a_save_based_on_an_older_version_is_refused_not_written_over()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var phone = await JurorAsync(admin, id, jurors[0]);

        var first = await Json(await ScoreAsync(phone, w[0].Id, 7));
        var v1 = first.GetProperty("version").GetUInt32();
        // The same score again changes nothing, whatever version it carries.
        Assert.Equal(v1, (await Json(await ScoreAsync(phone, w[0].Id, 7))).GetProperty("version").GetUInt32());
        Assert.Equal(v1, (await Json(await ScoreAsync(phone, w[0].Id, 7, v1))).GetProperty("version").GetUInt32());

        var v2 = (await Json(await ScoreAsync(phone, w[0].Id, 8, v1))).GetProperty("version").GetUInt32();
        Assert.NotEqual(v1, v2);
        // Another save still based on v1 (or on nothing): refused, and the 8 stands.
        Assert.Equal(HttpStatusCode.Conflict, (await ScoreAsync(phone, w[0].Id, 9, v1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ScoreAsync(phone, w[0].Id, 9)).StatusCode);
        var stored = await _factory.QueryDbAsync(db => db.JuryWorkScores.AsNoTracking().SingleAsync());
        Assert.Equal(8, stored.Score);
        Assert.Equal(JuryScoreSource.Online, stored.Source);

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == AuditActions.JuryWorkScored && a.Summary.Contains("— → 7"));
        Assert.Contains(audit, a => a.Action == AuditActions.JuryWorkScored && a.Summary.Contains("7 → 8"));
        Assert.Equal(2, audit.Count(a => a.Action == AuditActions.JuryWorkScored));
    }

    [Fact]
    public async Task Submitting_needs_every_work_locks_the_scores_and_only_an_admin_reopens_them_with_a_reason()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        await ScoreAllAsync(juror, w[..7], 5, 5, 5, 5, 5, 5, 5);
        Assert.Contains("Qalan 1", await ErrorText(await juror.PostAsync("/api/jury/submit", null), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(Url(id, $"/jurors/{jurors[0]}/lock"), null)).StatusCode);

        var last = await Json(await ScoreAsync(juror, w[7].Id, 6));
        var d = await Json(await juror.PostAsync("/api/jury/submit", null));
        Assert.True(d.GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await ScoreAsync(juror, w[7].Id, 9, last.GetProperty("version").GetUInt32())).StatusCode);
        Assert.True((await Json(await juror.GetAsync($"/api/jury/works/{w[7].Id}"))).GetProperty("locked").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(id, $"/jurors/{jurors[0]}/reopen"), new { reason = "qısa" })).StatusCode);
        // Reopening one juror never touches another.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Url(id, $"/jurors/{jurors[1]}/reopen"),
            new { reason = "Bu münsif hələ yekunlaşdırmayıb, açılacaq heç nə yoxdur." })).StatusCode);
        var e = await Json(await admin.PostAsJsonAsync(Url(id, $"/jurors/{jurors[0]}/reopen"),
            new { reason = "Münsif son işin balını yenidən baxmaq istədi." }));
        Assert.Equal("InProgress", e.GetProperty("jurors")[0].GetProperty("status").GetString());
        await Json(await ScoreAsync(juror, w[7].Id, 9, last.GetProperty("version").GetUInt32()));
        Assert.Contains(await AuditAsync(admin, id), a => a.Action == AuditActions.JuryEvaluationReopened
                                                          && a.Summary.Contains("son işin balını"));
    }

    // ---- Paper sheets ---------------------------------------------------------------------------

    [Fact]
    public async Task A_paper_sheet_writes_the_same_records_and_a_conflict_needs_an_explicit_choice_and_a_reason()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        var online = await Json(await ScoreAsync(juror, w[0].Id, 6));
        await Json(await ScoreAsync(juror, w[1].Id, 4));

        object Sheet(params object[] resolutions) => new
        {
            jurorId = jurors[0],
            lines = new object[]
            {
                new { reference = w[0].Reference, score = "8" },       // conflicts with the 6 saved online
                new { reference = w[1].Reference.ToLowerInvariant(), score = " 4 " }, // the same as saved
                new { reference = w[2].Reference, score = "5" },       // new
            },
            resolutions,
            reason = resolutions.Length > 0 ? "Münsif kağız vərəqdə 8 yazıb və imzalayıb." : null,
        };

        var p = await Json(await admin.PostAsJsonAsync(Url(id, "/paper/preview"), Sheet()));
        Assert.Equal(1, p.GetProperty("new").GetInt32());
        Assert.Equal(1, p.GetProperty("same").GetInt32());
        Assert.Equal(1, p.GetProperty("conflicts").GetInt32());
        Assert.Equal(5, p.GetProperty("missing").GetInt32());
        Assert.Equal(["Conflict", "Same", "New"], p.GetProperty("rows").EnumerateArray().Take(3).Select(r => r.GetProperty("status").GetString()));

        // Anything invalid on the sheet: nothing at all is saved.
        var bad = await admin.PostAsJsonAsync(Url(id, "/paper/commit"), new
        {
            jurorId = jurors[0],
            lines = new object[]
            {
                new { reference = w[2].Reference, score = "5" }, new { reference = w[3].Reference, score = "7.5" },
                new { reference = w[4].Reference, score = "11" }, new { reference = "SUB-2099-00010", score = "5" },
            },
        });
        var text = await ErrorText(bad, HttpStatusCode.BadRequest);
        Assert.Contains("7.5", text);
        Assert.Contains("SUB-2099-00010", text);
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.JuryWorkScores.CountAsync()));

        // A conflict without a choice, or a choice without a reason, or a choice made against a score since changed: refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Url(id, "/paper/commit"), Sheet())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(id, "/paper/commit"), new
        {
            jurorId = jurors[0], lines = new object[] { new { reference = w[0].Reference, score = "8" } },
            resolutions = new object[] { new { submissionId = w[0].Id, keep = "paper", seen = 6 } }, reason = "",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Url(id, "/paper/commit"),
            Sheet(new { submissionId = w[0].Id, keep = "paper", seen = 5 }))).StatusCode);
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.JuryWorkScores.CountAsync()));

        var done = await Json(await admin.PostAsJsonAsync(Url(id, "/paper/commit"), Sheet(new { submissionId = w[0].Id, keep = "paper", seen = 6 })));
        Assert.Equal(2, done.GetProperty("saved").GetInt32());
        Assert.Equal(1, done.GetProperty("conflictsResolved").GetInt32());
        Assert.Equal(3, done.GetProperty("scored").GetInt32());

        // One record per juror and work: the online row became the paper's 8, with its source; no second row.
        var rows = await _factory.QueryDbAsync(db => db.JuryWorkScores.AsNoTracking().Where(s => s.JurorId == jurors[0]).ToListAsync());
        Assert.Equal(3, rows.Count);
        Assert.Equal((8, JuryScoreSource.Paper), rows.Where(r => r.SubmissionId == w[0].Id).Select(r => (r.Score, r.Source)).Single());
        Assert.Equal((5, JuryScoreSource.Paper), rows.Where(r => r.SubmissionId == w[2].Id).Select(r => (r.Score, r.Source)).Single());
        Assert.Equal(JuryScoreSource.Online, rows.Single(r => r.SubmissionId == w[1].Id).Source);

        // The juror's screen, still holding the old version, cannot write over the paper's score.
        Assert.Equal(HttpStatusCode.Conflict, (await ScoreAsync(juror, w[0].Id, 6, online.GetProperty("version").GetUInt32())).StatusCode);
        var work = await Json(await juror.GetAsync($"/api/jury/works/{w[0].Id}"));
        Assert.Equal(8, work.GetProperty("score").GetInt32());

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == AuditActions.JuryPaperScoresEntered && a.Summary.Contains("kağız vərəqdə 8"));
        Assert.Contains(audit, a => a.Action == AuditActions.JuryPaperScoresEntered && a.Summary.Contains($"{w[0].Reference} 6→8"));

        // "Keep the saved one" leaves it as it is.
        await Json(await ScoreAsync(juror, w[3].Id, 3));
        await Json(await admin.PostAsJsonAsync(Url(id, "/paper/commit"), new
        {
            jurorId = jurors[0], lines = new object[] { new { reference = w[3].Reference, score = "9" } },
            resolutions = new object[] { new { submissionId = w[3].Id, keep = "existing", seen = 3 } },
            reason = "Onlayn bal sonradır, münsif onu təsdiqlədi.",
        }));
        Assert.Equal(3, await _factory.QueryDbAsync(db => db.JuryWorkScores.Where(s => s.SubmissionId == w[3].Id).Select(s => s.Score).SingleAsync()));
    }

    [Fact]
    public async Task An_admin_can_complete_a_fully_entered_paper_sheet_and_a_locked_juror_takes_no_paper()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var lines = w.Select((x, i) => new { reference = x.Reference, score = (i + 2).ToString() }).ToArray();
        await Json(await admin.PostAsJsonAsync(Url(id, "/paper/commit"), new { jurorId = jurors[2], lines }));
        var e = await Json(await admin.PostAsync(Url(id, $"/jurors/{jurors[2]}/lock"), null));
        var progress = e.GetProperty("jurors")[2];
        Assert.Equal("Submitted", progress.GetProperty("status").GetString());
        Assert.True(progress.GetProperty("submittedByAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(Url(id, "/paper/commit"), new { jurorId = jurors[2], lines })).StatusCode);

        // The juror's own screen shows the paper's scores, locked.
        var juror = await JurorAsync(admin, id, jurors[2]);
        var d = await Json(await juror.GetAsync("/api/jury/dashboard"));
        Assert.True(d.GetProperty("locked").GetBoolean());
        Assert.Equal(Enumerable.Range(2, 8), d.GetProperty("works").EnumerateArray().Select(x => x.GetProperty("score").GetInt32()));
    }

    // ---- The internal ranking --------------------------------------------------------------------

    [Fact]
    public async Task The_ranking_appears_once_every_juror_has_submitted_and_ranks_every_work_by_its_exact_average()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var votes = await VotesFingerprintAsync(id);

        // Not before every juror has submitted: the progress, and no position at all.
        var r = await Json(await admin.GetAsync(Url(id, "/ranking")));
        Assert.False(r.GetProperty("complete").GetBoolean());
        Assert.Empty(r.GetProperty("rows").EnumerateArray());
        Assert.Equal(24, r.GetProperty("expectedScores").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync(Url(id, "/ranking.csv"))).StatusCode);

        await EveryoneScoresAsync(admin, id, w, jurors);
        r = await Json(await admin.GetAsync(Url(id, "/ranking")));
        Assert.True(r.GetProperty("complete").GetBoolean());
        Assert.Equal((3, 3, 24), (r.GetProperty("jurorCount").GetInt32(), r.GetProperty("submittedJurors").GetInt32(), r.GetProperty("completedScores").GetInt32()));
        var rows = r.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(8, rows.Count);
        // Every work once, by the jury alone: w8 is first with one valid vote (and 20 set aside).
        Assert.Equal([w[7], w[3], w[0], w[1], w[2], w[4], w[5], w[6]], rows.Select(x => w.Single(v => v.Id == x.GetProperty("submissionId").GetInt32())));
        Assert.Equal([1, 2, 3, 4, 5, 6, 6, 8], rows.Select(x => x.GetProperty("position").GetInt32()));
        Assert.Equal([9.6667m, 9m, 8.6667m, 8m, 7.6667m, 5m, 5m, 1m], rows.Select(x => x.GetProperty("average").GetDecimal()));
        Assert.Equal([29L, 27, 26, 24, 23, 15, 15, 3], rows.Select(x => x.GetProperty("scoreSum").GetInt64()));
        Assert.All(rows, x => Assert.Equal(3, x.GetProperty("scoreCount").GetInt32()));
        // w5 and w6 have the same average: both 6th, both marked as tied, nothing decides between them.
        Assert.Equal([false, false, false, false, false, true, true, false], rows.Select(x => x.GetProperty("tied").GetBoolean()));
        Assert.Equal([1, 1, 1, 1, 1, 2, 2, 1], rows.Select(x => x.GetProperty("tieSize").GetInt32()));
        Assert.Equal(1, r.GetProperty("tieGroups").GetInt32());

        var csv = await admin.GetAsync(Url(id, "/ranking.csv"));
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(9, lines.Length);
        Assert.Equal("\"Position\";\"Reference\";\"Title\";\"Average\";\"ScoreSum\";\"Scores\";\"TiedWorks\"", lines[0]);
        Assert.Equal($"\"1\";\"{w[7].Reference}\";\"{w[7].Title}\";\"9.6667\";\"29\";\"3\";\"\"", lines[1]);
        Assert.Equal($"\"6\";\"{w[4].Reference}\";\"{w[4].Title}\";\"5.0000\";\"15\";\"3\";\"2\"", lines[6]);

        // Scoring changed nothing of the vote.
        Assert.Equal(votes, await VotesFingerprintAsync(id));

        // Reopening one juror takes the ranking back until they submit again.
        await Json(await admin.PostAsJsonAsync(Url(id, $"/jurors/{jurors[1]}/reopen"), new { reason = "Münsif bir balı yenidən baxmaq istədi." }));
        Assert.False((await Json(await admin.GetAsync(Url(id, "/ranking")))).GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task The_ranking_and_every_mark_stay_internal()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var clients = await EveryoneScoresAsync(admin, id, w, jurors);

        // Admins only: not anonymous, not a participant, not a juror.
        var anonymous = _factory.CreateClient();
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        foreach (var path in new[] { "/ranking", "/ranking.csv", "/matrix", "/export.csv" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(id, path))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync(Url(id, path))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await clients[0].GetAsync(Url(id, path))).StatusCode);
        }

        // A juror's own screen: their scores and the public vote only — never an average, the jury's position or tie, or
        // another juror.
        foreach (var path in new[] { "/api/jury/dashboard", $"/api/jury/works/{w[7].Id}" })
        {
            var mine = await (await clients[0].GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("average", mine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scoreSum", mine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tied", mine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("9.6667", mine);
            Assert.DoesNotContain("Rauf", mine);
        }

        // No public page or endpoint knows the jury: no awards route, no results, no jury word on the public answers.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Slug}/awards")).StatusCode);
        var page = await Json(await anonymous.GetAsync($"/api/competitions/{Slug}"));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("resultsPublishedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Slug}/results")).StatusCode);
        foreach (var path in new[] { $"/api/competitions/{Slug}", $"/api/competitions/{Slug}/voting", "/api/competitions", "/api/results" })
        {
            var text = await (await anonymous.GetAsync(path)).Content.ReadAsStringAsync();
            var at = text.IndexOf("jury", StringComparison.OrdinalIgnoreCase);
            Assert.True(at < 0, $"{path}: …{(at < 0 ? "" : text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 60)])}…");
            Assert.DoesNotContain("9.6667", text);
        }
    }

    [Fact]
    public async Task The_old_50_50_final_is_closed_but_the_public_votes_own_result_can_still_be_published()
    {
        var (admin, id, w, _) = await ScenarioAsync();
        var final = $"/api/admin/competitions/{id}/final";
        await Json(await admin.GetAsync(final));
        await Json(await admin.PostAsync(Url(id), null));

        // The combined final's record stays readable, as history; its formula no longer runs.
        await Json(await admin.GetAsync(final));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"{final}/marks", new { jurorId = 1, marks = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{final}/lock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync($"{final}/show")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{final}/publish", new { confirm = true })).StatusCode);
        // The frozen public Top 10 is not re-frozen from under the jury.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"{final}/freeze", new { auditCompleted = true, prizes = new[] { 250, 150, 100 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{final}/jurors", null)).StatusCode);

        // The public vote's own result is the owner's to publish as before — the jury does not stand in its way.
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/result/places",
            new { places = new int?[] { w[0].Id, w[1].Id, w[2].Id }, current = new int?[] { null, null, null }, confirmPublished = false }));
        await Json(await admin.PostAsync($"/api/admin/competitions/{id}/result/publish", null));
        var result = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Slug}/results"));
        var raw = result.GetRawText();
        var hit = raw.IndexOf("jury", StringComparison.OrdinalIgnoreCase);
        Assert.True(hit < 0, hit < 0 ? "" : raw[Math.Max(0, hit - 80)..Math.Min(raw.Length, hit + 80)]);
        Assert.Equal(FinalStage.Frozen, await _factory.QueryDbAsync(db => db.CompetitionFinals.Where(f => f.CompetitionId == id).Select(f => f.Stage).SingleAsync()));
    }

    // ---- Matrix, CSV, print ---------------------------------------------------------------------

    [Fact]
    public async Task The_matrix_and_its_csv_show_every_jurors_score_and_averages_only_when_complete()
    {
        var (admin, id, w, jurors) = await ReadyAsync();
        var juror = await JurorAsync(admin, id, jurors[0]);
        await Json(await ScoreAsync(juror, w[0].Id, 7, comment: "Yaxşı rəng həlli"));
        var m = await Json(await admin.GetAsync(Url(id, "/matrix")));
        Assert.False(m.GetProperty("complete").GetBoolean());
        var row = m.GetProperty("rows")[0];
        Assert.Equal([7, (int?)null, null], row.GetProperty("scores").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (int?)null : x.GetInt32()));
        Assert.True(row.GetProperty("hasComment")[0].GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("average").ValueKind);

        await juror.PostAsync("/api/jury/sign-out", null);
        await EveryoneScoresAsync(admin, id, w, jurors);
        m = await Json(await admin.GetAsync(Url(id, "/matrix")));
        Assert.True(m.GetProperty("complete").GetBoolean());
        Assert.Equal(26m / 3m, m.GetProperty("rows")[0].GetProperty("average").GetDecimal(), 4);

        var csv = await admin.GetAsync(Url(id, "/export.csv"));
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(9, lines.Length);
        Assert.Contains("\"Aysel Məmmədova\"", lines[0]);
        // The first column is the row's place in the printed catalog, not a rank of any kind.
        Assert.StartsWith("\"CatalogNo\";\"Reference\";\"Title\"", lines[0]);
        Assert.StartsWith($"\"1\";\"{w[0].Reference}\"", lines[1]);
        Assert.Contains("\"8.6667\"", lines[1]);
    }
}
