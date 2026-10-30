using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// D14 (owner, 7 October 2026): the vote of an account opened during the vote with an e-mail domain of the frozen
/// disposable set is set aside — competition-wide, in one audited decision, never deleted, never counted twice, and
/// only when the count is the one the admin was shown. The public audit names the rule and its total, nothing else.
/// </summary>
public sealed class DisposableEmailRuleTests
{
    // ---- The rule itself ----------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 7, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 7, 7, 40, 0, TimeSpan.Zero);

    [Fact]
    public void The_frozen_set_is_the_37_audited_domains()
    {
        Assert.Equal(37, DisposableEmailRule.Domains.Count);
        Assert.Equal(9, DisposableEmailRule.Domains.Count(d => d.Value == "temporarymail.com"));
        Assert.Equal("D14_DISPOSABLE_EMAIL", DisposableEmailRule.PolicyId);
        Assert.Equal("DisposableEmailDuringVoting", DisposableEmailRule.EvidenceType);
        Assert.All(DisposableEmailRule.Domains.Keys, d => Assert.Equal(d, d.Trim().ToLowerInvariant()));
    }

    [Theory]
    // An approved domain on an account opened during the vote: eligible.
    [InlineData("leyla@inboxorigin.com", 1, true)]
    [InlineData("x@3r.nexushint.com", 1, true)]
    // Normalized: case, surrounding spaces, a trailing dot.
    [InlineData("  Leyla.Q@MailMagnet.CO ", 1, true)]
    [InlineData("a@end.tw.", 1, true)]
    // Opened before the vote, or after it: never.
    [InlineData("leyla@inboxorigin.com", -1, false)]
    [InlineData("leyla@inboxorigin.com", 99, false)]
    // Ordinary providers, Gmail typos and malformed look-alikes: never.
    [InlineData("leyla@gmail.com", 1, false)]
    [InlineData("leyla@gamil.com", 1, false)]
    [InlineData("leyla@gmail.con", 1, false)]
    [InlineData("leyla@66.gmail.com", 1, false)]
    [InlineData("leyla@mail.ru", 1, false)]
    // Left out by the owner (one source only).
    [InlineData("leyla@fusioninbox.com", 1, false)]
    // No subdomain or look-alike guessing.
    [InlineData("leyla@mail.inboxorigin.com", 1, false)]
    [InlineData("leyla@inboxorigin.co", 1, false)]
    [InlineData("nobody", 1, false)]
    [InlineData(null, 1, false)]
    public void Eligible_only_for_a_listed_domain_on_an_account_opened_during_the_vote(string? email, int hoursIn, bool expected)
    {
        var opened = hoursIn switch { -1 => Start.AddSeconds(-1), 99 => End, _ => Start.AddHours(hoursIn) };
        Assert.Equal(expected, DisposableEmailRule.Meets(email, opened, Start, End));
    }

    [Fact]
    public void Without_a_voting_window_nothing_is_eligible() =>
        Assert.False(DisposableEmailRule.Meets("a@inboxorigin.com", Start, null, End));

    [Fact]
    public void An_admin_sees_a_masked_address_never_the_address() =>
        Assert.Equal("l***@inboxorigin.com", DisposableEmailRule.Mask("leyla.quliyeva@InboxOrigin.com"));

    // ---- In the competition --------------------------------------------------------------------------------------

    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private sealed class Scenario : IDisposable
    {
        public MeydanFactory Factory { get; }
        public DateTimeOffset T0 { get; }

        public Scenario(bool enabled)
        {
            Factory = new MeydanFactory
            {
                Settings =
                {
                    ["Email:OutboxWorkerEnabled"] = "false",
                    ["Voting:LeaderboardCacheSeconds"] = "0",
                    ["Voting:DisposableEmail:Enabled"] = enabled ? "true" : "false",
                },
            };
            var now = DateTimeOffset.UtcNow;
            T0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
            Factory.Clock.Now = T0;
        }

        public void Dispose() => Factory.Dispose();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> Refused(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"expected {(int)expected}, got {(int)res.StatusCode}: {body}");
        return body;
    }

