using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// A result announced as two independent podiums (owner, 16 October 2026): the jury's choice with the points of its
/// own (paper) protocol, and the public's choice with the works' valid votes. Recorded by an admin, checked by the
/// server, public only through the existing publication — never combined, never an overall winner, and the votes,
/// the audit and the jury's own evaluation untouched.
/// </summary>
public sealed class CompetitionAwardTests : IDisposable
{
    private const string Slug = "iki-secim-sinaq";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Work(int Id, string Reference);

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ProblemAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"{(int)res.StatusCode}: {body}");
        return body;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private static string Url(int id, string path = "") => $"/api/admin/competitions/{id}/result{path}";

    /// <summary>
    /// Five works on the ballot — valid votes 9, 7, 5, 2, 0; the fourth also lost 20 votes to an audit decision — and a
    /// rejected sixth. Entries are closed, the vote is over and nothing is published.
    /// </summary>
    private async Task<(HttpClient Admin, int Competition, Work[] Works, Work Rejected)> ScenarioAsync()
    {
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var id = (await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "“İki seçim” sınaq müsabiqəsi", slug = Slug, shortDescription = "Sınaq", hasCategories = false,
        }))).GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var now = _factory.Clock.Now;
        var works = new List<Work>();
        Work? rejected = null;
        await _factory.WithDbAsync(async db =>
        {
            var umumi = new CompetitionCategory { CompetitionId = id, Name = "Ümumi", Slug = "umumi", IsActive = true };
            db.CompetitionCategories.Add(umumi);
            var c = await db.Competitions.FirstAsync(x => x.Id == id);
            c.EndDate = now.AddDays(-4);
            c.VotingStartsAt = now.AddDays(-3);
            c.VotingEndsAt = now.AddHours(-1);
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

            Submission Add(int n, SubmissionStatus status) => db.Submissions.Add(new Submission
            {
                UserId = User($"Müəllif{n}", "Hesabadı").Id, CompetitionId = id, CompetitionCategoryId = umumi.Id, Title = $"Əsər {n}",
                Status = status, ReferenceNumber = $"SUB-2097-{n:00000}", SubmittedAt = now.AddDays(-5), CreatedAt = now.AddDays(-6),
                UpdatedAt = now.AddDays(-5),
            }).Entity;

            var subs = Enumerable.Range(1, 5).Select(n => Add(n, SubmissionStatus.Submitted)).ToList();
            var r = Add(6, SubmissionStatus.Rejected);
            await db.SaveChangesAsync();
            int[] valid = [9, 7, 5, 2, 0];
            for (var i = 0; i < subs.Count; i++)
                for (var v = 0; v < valid[i] + (i == 3 ? 20 : 0); v++)
                    db.Votes.Add(new Vote
                    {
                        UserId = User("Səsverən", $"S{i}-{v}").Id, SubmissionId = subs[i].Id, CompetitionCategoryId = umumi.Id,
                        CreatedAt = now.AddDays(-2), InvalidatedAt = i == 3 && v < 20 ? now.AddDays(-1) : null,
                        InvalidationReason = i == 3 && v < 20 ? "D12: sınaq" : null,
                    });
            await db.SaveChangesAsync();
            works.AddRange(subs.Select(s => new Work(s.Id, s.ReferenceNumber!)));
            rejected = new Work(r.Id, r.ReferenceNumber!);
        });
        return (admin, id, works.ToArray(), rejected!);
    }

    private static object Place(Work w, string name, int points) => new { submissionId = w.Id, participantName = name, points };

    private static object Podium(object[] places, int?[]? current = null, string? source = null, bool confirm = false) =>
        new { places, current = current ?? [null, null, null], source, confirmPublished = confirm };

    private Task<HttpResponseMessage> JuryAsync(HttpClient admin, int id, Work[] w, int?[]? current = null, bool confirm = false) =>
        admin.PutAsJsonAsync(Url(id, "/awards/jury"), Podium(
            [Place(w[2], "Kamal Məmmədli", 30), Place(w[0], "Məryəm Əhmədova", 26), Place(w[4], "Nərgiz Əsədova", 16)],
            current, "Kağız protokol (münsiflərin səsverməsi)", confirm));

    private Task<HttpResponseMessage> PublicAsync(HttpClient admin, int id, Work[] w, int?[]? current = null) =>
        admin.PutAsJsonAsync(Url(id, "/awards/public"), Podium(
            [Place(w[0], "Məryəm Əhmədova", 9), Place(w[1], "Şəbnəm Ələkbərli", 7), Place(w[2], "Səid Məmmədov", 5)], current));

    private Task<string> VotesFingerprintAsync(int competitionId) =>
        _factory.QueryDbAsync(async db =>
        {
            var rows = await db.Votes.IgnoreQueryFilters().Where(v => v.Submission.CompetitionId == competitionId).OrderBy(v => v.Id)
                .Select(v => new { v.Id, v.SubmissionId, v.InvalidatedAt, v.InvalidationReason }).ToListAsync();
            return string.Join(";", rows.Select(v => $"{v.Id}|{v.SubmissionId}|{v.InvalidatedAt:O}|{v.InvalidationReason}"));
        });

    [Fact]
    public async Task Both_podiums_stay_private_until_the_result_is_published_then_show_apart()
    {
        var (admin, id, w, _) = await ScenarioAsync();
        var votes = await VotesFingerprintAsync(id);
        var anonymous = _factory.CreateClient();

        await Json(await JuryAsync(admin, id, w));
        var a = await Json(await PublicAsync(admin, id, w));
        Assert.Equal([30, 26, 16], a.GetProperty("jury").GetProperty("places").EnumerateArray().Select(p => p.GetProperty("points").GetInt32()));
        Assert.Equal([9, 7, 5], a.GetProperty("public").GetProperty("places").EnumerateArray().Select(p => p.GetProperty("points").GetInt32()));
        Assert.Equal("Kağız protokol (münsiflərin səsverməsi)", a.GetProperty("jury").GetProperty("source").GetString());
        Assert.Equal(AwardAdministration.PublicSource, a.GetProperty("public").GetProperty("source").GetString());

        // Recorded, not published: no public page or endpoint knows of either podium.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Slug}/results")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Slug}/gallery")).StatusCode);
        foreach (var path in new[] { $"/api/competitions/{Slug}", $"/api/competitions/{Slug}/voting", $"/api/competitions/{Slug}/voting/works",
                     $"/api/competitions/{Slug}/voting/leaderboard", "/api/competitions", "/api/results" })
        {
            var text = await (await anonymous.GetAsync(path)).Content.ReadAsStringAsync();
            foreach (var word in new[] { "Kamal", "Kağız", "award", "Award", "jury", "Jury" })
                Assert.DoesNotContain(word, text);
        }
        var state = await Json(await admin.GetAsync(Url(id)));
        Assert.Equal("Prepared", state.GetProperty("state").GetString());
        Assert.Empty(state.GetProperty("publishProblems").EnumerateArray());

        // Published by the existing publication — no classic winner needed — and shown as two podiums.
        await Json(await admin.PostAsync(Url(id, "/publish"), null));
        var r = await Json(await anonymous.GetAsync($"/api/competitions/{Slug}/results"));
        var jury = r.GetProperty("awards").GetProperty("jury").EnumerateArray().ToList();
        var pub = r.GetProperty("awards").GetProperty("public").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], jury.Select(x => x.GetProperty("place").GetInt32()));
        Assert.Equal(["Kamal Məmmədli", "Məryəm Əhmədova", "Nərgiz Əsədova"], jury.Select(x => x.GetProperty("participantName").GetString()));
        Assert.Equal([w[2].Reference, w[0].Reference, w[4].Reference], jury.Select(x => x.GetProperty("work").GetProperty("reference").GetString()));
        Assert.Equal([30, 26, 16], jury.Select(x => x.GetProperty("points").GetInt32()));
        Assert.Equal([w[0].Reference, w[1].Reference, w[2].Reference], pub.Select(x => x.GetProperty("work").GetProperty("reference").GetString()));
        Assert.Equal([9, 7, 5], pub.Select(x => x.GetProperty("points").GetInt32()));
        // Nothing merged: no overall winner, no combined final, no source and no account name on the public side.
        Assert.Equal(JsonValueKind.Null, r.GetProperty("overallWinner").ValueKind);
        Assert.Equal(0, r.GetProperty("places").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("final").ValueKind);
        var text2 = r.GetRawText();
        Assert.DoesNotContain("Kağız", text2);
        Assert.DoesNotContain("Hesabadı", text2);
        Assert.Equal(4, r.GetProperty("winnerCount").GetInt32()); // four works on the two podiums, two of them on both

        // The work standing on both podiums says so on its own page.
        var page = await Json(await anonymous.GetAsync($"/api/competitions/{Slug}/gallery/{w[0].Reference}"));
        Assert.Equal([("Jury", 2, 26), ("Public", 1, 9)], page.GetProperty("awards").EnumerateArray()
            .Select(x => (x.GetProperty("category").GetString(), x.GetProperty("place").GetInt32(), x.GetProperty("points").GetInt32())));
        Assert.Equal("Məryəm Əhmədova", page.GetProperty("winnerName").GetString());

        // Taken off again: private again. The votes never moved.
        await Json(await admin.PostAsync(Url(id, "/unpublish"), null));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Slug}/results")).StatusCode);
        Assert.Equal(votes, await VotesFingerprintAsync(id));
        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.Action == AuditActions.AwardsSet));
            Assert.Contains(await db.AuditLogs.Where(x => x.Action == AuditActions.AwardsSet).Select(x => x.Summary).ToListAsync(),
                s => s!.StartsWith("Münsiflərin seçimi: 1 SUB-2097-00003 Kamal Məmmədli 30", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task The_public_podium_is_the_valid_vote_itself()
    {
        var (admin, id, w, _) = await ScenarioAsync();
        async Task<string> Refused(object[] places) =>
            await ProblemAsync(await admin.PutAsJsonAsync(Url(id, "/awards/public"), Podium(places)), HttpStatusCode.BadRequest);

        // A count that is not the server's.
        Assert.Contains("9 etibarlı səs, siz 10 yazmısınız",
            await Refused([Place(w[0], "Ad Bir", 10), Place(w[1], "Ad İki", 7), Place(w[2], "Ad Üç", 5)]));
        // Not the vote's order: the fourth work (2 valid, 20 set aside) is not in the top three.
        Assert.Contains("4-ci yerdədir",
            await Refused([Place(w[0], "Ad Bir", 9), Place(w[1], "Ad İki", 7), Place(w[3], "Ad Dörd", 2)]));
        // Set-aside votes never count: with its 20 set aside, the fourth work has 2, not 22.
        Assert.Contains("2 etibarlı səs, siz 22 yazmısınız",
            await Refused([Place(w[3], "Ad Dörd", 22), Place(w[0], "Ad Bir", 9), Place(w[1], "Ad İki", 7)]));

        // Nothing was written by any refusal.
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.CompetitionAwards.CountAsync()));

        // While the vote is open there is no public podium at all.
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.FirstAsync(x => x.Id == id);
            c.VotingEndsAt = _factory.Clock.Now.AddDays(1);
            await db.SaveChangesAsync();
        });
        await ProblemAsync(await PublicAsync(admin, id, w), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_jury_podium_needs_its_source_three_eligible_works_and_descending_points()
    {
        var (admin, id, w, rejected) = await ScenarioAsync();
        async Task<string> Refused(object body) =>
            await ProblemAsync(await admin.PutAsJsonAsync(Url(id, "/awards/jury"), body), HttpStatusCode.BadRequest);

        Assert.Contains("mənbəyini", await Refused(Podium([Place(w[2], "Ad Bir", 30), Place(w[0], "Ad İki", 26), Place(w[4], "Ad Üç", 16)])));
        Assert.Contains("bir yer", await Refused(Podium([Place(w[2], "Ad Bir", 30), Place(w[2], "Ad İki", 26), Place(w[4], "Ad Üç", 16)], source: "Kağız protokol")));
        Assert.Contains("Rədd edilmiş", await Refused(Podium([Place(w[2], "Ad Bir", 30), Place(rejected, "Ad İki", 26), Place(w[4], "Ad Üç", 16)], source: "Kağız protokol")));
        Assert.Contains("az olmalıdır", await Refused(Podium([Place(w[2], "Ad Bir", 30), Place(w[0], "Ad İki", 30), Place(w[4], "Ad Üç", 16)], source: "Kağız protokol")));
        Assert.Contains("adını", await Refused(Podium([Place(w[2], " ", 30), Place(w[0], "Ad İki", 26), Place(w[4], "Ad Üç", 16)], source: "Kağız protokol")));
        Assert.Contains("Üç yerin", await Refused(Podium([Place(w[2], "Ad Bir", 30), Place(w[0], "Ad İki", 26)], source: "Kağız protokol")));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.CompetitionAwards.CountAsync()));

        // The jury's own evaluation is never read or written by any of this.
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.JuryWorkScores.CountAsync()));
    }

    [Fact]
    public async Task A_stale_screen_or_a_published_result_is_never_overwritten_silently()
    {
        var (admin, id, w, _) = await ScenarioAsync();
        await Json(await JuryAsync(admin, id, w));
        await Json(await PublicAsync(admin, id, w));

        // A screen that still shows the empty podium is refused.
        await ProblemAsync(await JuryAsync(admin, id, w), HttpStatusCode.Conflict);

        await Json(await admin.PostAsync(Url(id, "/publish"), null));
        int?[] current = [w[2].Id, w[0].Id, w[4].Id];
        // Once public: only with the admin's explicit confirmation, and recorded as a change to a published result.
        Assert.Contains("dərhal görünəcək", await ProblemAsync(await JuryAsync(admin, id, w, current), HttpStatusCode.Conflict));
        await Json(await JuryAsync(admin, id, w, current, confirm: true));
        Assert.Contains(await _factory.QueryDbAsync(db => db.AuditLogs.Where(x => x.Action == AuditActions.AwardsSet)
            .Select(x => x.Summary).ToListAsync()), s => s!.EndsWith("(published result)", StringComparison.Ordinal));
        // And a podium is never taken off a published result.
        await ProblemAsync(await admin.DeleteAsync(Url(id, "/awards/jury")), HttpStatusCode.Conflict);

        // An awarded work stays: it cannot be deleted for good.
        await ProblemAsync(await admin.DeleteAsync($"/api/admin/submissions/{w[4].Id}/permanent"), HttpStatusCode.Conflict);
        Assert.Equal(6, await _factory.QueryDbAsync(db => db.CompetitionAwards.CountAsync()));
    }

    [Fact]
    public async Task Only_admins_reach_the_podiums()
    {
        var (_, id, _, _) = await ScenarioAsync();
        var anonymous = _factory.CreateClient();
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Url(id, "/awards"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync(Url(id, "/awards"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.PutAsJsonAsync(Url(id, "/awards/jury"), new { })).StatusCode);
        var unknown = await (await AdminAsync()).PutAsJsonAsync(Url(id, "/awards/overall"), Podium([]));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
