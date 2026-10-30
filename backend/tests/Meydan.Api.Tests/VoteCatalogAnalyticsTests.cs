using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The administration's vote analytics over the catalog of every work: valid votes, public position and the votes set
/// aside by approved audit rules — D12's phone cluster, R2 and D14 — counted from the votes and classified exactly as
/// the public audit classifies them. Admins only. The jury evaluates informed: its catalog, papers and API carry each
/// work's aggregate (public position, valid, set aside) in the same order — never the rules, a voter or the evidence.
/// </summary>
public sealed class VoteCatalogAnalyticsTests : IDisposable
{
    private const string Slug = "emircan-analitika";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Work(int Id, string Reference);

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private static string Url(int id) => $"/api/admin/competitions/{id}/voting/catalog";

    /// <summary>
    /// Five works on the ballot and a rejected one. Valid votes 6, 4, 4, 1, 0. Set aside: w1 two by D14 and one by the
    /// phone rule (three accounts share a phone), w2 one by R2, w5 one by D14. The rejected work has one valid vote.
    /// </summary>
    private async Task<(HttpClient Admin, int Competition, Work[] Works)> ScenarioAsync()
    {
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var id = (await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Səs analitikası sınağı", slug = Slug, shortDescription = "Sınaq", hasCategories = false,
        }))).GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var now = _factory.Clock.Now;
        var works = new List<Work>();
        await _factory.WithDbAsync(async db =>
        {
            var umumi = new CompetitionCategory { CompetitionId = id, Name = "Ümumi", Slug = "umumi", IsActive = true };
            db.CompetitionCategories.Add(umumi);
            var c = await db.Competitions.FirstAsync(x => x.Id == id);
            c.EndDate = now.AddDays(-4);
            c.VotingStartsAt = now.AddDays(-3);
            c.VotingEndsAt = now.AddHours(-1);
            await db.SaveChangesAsync();

            var n = 0;
            ApplicationUser User(string? phone = null)
            {
                var email = $"u-{Guid.NewGuid():N}@example.com";
                var u = new ApplicationUser
                {
                    Id = Guid.NewGuid(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
                    NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, FirstName = "Səsverən", LastName = $"S{++n}",
                    PhoneNumber = phone, SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-10),
                    UpdatedAt = now.AddDays(-10), TermsAcceptedAt = now.AddDays(-10),
                };
                db.Users.Add(u);
                return u;
            }

            Submission Add(int k, SubmissionStatus status)
            {
                var s = new Submission
                {
                    UserId = User().Id, CompetitionId = id, CompetitionCategoryId = umumi.Id, Title = $"Əsər {k}", Status = status,
                    ReferenceNumber = $"SUB-2098-{k:00000}", SubmittedAt = now.AddDays(-5), CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-5),
                };
                db.Submissions.Add(s);
                return s;
            }

            var subs = Enumerable.Range(1, 5).Select(k => Add(k, SubmissionStatus.Submitted)).ToList();
            var rejected = Add(6, SubmissionStatus.Rejected);
            await db.SaveChangesAsync();

            void Vote(Submission s, string? setAsideFor = null, string? phone = null) => db.Votes.Add(new Vote
            {
                UserId = User(phone).Id, SubmissionId = s.Id, CompetitionCategoryId = umumi.Id, CreatedAt = now.AddDays(-2),
                InvalidatedAt = setAsideFor is null ? null : now.AddDays(-1), InvalidationReason = setAsideFor,
            });
            const string D14 = "D14_DISPOSABLE_EMAIL v1: Disposable/temporary email domain used by an account created during the voting window.";
            foreach (var (s, valid) in subs.Zip(new[] { 6, 4, 4, 1, 0 }))
                for (var v = 0; v < valid; v++) Vote(s);
            Vote(subs[0], D14);
            Vote(subs[0], D14);
            // Three accounts on one phone: two still count (on w3), one was set aside (on w1) — the phone rule.
            Vote(subs[0], "D12: təkrarlanan telefon", "+994 50 123 45 67");
            Vote(subs[2], null, "0501234567");
            Vote(subs[2], null, "994501234567");
            Vote(subs[1], "D12: koordinasiyalı hesablar");
            Vote(subs[4], D14);
            Vote(rejected);
            await db.SaveChangesAsync();
            works.AddRange(subs.Select(s => new Work(s.Id, s.ReferenceNumber!)));
        });
        return (admin, id, works.ToArray());
    }

    [Fact]
    public async Task Every_work_carries_its_valid_votes_position_and_votes_set_aside_by_rule_most_valid_first()
    {
        var (admin, id, w) = await ScenarioAsync();
        var c = await Json(await admin.GetAsync(Url(id)));
        var rows = c.GetProperty("works").EnumerateArray().ToList();

        // w3 gained two valid votes on the shared phone: 6, 6, 4, 1, 0 — most valid first, references inside a tie.
        Assert.Equal([w[0].Reference, w[2].Reference, w[1].Reference, w[3].Reference, w[4].Reference],
            rows.Select(r => r.GetProperty("reference").GetString()));
        Assert.Equal([6, 6, 4, 1, 0], rows.Select(r => r.GetProperty("validVotes").GetInt32()));
        Assert.Equal([1, 1, 3, 4, 5], rows.Select(r => r.GetProperty("position").GetInt32()));
        Assert.Equal([2, 2, 1, 1, 1], rows.Select(r => r.GetProperty("tieSize").GetInt32()));

        var first = rows[0].GetProperty("invalidated");
        Assert.Equal((1, 0, 2), (first.GetProperty("duplicatePhone").GetInt32(), first.GetProperty("coordinatedAccounts").GetInt32(),
            first.GetProperty("disposableEmail").GetInt32()));
        Assert.Equal(3, rows[0].GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(1, rows[2].GetProperty("invalidated").GetProperty("coordinatedAccounts").GetInt32());
        Assert.Equal(1, rows[4].GetProperty("invalidated").GetProperty("disposableEmail").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("invalidatedVotes").GetInt32());

        Assert.Equal(17, c.GetProperty("validVotes").GetInt32());
        Assert.Equal(5, c.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(3, c.GetProperty("worksWithInvalidations").GetInt32());
        // The rejected work's vote is counted apart, not shown; nothing is invented as "pending review".
        Assert.Equal(1, c.GetProperty("outsideCatalog").GetInt32());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("pendingReview").ValueKind);
        // No person, phone or e-mail travels with it.
        var text = c.GetRawText();
        Assert.DoesNotContain("@example.com", text);
        Assert.DoesNotContain("1234567", text);
        Assert.DoesNotContain("Səsverən", text);
    }

    [Fact]
    public async Task The_rule_counts_agree_with_the_public_audit()
    {
        var (admin, id, _) = await ScenarioAsync();
        var c = await Json(await admin.GetAsync(Url(id)));
        var audit = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Slug}/voting/audit"));
        var rules = audit.GetProperty("rules").EnumerateArray()
            .ToDictionary(r => r.GetProperty("reason").GetString()!, r => r.GetProperty("votes").GetInt32());
        var mine = c.GetProperty("invalidated");
        Assert.Equal(rules.GetValueOrDefault("DuplicatePhone"), mine.GetProperty("duplicatePhone").GetInt32());
        Assert.Equal(rules.GetValueOrDefault("CoordinatedAccounts"), mine.GetProperty("coordinatedAccounts").GetInt32());
        Assert.Equal(rules.GetValueOrDefault("DisposableEmail"), mine.GetProperty("disposableEmail").GetInt32());
        Assert.Equal(audit.GetProperty("invalidatedVotes").GetInt32(), c.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(audit.GetProperty("validVotes").GetInt32(), c.GetProperty("validVotes").GetInt32() + c.GetProperty("outsideCatalog").GetInt32());
    }

    [Fact]
    public async Task Only_admins_read_the_breakdown_and_the_jury_sees_each_works_aggregate_in_the_same_order()
    {
        var (admin, id, w) = await ScenarioAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Url(id))).StatusCode);
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync(Url(id))).StatusCode);
        Assert.Equal("no-store", (await admin.GetAsync(Url(id))).Headers.CacheControl?.ToString());

        // The jury's side of the same works: print data, the admin's matrix, a juror's list and work.
        await _factory.WithDbAsync(async db =>
        {
            db.CompetitionFinals.Add(new CompetitionFinal
            {
                CompetitionId = id, Stage = FinalStage.Frozen, FrozenAt = _factory.Clock.Now, FrozenVotesFingerprint = "sınaq",
                Jurors = [new FinalJuror { Label = "Münsif A", SortOrder = 1, CreatedAt = _factory.Clock.Now }],
            });
            await db.SaveChangesAsync();
        });
        var jury = $"/api/admin/competitions/{id}/jury-evaluation";
        await Json(await admin.PostAsync(jury, null));
        var e = await Json(await admin.PostAsync($"{jury}/roster/freeze", null));
        var jurorId = e.GetProperty("jurors")[0].GetProperty("jurorId").GetInt32();
        var print = await Json(await admin.GetAsync($"{jury}/print"));
        string?[] order = [w[0].Reference, w[2].Reference, w[1].Reference, w[3].Reference, w[4].Reference];
        static string?[] Refs(JsonElement works) => [.. works.EnumerateArray().Select(x => x.GetProperty("reference").GetString())];
        // The analytics' own order and numbers, without the rules: most valid first, references inside a tie.
        Assert.Equal(order, Refs(print.GetProperty("works")));
        var votes = print.GetProperty("works").EnumerateArray().Select(x => x.GetProperty("vote")).ToList();
        Assert.Equal([1, 1, 3, 4, 5], votes.Select(v => v.GetProperty("position").GetInt32()));
        Assert.Equal([2, 2, 1, 1, 1], votes.Select(v => v.GetProperty("tieSize").GetInt32()));
        Assert.Equal([6, 6, 4, 1, 0], votes.Select(v => v.GetProperty("validVotes").GetInt32()));
        Assert.Equal([3, 0, 1, 0, 1], votes.Select(v => v.GetProperty("invalidatedVotes").GetInt32()));
        // The same place and count the public ranking shows for each work — one rule on every surface.
        var board = (await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Slug}/voting/leaderboard")))
            .GetProperty("entries").EnumerateArray()
            .ToDictionary(x => x.GetProperty("reference").GetString()!, x => (x.GetProperty("rank").GetInt32(), x.GetProperty("voteCount").GetInt32()));
        Assert.All(print.GetProperty("works").EnumerateArray(), x => Assert.Equal(board[x.GetProperty("reference").GetString()!],
            (x.GetProperty("vote").GetProperty("position").GetInt32(), x.GetProperty("vote").GetProperty("validVotes").GetInt32())));
        // The matrix the paper scores are entered from follows the printed catalog.
        Assert.Equal(order, Refs((await Json(await admin.GetAsync($"{jury}/matrix"))).GetProperty("rows")));

        var inv = await Json(await admin.PostAsJsonAsync($"{jury}/jurors/{jurorId}/invitation", new { days = 3 }));
        var link = inv.GetProperty("link").GetString()!;
        var juror = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await Json(await juror.PostAsJsonAsync("/api/jury/sign-in", new { token = link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..], code = inv.GetProperty("code").GetString() }));
        await Json(await juror.PostAsync("/api/jury/confirm", null));
        var dashboard = await Json(await juror.GetAsync("/api/jury/dashboard"));
        Assert.Equal(order, Refs(dashboard.GetProperty("works")));
        Assert.Equal(3, dashboard.GetProperty("works")[0].GetProperty("vote").GetProperty("invalidatedVotes").GetInt32());
        // The juror walks the works in the same order: w3 (6 valid) comes after w1 and before w2.
        var work = await Json(await juror.GetAsync($"/api/jury/works/{w[2].Id}"));
        Assert.Equal((2, w[0].Id, w[1].Id), (work.GetProperty("index").GetInt32(), work.GetProperty("previousId").GetInt32(),
            work.GetProperty("nextId").GetInt32()));
        Assert.Equal((1, 2, 6, 0), (work.GetProperty("vote").GetProperty("position").GetInt32(), work.GetProperty("vote").GetProperty("tieSize").GetInt32(),
            work.GetProperty("vote").GetProperty("validVotes").GetInt32(), work.GetProperty("vote").GetProperty("invalidatedVotes").GetInt32()));
        // Counts only: no rule, no evidence, no voter, no jury standing.
        foreach (var text in new[] { print.GetRawText(), dashboard.GetRawText(), work.GetRawText() })
        {
            foreach (var word in new[] { "audit", "suspic", "disposable", "phone", "duplicate", "coordinated", "D14", "D12", "rank", "average" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@example.com", text);
            Assert.DoesNotContain("1234567", text);
            Assert.DoesNotContain("Səsverən", text);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await juror.GetAsync(Url(id))).StatusCode);
    }
}
