using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The jury final (D13) end to end: the ten finalists frozen from the valid vote after the audit, the jury's marks
/// entered blind to the vote, locked, combined with the public points, a tie at a prize settled only by the jury's
/// recorded order, the show's data read any number of times, and the official places written only by the explicit
/// publish. Votes are never touched.
/// </summary>
public sealed class FinalTests : IDisposable
{
    private const string Slug = "emircan-final";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Work(int Id, string Reference, string Title, string Author);

    /// <summary>
    /// Twelve works in one "Ümumi" nomination. Valid votes 20, 18, 15, 15, 12, 10, 8, 6, 5, 4, 2, 1; the first has
    /// three votes set aside, the eleventh five — counted, the eleventh would be in the ten.
    /// </summary>
    private static readonly int[] DefaultValid = [20, 18, 15, 15, 12, 10, 8, 6, 5, 4, 2, 1];

    private async Task<(HttpClient Admin, int Competition, Work[] Works)> ScenarioAsync(int[]? valid = null, bool votingOpen = false)
    {
        valid ??= DefaultValid;
        int[] invalid = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0];
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Əmircan final sınağı", slug = Slug, shortDescription = "Sınaq", hasCategories = false,
        }));
        var id = c.GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var works = new List<Work>();
        var now = _factory.Clock.Now;
        await _factory.WithDbAsync(async db =>
        {
            var umumi = new CompetitionCategory { CompetitionId = id, Name = "Ümumi", Slug = "umumi", IsActive = true };
            db.CompetitionCategories.Add(umumi);
            var competition = await db.Competitions.FirstAsync(x => x.Id == id);
            competition.VotingStartsAt = now.AddDays(-3);
            competition.VotingEndsAt = votingOpen ? now.AddHours(1) : now.AddHours(-1);
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

            var submissions = new List<Submission>();
            for (var i = 0; i < valid.Length; i++)
            {
                var author = User($"Müəllif{i + 1}", "Soyadlı");
                var s = new Submission
                {
                    UserId = author.Id, CompetitionId = id, CompetitionCategoryId = umumi.Id, Title = $"Əsər {i + 1}",
                    Status = SubmissionStatus.Submitted, ReferenceNumber = $"SUB-2099-{i + 1:00000}",
                    SubmittedAt = now.AddDays(-5), CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-5),
                };
                db.Submissions.Add(s);
                submissions.Add(s);
                works.Add(new Work(0, s.ReferenceNumber, s.Title, $"Müəllif{i + 1} Soyadlı"));
            }
            await db.SaveChangesAsync();
            for (var i = 0; i < valid.Length; i++)
            {
                works[i] = works[i] with { Id = submissions[i].Id };
                for (var v = 0; v < valid[i] + invalid[i]; v++)
                {
                    var voter = User("Səsverən", $"S{i}-{v}");
                    db.Votes.Add(new Vote
                    {
                        UserId = voter.Id, SubmissionId = submissions[i].Id, CompetitionCategoryId = umumi.Id,
                        CreatedAt = now.AddDays(-2),
                        InvalidatedAt = v < invalid[i] ? now.AddDays(-1) : null,
                        InvalidationReason = v < invalid[i] ? "sınaq" : null,
                    });
                }
            }
            await db.SaveChangesAsync();
        });
        return (admin, id, works.ToArray());
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

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

    private static string Url(int id, string path = "") => $"/api/admin/competitions/{id}/final{path}";

    private static Task<HttpResponseMessage> FreezeAsync(HttpClient admin, int id, bool audit = true) =>
        admin.PostAsJsonAsync(Url(id, "/freeze"), new { auditCompleted = audit, prizes = new[] { 250, 150, 100 } });

    private static Task<HttpResponseMessage> MarksAsync(HttpClient admin, int id, int jurorId, params object[] marks) =>
        admin.PutAsJsonAsync(Url(id, "/marks"), new { jurorId, marks });

    /// <summary>A juror's three marks adding up to <paramref name="total"/> (0–30, in halves).</summary>
    private static object Sheet(int submissionId, decimal total)
    {
        var idea = Math.Min(10m, total);
        var execution = Math.Min(10m, total - idea);
        return new { submissionId, idea, execution, relevance = total - idea - execution };
    }

    /// <summary>Both jurors mark every work, the totals given per work (each juror half of it, the first a half more).</summary>
    private static async Task MarkAllAsync(HttpClient admin, int id, IReadOnlyDictionary<int, decimal> jury)
    {
        var f = await Json(await admin.GetAsync(Url(id)));
        var jurors = f.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToArray();
        await Json(await MarksAsync(admin, id, jurors[0], jury.Select(x => Sheet(x.Key, x.Value / 2 + 0.5m)).ToArray()));
        await Json(await MarksAsync(admin, id, jurors[1], jury.Select(x => Sheet(x.Key, x.Value / 2 - 0.5m)).ToArray()));
    }

    private Task<string> VotesFingerprintAsync(int competitionId) =>
        _factory.QueryDbAsync(db => db.Database.SqlQuery<string>($"""
            SELECT md5(string_agg(concat_ws('|', v."Id", v."SubmissionId", v."InvalidatedAt" IS NULL), ';' ORDER BY v."Id")) AS "Value"
            FROM "Votes" v JOIN "Submissions" s ON s."Id" = v."SubmissionId" WHERE s."CompetitionId" = {competitionId}
            """).SingleAsync());

    private static async Task<List<(string Action, string Summary)>> AuditAsync(HttpClient admin, int competitionId) =>
        (await Json(await admin.GetAsync($"/api/admin/audit?entityType=Competition&entityId={competitionId}"))).EnumerateArray()
        .Select(x => (x.GetProperty("action").GetString()!, x.GetProperty("summary").GetString() ?? "")).ToList();

    private static Dictionary<int, decimal> Jury(Work[] w, params decimal[] totals) =>
        totals.Select((t, i) => (w[i].Id, t)).ToDictionary(x => x.Id, x => x.t);

    // ---- Freezing -------------------------------------------------------------------------------

    [Fact]
    public async Task The_ten_are_frozen_from_valid_votes_only_after_the_vote_and_the_audit_and_do_not_follow_later_changes()
    {
        var (admin, id, w) = await ScenarioAsync();
        var before = await VotesFingerprintAsync(id);

        // Before freezing the admin sees the ten that would be taken, with the margin at the line.
        var preview = (await Json(await admin.GetAsync(Url(id)))).GetProperty("preview");
        Assert.Equal(w.Take(10).Select(x => x.Reference),
            preview.GetProperty("top").EnumerateArray().Select(x => x.GetProperty("reference").GetString()));
        Assert.Equal(w[10].Reference, preview.GetProperty("next").GetProperty("reference").GetString());
        Assert.Equal(2, preview.GetProperty("next").GetProperty("validVotes").GetInt32());
        Assert.Equal(before, preview.GetProperty("votesFingerprint").GetString());
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());

        // Only on the admin's confirmation that the audit is complete.
        Assert.Contains("audit", await ErrorText(await FreezeAsync(admin, id, audit: false), HttpStatusCode.BadRequest));

        var f = await Json(await FreezeAsync(admin, id));
        Assert.Equal("Frozen", f.GetProperty("stage").GetString());
        Assert.Equal(116, f.GetProperty("frozenValidVotes").GetInt32());
        Assert.Equal(8, f.GetProperty("frozenInvalidatedVotes").GetInt32());
        // The fingerprint is the operator's own SQL, character for character.
        Assert.Equal(before, f.GetProperty("frozenVotesFingerprint").GetString());
        Assert.Equal([250, 150, 100], f.GetProperty("prizes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(2, f.GetProperty("jurors").GetArrayLength());

        var stored = await _factory.QueryDbAsync(db => db.CompetitionFinalists.AsNoTracking()
            .Where(x => x.CompetitionId == id).OrderBy(x => x.PublicRank).ThenBy(x => x.SubmissionId)
            .Select(x => new { x.SubmissionId, x.ValidVotes, x.PublicRank, x.PublicPoints }).ToListAsync());
        Assert.Equal(10, stored.Count);
        // Level on 15 votes, the third and fourth share third place and its 8 points; the tenth has 1 point.
        Assert.Equal(new[] { (20, 1, 10), (18, 2, 9), (15, 3, 8), (15, 3, 8), (12, 5, 6), (10, 6, 5), (8, 7, 4), (6, 8, 3), (5, 9, 2), (4, 10, 1) },
            stored.Select(x => (x.ValidVotes, x.PublicRank, x.PublicPoints)));
        // The eleventh, with 2 valid and 5 set-aside votes, is not in it: set-aside votes count nowhere.
        Assert.DoesNotContain(stored, x => x.SubmissionId == w[10].Id);

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == "FinalistsFrozen" && a.Summary.Contains($"votes md5 {before}")
                                    && a.Summary.Contains($"1 {w[0].Reference} 20") && a.Summary.Contains("valid 116, invalidated 8"));

        // A vote set aside after freezing changes nothing in the final; the screen says the votes have moved.
        await _factory.WithDbAsync(async db =>
        {
            var vote = await db.Votes.FirstAsync(v => v.SubmissionId == w[1].Id);
            vote.InvalidatedAt = _factory.Clock.Now;
            vote.InvalidationReason = "sınaq";
            await db.SaveChangesAsync();
        });
        f = await Json(await admin.GetAsync(Url(id)));
        Assert.True(f.GetProperty("votesChangedSinceFreeze").GetBoolean());
        Assert.Equal(18, await _factory.QueryDbAsync(db =>
            db.CompetitionFinalists.Where(x => x.SubmissionId == w[1].Id).Select(x => x.ValidVotes).SingleAsync()));

        // Freezing writes no result.
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionResults.AnyAsync(r => r.CompetitionId == id)));
    }

    [Fact]
    public async Task A_refreeze_names_why_and_keeps_the_superseded_ten_in_the_audit()
    {
        var (admin, id, w) = await ScenarioAsync();
        await Json(await FreezeAsync(admin, id));
        var jurorsBefore = (await Json(await admin.GetAsync(Url(id)))).GetProperty("jurors").GetRawText();

        // Votes set aside after the freeze under a newly approved rule: the first work keeps 7 of its 20.
        await _factory.WithDbAsync(async db =>
        {
            foreach (var v in await db.Votes.Where(v => v.SubmissionId == w[0].Id && v.InvalidatedAt == null).OrderBy(v => v.Id).Take(13).ToListAsync())
            {
                v.InvalidatedAt = _factory.Clock.Now;
                v.InvalidationReason = "D14_DISPOSABLE_EMAIL v1: sınaq";
            }
            await db.SaveChangesAsync();
        });

        // Without saying why, the frozen ten stay.
        var prizes = new[] { 250, 150, 100 };
        Assert.Contains("niyə əvəz", await ErrorText(
            await admin.PostAsJsonAsync(Url(id, "/freeze"), new { auditCompleted = true, prizes }), HttpStatusCode.BadRequest));
        Assert.Equal(20, await _factory.QueryDbAsync(db =>
            db.CompetitionFinalists.Where(x => x.SubmissionId == w[0].Id).Select(x => x.ValidVotes).SingleAsync()));

        const string why = "Post-close integrity audit approved a new competition-wide disposable-email invalidation rule before jury scoring began.";
        var f = await Json(await admin.PostAsJsonAsync(Url(id, "/freeze"), new { auditCompleted = true, prizes, supersedeReason = why }));
        Assert.Equal("Frozen", f.GetProperty("stage").GetString());
        Assert.False(f.GetProperty("votesChangedSinceFreeze").GetBoolean());
        Assert.Equal(103, f.GetProperty("frozenValidVotes").GetInt32());
        var stored = await _factory.QueryDbAsync(db => db.CompetitionFinalists.AsNoTracking()
            .Where(x => x.CompetitionId == id).OrderBy(x => x.PublicRank).ThenBy(x => x.SubmissionId)
            .Select(x => new { x.SubmissionId, x.ValidVotes, x.PublicRank, x.PublicPoints }).ToListAsync());
        Assert.Equal(new[] { (18, 1, 10), (15, 2, 9), (15, 2, 9), (12, 4, 7), (10, 5, 6), (8, 6, 5), (7, 7, 4), (6, 8, 3), (5, 9, 2), (4, 10, 1) },
            stored.Select(x => (x.ValidVotes, x.PublicRank, x.PublicPoints)));
        Assert.Equal(w[0].Id, stored[6].SubmissionId);

        // The old ten stay readable in the audit — every finalist with votes, rank and points — with the reason.
        var audit = await AuditAsync(admin, id);
        var superseded = audit.Where(a => a.Action == "FinalistsSuperseded").Select(a => a.Summary).ToList();
        Assert.Equal(2, superseded.Count);
        Assert.Contains(superseded, x => x.StartsWith("superseded snapshot frozen") && x.Contains("valid 116, invalidated 8") && x.EndsWith($"reason: {why}"));
        Assert.Contains(superseded, x => x.StartsWith("superseded ten: ") && x.Contains($"1 {w[0].Reference} 20 (10 pts)")
                                         && x.Contains($"10 {w[9].Reference} 4 (1 pts)"));
        Assert.All(superseded, x => Assert.True(x.Length <= 500));
        Assert.Contains(audit, a => a.Action == "FinalistsFrozen" && a.Summary.StartsWith("re-frozen") && a.Summary.Contains($"7 {w[0].Reference} 7"));

        // The jurors, the marks and the result are untouched; the jury still sees nothing of the vote.
        Assert.Equal(jurorsBefore, f.GetProperty("jurors").GetRawText());
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.JuryScores.CountAsync(x => x.Idea != null || x.Execution != null || x.Relevance != null)));
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionResults.AnyAsync(r => r.CompetitionId == id)));
        foreach (var leak in new[] { "validVotes", "publicRank", "publicPoints" })
            Assert.DoesNotContain(leak, f.GetProperty("finalists").GetRawText());
    }

    [Fact]
    public async Task Nothing_is_frozen_while_the_vote_is_open_or_when_the_tenth_place_is_level()
    {
        var (admin, id, _) = await ScenarioAsync(votingOpen: true);
        Assert.Contains("bağlanmayıb", await ErrorText(await FreezeAsync(admin, id), HttpStatusCode.BadRequest));
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionFinals.AnyAsync()));
    }

    [Fact]
    public async Task A_level_tenth_place_is_not_cut_by_the_machine()
    {
        var (admin, id, _) = await ScenarioAsync([20, 18, 15, 15, 12, 10, 8, 6, 5, 4, 4, 1]);
        var preview = (await Json(await admin.GetAsync(Url(id)))).GetProperty("preview");
        Assert.True(preview.GetProperty("boundaryTie").GetBoolean());
        Assert.Contains("bərabər səsdədir", await ErrorText(await FreezeAsync(admin, id), HttpStatusCode.BadRequest));
    }

    // ---- The jury -------------------------------------------------------------------------------

    [Fact]
    public async Task A_juror_takes_a_name_for_the_admin_alone_and_the_jury_rows_name_the_author_but_never_the_vote()
    {
        var (admin, id, w) = await ScenarioAsync();
        var f = await Json(await FreezeAsync(admin, id));

        // The admin's rows are internal: each finalist with its author — and still nothing of the vote.
        var rows = f.GetProperty("finalists");
        Assert.Equal(w[0].Author, rows.EnumerateArray().Single(x => x.GetProperty("submissionId").GetInt32() == w[0].Id)
            .GetProperty("author").GetString());
        foreach (var leak in new[] { "validVotes", "publicRank", "publicPoints", "finalScore" })
            Assert.DoesNotContain(leak, rows.GetRawText());

        // A juror's own name, tidied; the other juror keeps theirs; marks stay with the juror, not the name.
        var jurors = f.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToArray();
        await Json(await MarksAsync(admin, id, jurors[0], Sheet(w[0].Id, 21m)));
        f = await Json(await admin.PutAsJsonAsync(Url(id, $"/jurors/{jurors[0]}"), new { label = "  Aysel   Məmmədova " }));
        Assert.Equal(["Aysel Məmmədova", "Münsif 2"], f.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("label").GetString()));
        Assert.Equal(21m, f.GetProperty("finalists").EnumerateArray().Single(x => x.GetProperty("submissionId").GetInt32() == w[0].Id)
            .GetProperty("marks").EnumerateArray().Single(m => m.GetProperty("jurorId").GetInt32() == jurors[0]).GetProperty("subtotal").GetDecimal());

        // Empty, too long, or another juror's name (in any case): refused.
        foreach (var bad in new[] { "   ", new string('a', 61) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Url(id, $"/jurors/{jurors[1]}"), new { label = bad })).StatusCode);
        Assert.Contains("artıq var", await ErrorText(
            await admin.PutAsJsonAsync(Url(id, $"/jurors/{jurors[1]}"), new { label = "aysel məmmədova" }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(Url(id, "/jurors/999999"), new { label = "X" })).StatusCode);
        Assert.Contains(("FinalJurorRenamed", "Münsif 1 → Aysel Məmmədova"), await AuditAsync(admin, id));

        // Named or not, a juror never reaches the show or a public page.
        await MarkAllAsync(admin, id, Jury(w, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21));
        await Json(await admin.PostAsync(Url(id, "/lock"), null));
        var show = await admin.GetStringAsync(Url(id, "/show"));
        foreach (var leak in new[] { "Aysel", "Məmmədova", "Münsif 2", "Müəllif" })
            Assert.DoesNotContain(leak, show);
    }

    [Fact]
    public async Task The_jury_screens_carry_nothing_of_the_vote_and_marks_are_validated_locked_and_audited()
    {
        var (admin, id, w) = await ScenarioAsync();
        var f = await Json(await FreezeAsync(admin, id));

        // Blind: in reference order, with no votes, no public rank, no points, no calculation.
        foreach (var leak in new[] { "validVotes", "publicRank", "publicPoints", "finalScore", "juryRank" })
            Assert.DoesNotContain(leak, f.GetProperty("finalists").GetRawText());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("calculation").ValueKind);
        Assert.Equal(w.Take(10).Select(x => x.Reference).Order(StringComparer.Ordinal),
            f.GetProperty("finalists").EnumerateArray().Select(x => x.GetProperty("reference").GetString()));
        Assert.Equal(60, f.GetProperty("missingMarks").GetInt32()); // 10 works × 2 jurors × 3 criteria
        Assert.Contains("kilidləndikdən sonra", await ErrorText(await admin.GetAsync(Url(id, "/show")), HttpStatusCode.Conflict));

        var jurors = f.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToArray();
        // 0–10 in halves; nothing for a work outside the ten.
        foreach (var bad in new[] { 10.5m, -1m, 7.25m })
            Assert.Contains("0,5 addımla", await ErrorText(await MarksAsync(admin, id, jurors[0],
                new { submissionId = w[0].Id, idea = bad, execution = 5m, relevance = 5m }), HttpStatusCode.BadRequest));
        Assert.Contains("finalçı deyil", await ErrorText(await MarksAsync(admin, id, jurors[0],
            new { submissionId = w[10].Id, idea = 5m, execution = 5m, relevance = 5m }), HttpStatusCode.BadRequest));

        // A partial sheet is kept; the lock waits for every mark.
        f = await Json(await MarksAsync(admin, id, jurors[0], new { submissionId = w[0].Id, idea = 7.5m, execution = (decimal?)null, relevance = 9m }));
        Assert.Equal(58, f.GetProperty("missingMarks").GetInt32());
        Assert.Contains("hələ daxil edilməyib", await ErrorText(await admin.PostAsync(Url(id, "/lock"), null), HttpStatusCode.BadRequest));

        // Once marks exist the ten cannot be frozen again.
        Assert.Contains("yenidən", await ErrorText(await FreezeAsync(admin, id), HttpStatusCode.Conflict));

        // Several jurors add up: a third juror, then everybody marks everything.
        f = await Json(await admin.PostAsync(Url(id, "/jurors"), null));
        jurors = f.GetProperty("jurors").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(3, jurors.Length);
        foreach (var juror in jurors)
            await Json(await MarksAsync(admin, id, juror, w.Take(10).Select(x => Sheet(x.Id, 20m)).ToArray()));
        f = await Json(await MarksAsync(admin, id, jurors[2], Sheet(w[0].Id, 27.5m)));
        var first = f.GetProperty("finalists").EnumerateArray().Single(x => x.GetProperty("submissionId").GetInt32() == w[0].Id);
        Assert.Equal(67.5m, first.GetProperty("juryTotal").GetDecimal());
        Assert.Equal([20m, 20m, 27.5m], first.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("subtotal").GetDecimal()));
        Assert.Equal(0, f.GetProperty("missingMarks").GetInt32());

        f = await Json(await admin.PostAsync(Url(id, "/lock"), null));
        Assert.Equal("JuryLocked", f.GetProperty("stage").GetString());
        Assert.Equal(90m, f.GetProperty("calculation").GetProperty("juryMax").GetDecimal());

        // Locked marks cannot change quietly; unlocking needs a reason and is audited.
        Assert.Contains("kilidlənib", await ErrorText(await MarksAsync(admin, id, jurors[0], Sheet(w[0].Id, 1m)), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(id, "/unlock"), new { reason = "" })).StatusCode);
        f = await Json(await admin.PostAsJsonAsync(Url(id, "/unlock"), new { reason = "Münsif 2 vərəqində səhv" }));
        Assert.Equal("Frozen", f.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, f.GetProperty("calculation").ValueKind);

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == "JuryScoresChanged" && a.Summary.Contains($"Münsif 1 · {w[0].Reference}: İdeya —→7.5"));
        Assert.Contains(audit, a => a.Action == "JuryScoresChanged" && a.Summary.Contains("Vizual / icra —→10"));
        Assert.Contains(audit, a => a.Action == "FinalJurorAdded" && a.Summary == "Münsif 3");
        Assert.Contains(audit, a => a.Action == "JuryLocked" && a.Summary.Contains($"{w[0].Reference} 67.5"));
        Assert.Contains(audit, a => a.Action == "JuryUnlocked" && a.Summary.Contains("Münsif 2 vərəqində səhv"));
    }

    // ---- The result -----------------------------------------------------------------------------

    [Fact]
    public async Task Points_combine_into_places_the_show_changes_nothing_and_only_the_publish_writes_the_result()
    {
        var (admin, id, w) = await ScenarioAsync();
        var votes = await VotesFingerprintAsync(id);
        await Json(await FreezeAsync(admin, id));
        // Public points 10, 9, 8, 8, 6, 5, 4, 3, 2, 1; jury totals below give jury ranks 6, 2, 3, 4, 1, 8, 5, 7, 10, 9.
        await MarkAllAsync(admin, id, Jury(w, 30, 50, 45, 40, 55, 20, 35, 25, 10, 15));
        var f = await Json(await admin.PostAsync(Url(id, "/lock"), null));

        var rows = f.GetProperty("calculation").GetProperty("rows").EnumerateArray().ToList();
        // Finals: 2nd 9+9=18; 5th 6+10=16 and 3rd 8+8=16 (jury 55 > 45); 1st 10+5=15 and 4th 8+7=15 (jury 40 > 30); ...
        Assert.Equal(new[] { w[1], w[4], w[2], w[3], w[0], w[6], w[5], w[7], w[9], w[8] }.Select(x => x.Reference),
            rows.Select(r => r.GetProperty("reference").GetString()));
        Assert.Equal([18, 16, 16, 15, 15, 10, 8, 7, 3, 3], rows.Select(r => r.GetProperty("finalScore").GetInt32()));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], rows.Select(r => r.GetProperty("place").GetInt32()));
        Assert.Equal([250, 150, 100], rows.Take(3).Select(r => r.GetProperty("prizeAzn").GetInt32()));
        Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("prizeAzn").ValueKind);
        Assert.Empty(f.GetProperty("calculation").GetProperty("publishProblems").EnumerateArray());

        // The show: every number, no author, no voter, no juror; read twice, nothing moves.
        var auditCount = (await AuditAsync(admin, id)).Count;
        var show = await admin.GetStringAsync(Url(id, "/show"));
        Assert.Equal(show, await admin.GetStringAsync(Url(id, "/show")));
        foreach (var leak in new[] { "Müəllif", "Soyadlı", "Səsverən", "@", "Münsif 1", "email" })
            Assert.DoesNotContain(leak, show);
        var shown = JsonDocument.Parse(show).RootElement;
        Assert.True(shown.GetProperty("prizesDecided").GetBoolean());
        Assert.Equal(60m, shown.GetProperty("juryMax").GetDecimal());
        var winner = shown.GetProperty("works").EnumerateArray().Single(x => x.GetProperty("place").GetInt32() == 1);
        Assert.Equal((w[1].Id, 18, 9, 9, 50m, 250), (winner.GetProperty("submissionId").GetInt32(), winner.GetProperty("validVotes").GetInt32(),
            winner.GetProperty("publicPoints").GetInt32(), winner.GetProperty("juryPoints").GetInt32(),
            winner.GetProperty("juryTotal").GetDecimal(), winner.GetProperty("prizeAzn").GetInt32()));
        Assert.Equal(auditCount, (await AuditAsync(admin, id)).Count);
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionResults.AnyAsync(r => r.CompetitionId == id)));

        // The publish: only on explicit confirmation, after entries are closed.
        Assert.Contains("təsdiqləyin", await ErrorText(await admin.PostAsJsonAsync(Url(id, "/publish"), new { confirm = false }), HttpStatusCode.BadRequest));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Closed" }));
        f = await Json(await admin.PostAsJsonAsync(Url(id, "/publish"), new { confirm = true }));
        Assert.Equal("Published", f.GetProperty("stage").GetString());

        var result = await _factory.QueryDbAsync(db => db.CompetitionResults.AsNoTracking().SingleAsync(r => r.CompetitionId == id));
        Assert.Equal((w[1].Id, w[4].Id, w[2].Id), (result.OverallWinnerSubmissionId!.Value, result.SecondPlaceSubmissionId!.Value, result.ThirdPlaceSubmissionId!.Value));
        Assert.NotNull(result.PublishedAt);

        // The public result: authors named as before, and for each place how it was reached — no juror named.
        var anonymous = _factory.CreateClient();
        var pub = await Json(await anonymous.GetAsync($"/api/competitions/{Slug}/results"));
        Assert.Equal(w[1].Author, pub.GetProperty("places")[0].GetProperty("winnerName").GetString());
        var final = pub.GetProperty("final");
        Assert.Equal((2, 60m), (final.GetProperty("jurorCount").GetInt32(), final.GetProperty("juryMax").GetDecimal()));
        var places = final.GetProperty("places").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], places.Select(p => p.GetProperty("place").GetInt32()));
        Assert.Equal((w[1].Reference, 18, 2, 9, 50m, 2, 9, 18), (places[0].GetProperty("reference").GetString(),
            places[0].GetProperty("validVotes").GetInt32(), places[0].GetProperty("publicRank").GetInt32(),
            places[0].GetProperty("publicPoints").GetInt32(), places[0].GetProperty("juryTotal").GetDecimal(),
            places[0].GetProperty("juryRank").GetInt32(), places[0].GetProperty("juryPoints").GetInt32(),
            places[0].GetProperty("finalScore").GetInt32()));
        Assert.DoesNotContain("Münsif", pub.GetRawText());

        // After the publish nothing in the final moves.
        var jurorId = f.GetProperty("jurors")[0].GetProperty("id").GetInt32();
        Assert.Contains("dərc olunub", await ErrorText(await MarksAsync(admin, id, jurorId, Sheet(w[0].Id, 1m)), HttpStatusCode.Conflict));
        Assert.Contains("dərc olunub", await ErrorText(await admin.PostAsJsonAsync(Url(id, "/unlock"), new { reason = "sınaq üçün" }), HttpStatusCode.Conflict));
        Assert.Equal(show, await admin.GetStringAsync(Url(id, "/show")));

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == "FinalPublished" && a.Summary.Contains($"1 {w[1].Reference} 18 (9+9)")
                                    && a.Summary.Contains("250/150/100 AZN"));
        Assert.Contains(audit, a => a.Action == "ResultsPublished");
        // Not one vote changed on the way.
        Assert.Equal(votes, await VotesFingerprintAsync(id));
    }

    [Fact]
    public async Task A_tie_at_a_prize_blocks_the_top_three_and_the_publish_until_the_jury_decides()
    {
        var (admin, id, w) = await ScenarioAsync();
        await Json(await FreezeAsync(admin, id));
        // The third and fourth work (15 votes each, 8 public points) get the same jury total: level on everything.
        await MarkAllAsync(admin, id, Jury(w, 25, 50, 45, 45, 30, 12, 20, 15, 5, 10));
        var f = await Json(await admin.PostAsync(Url(id, "/lock"), null));

        var calc = f.GetProperty("calculation");
        var tie = Assert.Single(calc.GetProperty("unresolvedTies").EnumerateArray());
        Assert.Equal(2, tie.GetProperty("rank").GetInt32());
        Assert.Equal([w[2].Reference, w[3].Reference], tie.GetProperty("works").EnumerateArray().Select(x => x.GetProperty("reference").GetString()));
        Assert.Contains(calc.GetProperty("publishProblems").EnumerateArray(), p => p.GetString()!.Contains("Münsif qərarı tələb olunur"));
        var show = await Json(await admin.GetAsync(Url(id, "/show")));
        Assert.False(show.GetProperty("prizesDecided").GetBoolean());
        Assert.All(show.GetProperty("works").EnumerateArray().Where(x => x.GetProperty("submissionId").GetInt32() == w[2].Id || x.GetProperty("submissionId").GetInt32() == w[3].Id),
            x => Assert.Equal(JsonValueKind.Null, x.GetProperty("place").ValueKind));

        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Closed" }));
        Assert.Contains("Münsif qərarı", await ErrorText(await admin.PostAsJsonAsync(Url(id, "/publish"), new { confirm = true }), HttpStatusCode.BadRequest));
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionResults.AnyAsync(r => r.CompetitionId == id)));

        // Only exactly the level works, and only with the jury's reason.
        Assert.Contains("bərabərlik yoxdur", await ErrorText(await admin.PostAsJsonAsync(Url(id, "/tie-breaks"),
            new { submissionIds = new[] { w[3].Id, w[1].Id }, reason = "Münsiflərin qərarı" }), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Url(id, "/tie-breaks"),
            new { submissionIds = new[] { w[3].Id, w[2].Id }, reason = "" })).StatusCode);
        f = await Json(await admin.PostAsJsonAsync(Url(id, "/tie-breaks"),
            new { submissionIds = new[] { w[3].Id, w[2].Id }, reason = "Münsiflər ideyanı daha güclü saydı" }));
        calc = f.GetProperty("calculation");
        Assert.Empty(calc.GetProperty("unresolvedTies").EnumerateArray());
        var top = calc.GetProperty("rows").EnumerateArray().Take(3)
            .Select(r => (r.GetProperty("reference").GetString()!, r.GetProperty("place").GetInt32(), r.GetProperty("tie").GetString()!)).ToList();
        Assert.Equal(new[] { (w[1].Reference, 1, "None"), (w[3].Reference, 2, "DecidedByJury"), (w[2].Reference, 3, "DecidedByJury") }, top);
        Assert.Contains(await AuditAsync(admin, id), a => a.Action == "FinalTieBreakRecorded"
            && a.Summary.Contains($"{w[3].Reference} > {w[2].Reference}; reason: Münsiflər ideyanı daha güclü saydı"));

        await Json(await admin.PostAsJsonAsync(Url(id, "/publish"), new { confirm = true }));
        var result = await _factory.QueryDbAsync(db => db.CompetitionResults.AsNoTracking().SingleAsync(r => r.CompetitionId == id));
        Assert.Equal((w[1].Id, w[3].Id, w[2].Id), (result.OverallWinnerSubmissionId!.Value, result.SecondPlaceSubmissionId!.Value, result.ThirdPlaceSubmissionId!.Value));
    }

    // ---- The public vote on paper ----------------------------------------------------------------

    [Fact]
    public async Task The_public_vote_prints_from_the_frozen_snapshot_and_in_full_from_valid_votes_only_and_writes_nothing()
    {
        // The 5th and 6th level on 10 votes: the full ranking reads 1, 2, 3, 3, 5, 5 … at that point.
        var (admin, id, w) = await ScenarioAsync([20, 18, 15, 15, 10, 10, 8, 6, 5, 4, 2, 1]);
        Assert.Contains("təsdiqlənməyib", await ErrorText(await admin.GetAsync(Url(id, "/print/top10")), HttpStatusCode.Conflict));
        await Json(await FreezeAsync(admin, id));

        // A vote set aside after freezing: the Top 10 print keeps the frozen numbers, the full ranking follows the votes.
        await _factory.WithDbAsync(async db =>
        {
            var vote = await db.Votes.FirstAsync(v => v.SubmissionId == w[0].Id && v.InvalidatedAt == null);
            vote.InvalidatedAt = _factory.Clock.Now;
            vote.InvalidationReason = "sınaq";
            await db.SaveChangesAsync();
        });
        var auditBefore = (await AuditAsync(admin, id)).Count;
        var votesBefore = await VotesFingerprintAsync(id);

        var top = await Json(await admin.GetAsync(Url(id, "/print/top10")));
        Assert.Equal("Top10", top.GetProperty("mode").GetString());
        Assert.Equal((114, 8, 122), (top.GetProperty("validVotes").GetInt32(), top.GetProperty("invalidatedVotes").GetInt32(), top.GetProperty("totalVotes").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, top.GetProperty("frozenAt").ValueKind);
        var rows = top.GetProperty("rows").EnumerateArray()
            .Select(r => (r.GetProperty("rank").GetInt32(), r.GetProperty("item").GetProperty("reference").GetString()!, r.GetProperty("validVotes").GetInt32(), r.GetProperty("publicPoints").GetInt32()))
            .ToList();
        Assert.Equal(10, rows.Count);
        Assert.Equal((1, w[0].Reference, 20, 10), rows[0]); // frozen: still 20, not the 19 that count now
        Assert.Equal(new[] { (5, 6), (5, 6) }, rows.Skip(4).Take(2).Select(r => (r.Item1, r.Item4)));
        Assert.Equal((10, w[9].Reference, 4, 1), rows[9]);

        var full = await Json(await admin.GetAsync(Url(id, "/print/full")));
        Assert.Equal("Full", full.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, full.GetProperty("frozenAt").ValueKind);
        Assert.Equal((113, 9, 122), (full.GetProperty("validVotes").GetInt32(), full.GetProperty("invalidatedVotes").GetInt32(), full.GetProperty("totalVotes").GetInt32()));
        var all = full.GetProperty("rows").EnumerateArray()
            .Select(r => (r.GetProperty("rank").GetInt32(), r.GetProperty("item").GetProperty("reference").GetString()!, r.GetProperty("validVotes").GetInt32()))
            .ToList();
        // Every work on the ballot, by valid votes now: the first has 19, the eleventh only its 2 valid votes (its 5
        // set-aside votes count nowhere), and level works share a rank.
        Assert.Equal(12, all.Count);
        Assert.Equal((1, w[0].Reference, 19), all[0]);
        Assert.Equal(new[] { 1, 2, 3, 3, 5, 5, 7, 8, 9, 10, 11, 12 }, all.Select(r => r.Item1));
        Assert.Equal((11, w[10].Reference, 2), all[10]);
        Assert.All(full.GetProperty("rows").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("publicPoints").ValueKind));

        // No voter, no author, nothing written.
        foreach (var body in new[] { top.GetRawText(), full.GetRawText() })
            foreach (var leak in new[] { "Müəllif", "Soyadlı", "Səsverən", "@", "phone", "email" })
                Assert.DoesNotContain(leak, body);

        // An internal document may name the authors when asked — the work's own author, and still no voter or contact.
        var named = await Json(await admin.GetAsync(Url(id, "/print/full?includeAuthor=true")));
        var first = named.GetProperty("rows")[0].GetProperty("item");
        Assert.Equal((w[0].Reference, w[0].Author), (first.GetProperty("reference").GetString(), first.GetProperty("author").GetString()));
        var namedTop = await Json(await admin.GetAsync(Url(id, "/print/top10?includeAuthor=true")));
        Assert.Equal(w[0].Author, namedTop.GetProperty("rows")[0].GetProperty("item").GetProperty("author").GetString());
        foreach (var leak in new[] { "Səsverən", "@", "phone", "email" })
            Assert.DoesNotContain(leak, named.GetRawText() + namedTop.GetRawText());
        Assert.Equal(auditBefore, (await AuditAsync(admin, id)).Count);
        Assert.Equal(votesBefore, await VotesFingerprintAsync(id));
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionResults.AnyAsync(r => r.CompetitionId == id)));

        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        foreach (var path in new[] { Url(id, "/print/top10"), Url(id, "/print/full") })
            Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_full_ranking_waits_for_the_vote_to_close()
    {
        var (admin, id, _) = await ScenarioAsync(votingOpen: true);
        Assert.Contains("bağlandıqdan sonra", await ErrorText(await admin.GetAsync(Url(id, "/print/full")), HttpStatusCode.Conflict));
    }

    // ---- Print, access ------------------------------------------------------------------------

    [Fact]
    public async Task The_jury_sheets_print_the_ten_without_votes_or_authors_and_only_admins_reach_the_final()
    {
        var (admin, id, w) = await ScenarioAsync();
        Assert.Contains("təsdiqlənməyib", await ErrorText(
            await admin.GetAsync($"/api/admin/submissions/print?competitionId={id}&finalists=true"), HttpStatusCode.Conflict));
        await Json(await FreezeAsync(admin, id));

        var print = await Json(await admin.GetAsync($"/api/admin/submissions/print?competitionId={id}&finalists=true"));
        Assert.Equal(w.Take(10).Select(x => x.Reference).Order(StringComparer.Ordinal),
            print.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("reference").GetString()));
        Assert.All(print.GetProperty("items").EnumerateArray(), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("author").ValueKind));
        foreach (var leak in new[] { "vote", "Vote", "rank", "Soyadlı", "@" })
            Assert.DoesNotContain(leak, print.GetRawText());

        // A participant reaches none of it.
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        foreach (var path in new[] { Url(id), Url(id, "/show") })
            Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url(id, "/show"))).StatusCode);

        // A finalist is not deleted out of the final one work at a time.
        Assert.Contains("finalçısıdır", await ErrorText(await admin.DeleteAsync($"/api/admin/submissions/{w[0].Id}/permanent"), HttpStatusCode.Conflict));
    }
}