    private static async Task<HttpClient> AccountAsync(Scenario s, string email, string? phone = null)
    {
        var client = s.Factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Səsverən", lastName = "Sınaq", phone = phone ?? TestPhones.Next(), email,
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await Json(res)).GetProperty("accessToken").GetString()!);
        return client;
    }

    private static async Task<(int Id, string Reference)> SubmitAsync(Scenario s, string settlement, string title)
    {
        var client = await AccountAsync(s, $"author-{Guid.NewGuid():N}@example.com");
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement })))
            .GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title, concept = "K", symbols = "S" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;
        return (id, reference);
    }

    private static async Task<HttpClient> AdminAsync(Scenario s)
    {
        var admin = s.Factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, s.Factory.AdminPassword));
        return admin;
    }

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, string reference) =>
        client.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null);

    private static string Url(int competition, string tail) => $"/api/admin/competitions/{competition}/voting/{tail}";

    private sealed record Race(HttpClient Admin, int Competition, (int Id, string Reference) A, (int Id, string Reference) B);

    /// <summary>
    /// Work A: three disposable accounts opened during the vote (one typed in capitals), a disposable one opened before
    /// it, and Gmail, a Gmail typo and fusioninbox.com during it. Work B: one disposable account during the vote, and a
    /// ring of three accounts on one phone — one of them disposable — set aside earlier under the phone rule.
    /// </summary>
    private static async Task<Race> RaceAsync(Scenario s)
    {
        var a = await SubmitAsync(s, "balaxani", "Balaxanı şevronu");
        var b = await SubmitAsync(s, "ramana", "Ramana qalası");
        var early = await AccountAsync(s, "early@abowned.com");
        var admin = await AdminAsync(s);
        var competition = (await Json(await admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
            .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();
        await s.Factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = s.T0.AddSeconds(5);
            c.VotingEndsAt = s.T0.AddSeconds(60);
            await db.SaveChangesAsync();
        });
        s.Factory.Clock.Now = s.T0.AddSeconds(10);

        foreach (var email in new[] { "one@inboxorigin.com", "  Two.Q@MailMagnet.CO ", "three@end.tw", "plain@gmail.com", "typo@gamil.com", "left@fusioninbox.com" })
            await Json(await VoteAsync(await AccountAsync(s, email), a.Reference));
        await Json(await VoteAsync(early, a.Reference));
        await Json(await VoteAsync(await AccountAsync(s, "four@aminavin.com"), b.Reference));

        var ring = new[] { await AccountAsync(s, "ring1@qabq.com"), await AccountAsync(s, "ring2@example.com"), await AccountAsync(s, "ring3@example.com") };
        var ringIds = new List<Guid>();
        foreach (var r in ring)
        {
            await Json(await VoteAsync(r, b.Reference));
            ringIds.Add((await Json(await r.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid());
        }
        await s.Factory.WithDbAsync(async db =>
        {
            foreach (var u in await db.Users.Where(u => ringIds.Contains(u.Id)).ToListAsync()) u.PhoneNumber = "+994 55 111 22 33";
            await db.SaveChangesAsync();
        });
        var review = await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{b.Id}")));
        var ringVotes = review.GetProperty("votes").EnumerateArray()
            .Where(v => v.GetProperty("evidence").GetString() == "PhoneCluster").Select(v => v.GetProperty("voteId").GetInt64()).ToArray();
        Assert.Equal(3, ringVotes.Length);
        await Json(await admin.PostAsJsonAsync(Url(competition, "votes/invalidate"),
            new { voteIds = ringVotes, reason = "Eyni telefonla açılmış 3 hesab." }));
        return new Race(admin, competition, a, b);
    }

    private static async Task<Dictionary<string, int>> RankingAsync(HttpClient client) =>
        (await Json(await client.GetAsync($"/api/competitions/{Chevron}/voting/leaderboard")))
        .GetProperty("entries").EnumerateArray()
        .ToDictionary(e => e.GetProperty("reference").GetString()!, e => e.GetProperty("voteCount").GetInt32());

    [Fact]
    public async Task Applied_once_competition_wide_it_sets_aside_exactly_the_eligible_votes_and_deletes_nothing()
    {
        using var s = new Scenario(enabled: true);
        var r = await RaceAsync(s);

        var preview = await Json(await r.Admin.GetAsync(Url(r.Competition, "rules/disposable-email")));
        Assert.True(preview.GetProperty("enabled").GetBoolean());
        Assert.Equal(37, preview.GetProperty("domainCount").GetInt32());
        Assert.Equal(4, preview.GetProperty("eligibleVotes").GetInt32());
        // The ring's disposable vote is already set aside under the phone rule: reported, never counted twice.
        Assert.Equal(1, preview.GetProperty("setAsideOtherwise").GetInt32());
        Assert.Equal(1, preview.GetProperty("openedBeforeVoting").GetInt32());
        var works = preview.GetProperty("works").EnumerateArray().ToDictionary(w => w.GetProperty("reference").GetString()!, w => w);
        Assert.Equal((3, 7, 4), (works[r.A.Reference].GetProperty("eligible").GetInt32(), works[r.A.Reference].GetProperty("validNow").GetInt32(),
            works[r.A.Reference].GetProperty("validAfter").GetInt32()));
        Assert.Equal((1, 1, 0), (works[r.B.Reference].GetProperty("eligible").GetInt32(), works[r.B.Reference].GetProperty("validNow").GetInt32(),
            works[r.B.Reference].GetProperty("validAfter").GetInt32()));
        // Masked evidence for the admin; never the address.
        var raw = preview.GetRawText();
        Assert.Contains("o***@inboxorigin.com", raw);
        foreach (var hidden in new[] { "one@inboxorigin.com", "Two.Q", "plain@gmail.com", "fusioninbox", "gamil" })
            Assert.DoesNotContain(hidden, raw, StringComparison.OrdinalIgnoreCase);

        var voter = await AccountAsync(s, $"v-{Guid.NewGuid():N}@example.com");
        // Not while the vote is open.
        Assert.Contains("bağlandıqdan sonra", await Refused(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"),
            new { expectedVotes = 4 }), HttpStatusCode.Conflict));
        s.Factory.Clock.Now = s.T0.AddSeconds(120);

        // A count other than the one shown changes nothing.
        Assert.Contains("Heç nə dəyişmədi", await Refused(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"),
            new { expectedVotes = 5 }), HttpStatusCode.Conflict));
        Assert.Equal(3, s.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
        // Participants never reach it.
        Assert.Equal(HttpStatusCode.Forbidden, (await voter.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"), new { expectedVotes = 4 })).StatusCode);

        var before = s.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().OrderBy(v => v.Id)
            .Select(v => new { v.Id, v.UserId, v.SubmissionId, v.CreatedAt }).ToList());
        var applied = await Json(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"), new { expectedVotes = 4 }));
        Assert.Equal(0, applied.GetProperty("eligibleVotes").GetInt32());
        Assert.Equal(4, applied.GetProperty("setAsideUnderRule").GetInt32());

        // Never deleted: every row is still there with its own facts; the four carry who, when and the rule.
        var after = s.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().OrderBy(v => v.Id).ToList());
        Assert.Equal(before.Select(v => (v.Id, v.UserId, v.SubmissionId, v.CreatedAt)), after.Select(v => (v.Id, v.UserId, v.SubmissionId, v.CreatedAt)));
        var d14 = after.Where(v => v.InvalidationReason?.StartsWith("D14_DISPOSABLE_EMAIL v1: ") == true).ToList();
        Assert.Equal(4, d14.Count);
        Assert.All(d14, v =>
        {
            Assert.Equal(s.Factory.Clock.Now, v.InvalidatedAt);
            Assert.NotNull(v.InvalidatedById);
            Assert.Equal($"D14_DISPOSABLE_EMAIL v1: {DisposableEmailRule.Reason}", v.InvalidationReason);
        });
        // The earlier phone-rule decision is untouched.
        Assert.Equal(3, after.Count(v => v.InvalidationReason == "Eyni telefonla açılmış 3 hesab."));

        // One audit entry per vote, with its evidence domain, and one for the decision.
        var audit = s.Factory.WithDb(db => db.AuditLogs.OrderBy(x => x.Id).Select(x => new { x.Action, x.Summary }).ToList());
        var votes = audit.Where(x => x.Action == "VoteInvalidated" && x.Summary!.Contains("D14_DISPOSABLE_EMAIL")).ToList();
        Assert.Equal(4, votes.Count);
        Assert.Contains(votes, x => x.Summary!.Contains("domain mailmagnet.co") && x.Summary.Contains("evidence DisposableEmailDuringVoting"));
        var decision = Assert.Single(audit, x => x.Action == "VotingRuleApplied");
        Assert.Contains("D14_DISPOSABLE_EMAIL v1 (DisposableEmailDuringVoting): 4 votes set aside", decision.Summary);
        Assert.Contains($"{r.A.Reference} 3, {r.B.Reference} 1", decision.Summary);

        // The ranking re-counts on valid votes at once.
        var ranking = await RankingAsync(s.Factory.CreateClient());
        Assert.Equal(4, ranking[r.A.Reference]);
        Assert.Equal(0, ranking[r.B.Reference]);

        // Applied again: nothing left to set aside.
        Assert.Contains("uyğun etibarlı səs yoxdur", await Refused(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"),
            new { expectedVotes = 4 }), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task The_public_audit_names_the_new_rule_and_its_total_and_nothing_private()
    {
        using var s = new Scenario(enabled: true);
        var r = await RaceAsync(s);
        s.Factory.Clock.Now = s.T0.AddSeconds(120);
        await Json(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"), new { expectedVotes = 4 }));

        var res = await s.Factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/audit");
        var body = await res.Content.ReadAsStringAsync();
        var audit = await Json(res);
        Assert.Equal(11, audit.GetProperty("totalVotes").GetInt32());
        Assert.Equal(7, audit.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(4, audit.GetProperty("validVotes").GetInt32());
        var rules = audit.GetProperty("rules").EnumerateArray()
            .ToDictionary(x => x.GetProperty("reason").GetString()!, x => x.GetProperty("votes").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["DuplicatePhone"] = 3, ["DisposableEmail"] = 4 }, rules);
        Assert.Equal(audit.GetProperty("invalidatedVotes").GetInt32(), rules.Values.Sum());
        Assert.Contains(audit.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("reason").GetString() == "DisposableEmail" && e.GetProperty("votes").GetInt32() == 4);

        // Aggregates only: no address, domain, account, admin, work or the stored reason.
        foreach (var hidden in new[] { "@", "inboxorigin", "mailmagnet", "aminavin", "qabq", "D14_", "Disposable/temporary", "Səsverən",
                     "+994", r.A.Reference, r.B.Reference, "userId", "invalidatedBy" })
            Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Switched_off_the_rule_is_shown_but_never_applied()
    {
        using var s = new Scenario(enabled: false);
        var r = await RaceAsync(s);
        s.Factory.Clock.Now = s.T0.AddSeconds(120);

        var preview = await Json(await r.Admin.GetAsync(Url(r.Competition, "rules/disposable-email")));
        Assert.False(preview.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, preview.GetProperty("eligibleVotes").GetInt32());
        Assert.Contains("aktiv deyil", await Refused(await r.Admin.PostAsJsonAsync(Url(r.Competition, "rules/disposable-email/apply"),
            new { expectedVotes = 4 }), HttpStatusCode.Conflict));
        // Nor through the vote-by-vote path: without the rule these votes are review-only.
        var review = await Json(await r.Admin.GetAsync(Url(r.Competition, $"suspicion/works/{r.A.Id}")));
        Assert.DoesNotContain(review.GetProperty("votes").EnumerateArray(), v => v.GetProperty("evidence").GetString() == "DisposableEmailDuringVoting");
        Assert.Equal(3, s.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
    }
}
