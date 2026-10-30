using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Meydan.Api.Tests;

/// <summary>
/// R2, coordinated identity, as the score reads it — no database: every condition is needed, none is enough alone,
/// the thresholds hold at their edges, and the rule changes nothing until it is switched on.
/// </summary>
public sealed class CoordinatedIdentityRuleTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 4, 7, 40, 0, TimeSpan.Zero);
    private static readonly CoordinatedIdentityRule On = new(Enabled: true);
    private static long _id;

    private static SuspicionVote Vote(
        string first, string last, string? ip, int work = 1, double signedUpMinutes = 60, string? phone = null) =>
        new(++_id, work, $"SUB-2026-{work:D5}", Guid.NewGuid(), first, last, phone ?? $"+994 70 {_id % 1000:D3} 00 00",
            $"{first.ToLowerInvariant()}{_id}x@example.com", Opened.AddMinutes(signedUpMinutes),
            Opened.AddMinutes(signedUpMinutes + 1), "Roya", "Əliyeva", "+994 55 111 22 33", ip is null ? [] : [ip], ip);

    private static List<SuspicionVote> Ring(int n, string ip = "10.9.9.9", int work = 1) =>
        Enumerable.Range(0, n).Select(i => Vote(i % 2 == 0 ? "Fədayə" : "Fedaye", "Qurbanova", ip, work)).ToList();

    [Fact]
    public void Five_new_accounts_of_one_name_from_one_address_are_a_coordinated_identity()
    {
        var all = VoteSuspicion.Score(Ring(5), Opened, On);
        Assert.All(all, s =>
        {
            Assert.Equal(VoteEvidence.CoordinatedIdentity, s.Evidence);
            Assert.True(s.CoordinatedIdentity);
            Assert.Equal((5, 5), (s.NameGroup, s.SignUpIpInNameGroup));
            Assert.Contains(s.Reasons, r => r.StartsWith("R2 (koordinasiyalı kimlik)") && !r.Contains("hələ qəbul edilməyib"));
        });
    }

    [Fact]
    public void Switched_off_it_is_only_named_and_the_score_does_not_move()
    {
        var votes = Ring(5);
        var off = VoteSuspicion.Score(votes, Opened);
        var on = VoteSuspicion.Score(votes, Opened, On);
        Assert.All(off, s =>
        {
            Assert.Equal(VoteEvidence.Weak, s.Evidence);
            Assert.True(s.CoordinatedIdentity);
            Assert.Contains(s.Reasons, r => r.StartsWith("R2") && r.Contains("hələ qəbul edilməyib"));
        });
        Assert.Equal(off.Select(s => s.Score), on.Select(s => s.Score));
    }

    [Fact]
    public void A_name_alone_an_address_alone_or_an_odd_phone_alone_is_not_enough()
    {
        // One name, five addresses.
        var names = Enumerable.Range(0, 6).Select(i => Vote("Aysel", "Məmmədova", $"10.1.0.{i}")).ToList();
        // One address, five names.
        var address = new[] { "Orxan", "Kamran", "Nigar", "Leyla", "Rəşad", "Tural" }
            .Select(f => Vote(f, "Babayev", "10.2.0.1")).ToList();
        // Five odd phones, five names, five addresses.
        var phones = new[] { "Samir", "Zaur", "Elvin", "Vüsal", "Fuad" }
            .Select((f, i) => Vote(f, "Həsənov", $"10.3.0.{i}", phone: "+994 12 444 55 6" + i)).ToList();
        var all = VoteSuspicion.Score([.. names, .. address, .. phones], Opened, On with { PhoneCorroborates = true });
        Assert.All(all, s =>
        {
            Assert.False(s.CoordinatedIdentity);
            Assert.NotEqual(VoteEvidence.CoordinatedIdentity, s.Evidence);
        });
    }

    [Fact]
    public void An_odd_phone_stands_in_for_the_address_only_when_the_owner_says_so()
    {
        var votes = Enumerable.Range(0, 5)
            .Select(i => Vote("Aysel", "Məmmədova", $"10.4.0.{i}", phone: $"+994 59 000 00 0{i}")).ToList();
        Assert.All(VoteSuspicion.Score(votes, Opened, On), s => Assert.False(s.CoordinatedIdentity));
        Assert.All(VoteSuspicion.Score(votes, Opened, On with { PhoneCorroborates = true }), s =>
        {
            Assert.Equal(VoteEvidence.CoordinatedIdentity, s.Evidence);
            Assert.Contains(s.Reasons, r => r.Contains("Azərbaycan mobil nömrəsi formasında deyil"));
        });
    }

    [Fact]
    public void An_account_opened_before_the_vote_is_never_caught_though_it_counts_toward_the_group()
    {
        var old = Vote("Fədayə", "Qurbanova", "10.9.9.9", signedUpMinutes: -600);
        var fresh = Ring(4);
        var all = VoteSuspicion.Score([old, .. fresh], Opened, On);
        Assert.False(all.Single(s => s.Vote.VoteId == old.VoteId).CoordinatedIdentity);
        Assert.Equal(VoteEvidence.Weak, all.Single(s => s.Vote.VoteId == old.VoteId).Evidence);
        Assert.All(all.Where(s => s.Vote.VoteId != old.VoteId), s => Assert.Equal(VoteEvidence.CoordinatedIdentity, s.Evidence));

        // Without a known start nothing is "new", so nothing is caught.
        Assert.All(VoteSuspicion.Score(Ring(6), null, On), s => Assert.False(s.CoordinatedIdentity));
    }

    [Fact]
    public void The_thresholds_hold_at_their_edges()
    {
        Assert.All(VoteSuspicion.Score(Ring(4), Opened, On), s => Assert.False(s.CoordinatedIdentity));
        Assert.All(VoteSuspicion.Score(Ring(5), Opened, On), s => Assert.True(s.CoordinatedIdentity));

        // Six of one name, but only four from one address and two from another: the address test fails for all.
        var split = Ring(4, "10.5.0.1").Concat(Ring(2, "10.5.0.2")).ToList();
        Assert.All(VoteSuspicion.Score(split, Opened, On), s => Assert.False(s.CoordinatedIdentity));
        // Five from one address and one elsewhere: the five are caught, the one is not.
        var five = Ring(5, "10.6.0.1");
        var other = Ring(1, "10.6.0.2");
        var all = VoteSuspicion.Score([.. five, .. other], Opened, On);
        Assert.All(all.Where(s => five.Contains(s.Vote)), s => Assert.True(s.CoordinatedIdentity));
        Assert.False(all.Single(s => s.Vote == other[0]).CoordinatedIdentity);

        // Stricter settings move the edge.
        Assert.All(VoteSuspicion.Score(Ring(6), Opened, On with { NameGroupMin = 7 }), s => Assert.False(s.CoordinatedIdentity));
        Assert.All(VoteSuspicion.Score(Ring(7), Opened, On with { NameGroupMin = 7, SameIpMin = 7 }), s => Assert.True(s.CoordinatedIdentity));
    }

    [Fact]
    public void The_group_is_read_across_the_whole_competition()
    {
        var all = VoteSuspicion.Score([.. Ring(3, work: 1), .. Ring(2, work: 2)], Opened, On);
        Assert.All(all, s => Assert.Equal(VoteEvidence.CoordinatedIdentity, s.Evidence));
    }

    [Fact]
    public void A_phone_cluster_stays_a_phone_cluster()
    {
        var votes = Ring(5).Select(v => v with { VoterPhone = "+994 50 777 66 55" }).ToList();
        Assert.All(VoteSuspicion.Score(votes, Opened, On), s =>
        {
            Assert.Equal(VoteEvidence.PhoneCluster, s.Evidence);
            Assert.True(s.CoordinatedIdentity);
        });
    }

    [Fact]
    public void Configuration_is_off_unless_set_and_never_below_three()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal(CoordinatedIdentityRule.Off, CoordinatedIdentityRule.From(empty));
        var loose = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Voting:CoordinatedIdentity:Enabled"] = "true",
            ["Voting:CoordinatedIdentity:NameGroupMin"] = "1",
            ["Voting:CoordinatedIdentity:SameIpMin"] = "2",
        }).Build();
        Assert.Equal(new CoordinatedIdentityRule(true, 3, 3, false), CoordinatedIdentityRule.From(loose));
    }
}

/// <summary>
/// R2 through the admin API: shown on the review screen whether or not it is on, ground to set a vote aside only when
/// it is on, all or none, in the audit log — and votes set aside before it stay exactly as they were.
/// </summary>
public sealed class CoordinatedIdentityReviewTests
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private const string Reason = "Eyni ad-soyadla, eyni IP-dən açılmış 5 yeni hesab — koordinasiyalı saxta kimlik.";

    private sealed record Work(int Id, string Reference);

    private sealed class World(MeydanFactory factory) : IDisposable
    {
        public MeydanFactory Factory { get; } = factory;
        public HttpClient Admin = null!;
        public int Competition;
        public Work Fraud = null!, Honest = null!;
        public List<long> RingVotes = [], CrowdVotes = [], OldVotes = [];
        public void Dispose() => Factory.Dispose();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(HttpClient Client, Guid Id)> AccountAsync(MeydanFactory f, string first, string last)
    {
        var client = f.CreateClient();
        client.UseToken((await Json(await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(), email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        }))).GetProperty("accessToken").GetString()!);
        var id = (await Json(await client.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();
        return (client, id);
    }

    private static async Task<Work> SubmitAsync(MeydanFactory f, string settlement, string title, string first, string last)
    {
        var (client, _) = await AccountAsync(f, first, last);
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement })))
            .GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title, concept = "K", symbols = "S" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        return new Work(id, (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!);
    }

    /// <summary>
    /// "Fraud" gets five votes from new accounts of one name opened from one address (R2), and three from older
    /// accounts of that same name and address — opened before the vote, so never R2. "Honest" gets three ordinary votes.
    /// </summary>
    private static async Task<World> WorldAsync(bool r2)
    {
        var w = new World(new MeydanFactory
        {
            Settings =
            {
                ["Email:OutboxWorkerEnabled"] = "false",
                ["Voting:LeaderboardCacheSeconds"] = "0",
                ["Voting:CoordinatedIdentity:Enabled"] = r2 ? "true" : "false",
            },
        });
        var f = w.Factory;
        var now = DateTimeOffset.UtcNow;
        var t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        f.Clock.Now = t0;

        w.Fraud = await SubmitAsync(f, "balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        w.Honest = await SubmitAsync(f, "ramana", "Ramana qalası", "Nigar", "Həsənova");
        var ring = new List<(HttpClient C, Guid Id)>();
        for (var i = 0; i < 5; i++) ring.Add(await AccountAsync(f, i % 2 == 0 ? "Fədayə" : "Fedaye", "Qurbanova"));
        var old = new List<(HttpClient C, Guid Id)>();
        for (var i = 0; i < 3; i++) old.Add(await AccountAsync(f, "Fədayə", "Qurbanova"));
        var crowd = new List<(HttpClient C, Guid Id)>();
        foreach (var (fn, ln) in new[] { ("Orxan", "Məmmədov"), ("Aysel", "Əliyeva"), ("Kamran", "Babayev") })
            crowd.Add(await AccountAsync(f, fn, ln));

        w.Admin = f.CreateClient();
        w.Admin.UseToken(await w.Admin.LoginAsync(MeydanFactory.AdminEmail, f.AdminPassword));
        w.Competition = (await Json(await w.Admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
            .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();

        var ringIds = ring.Select(x => x.Id).ToList();
        var oldIds = old.Select(x => x.Id).ToList();
        var crowdIds = crowd.Select(x => x.Id).ToList();
        await f.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = t0.AddSeconds(5);
            c.VotingEndsAt = t0.AddSeconds(60);
            // The ring was opened during the vote; the older namesakes before it. Both from one address.
            foreach (var u in await db.Users.Where(u => ringIds.Contains(u.Id)).ToListAsync()) u.CreatedAt = t0.AddSeconds(6);
            foreach (var u in await db.Users.Where(u => oldIds.Contains(u.Id)).ToListAsync()) u.CreatedAt = t0.AddDays(-3);
            await db.SaveChangesAsync();
            await db.RefreshTokens.Where(t => ringIds.Contains(t.UserId) || oldIds.Contains(t.UserId))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, "10.77.0.1"));
            var k = 0;
            foreach (var id in crowdIds)
            {
                var ip = $"10.88.0.{++k}";
                await db.RefreshTokens.Where(t => t.UserId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, ip));
            }
        });
        f.Clock.Now = t0.AddSeconds(10);

        foreach (var (c, _) in ring.Concat(old))
            await Json(await c.PostAsync($"/api/competitions/{Chevron}/voting/works/{w.Fraud.Reference}/vote", null));
        foreach (var (c, _) in crowd)
            await Json(await c.PostAsync($"/api/competitions/{Chevron}/voting/works/{w.Honest.Reference}/vote", null));

        w.RingVotes = f.WithDb(db => db.Votes.Where(v => ringIds.Contains(v.UserId)).Select(v => v.Id).ToList());
        w.OldVotes = f.WithDb(db => db.Votes.Where(v => oldIds.Contains(v.UserId)).Select(v => v.Id).ToList());
        w.CrowdVotes = f.WithDb(db => db.Votes.Where(v => crowdIds.Contains(v.UserId)).Select(v => v.Id).ToList());
        return w;
    }

    private static async Task<JsonElement> ReviewAsync(World w, Work work) =>
        await Json(await w.Admin.GetAsync($"/api/admin/competitions/{w.Competition}/voting/suspicion/works/{work.Id}"));

    private static Task<HttpResponseMessage> InvalidateAsync(World w, IEnumerable<long> ids) =>
        w.Admin.PostAsJsonAsync($"/api/admin/competitions/{w.Competition}/voting/votes/invalidate", new { voteIds = ids, reason = Reason });

    private static int SetAside(World w) => w.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null));

    [Fact]
    public async Task Switched_off_the_review_shows_R2_but_the_server_still_refuses_it()
    {
        using var w = await WorldAsync(r2: false);
        var review = await ReviewAsync(w, w.Fraud);
        var e = review.GetProperty("evidence");
        Assert.Equal(5, e.GetProperty("coordinatedIdentityVotes").GetInt32());
        Assert.False(e.GetProperty("coordinatedIdentityEnabled").GetBoolean());
        var votes = review.GetProperty("votes").EnumerateArray().ToList();
        var ring = votes.Where(v => w.RingVotes.Contains(v.GetProperty("voteId").GetInt64())).ToList();
        Assert.All(ring, v =>
        {
            Assert.True(v.GetProperty("coordinatedIdentity").GetBoolean());
            Assert.Equal("Weak", v.GetProperty("evidence").GetString());
        });
        Assert.All(votes.Where(v => w.OldVotes.Contains(v.GetProperty("voteId").GetInt64())),
            v => Assert.False(v.GetProperty("coordinatedIdentity").GetBoolean()));

        var refused = await InvalidateAsync(w, w.RingVotes);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("telefon klasterinə aid deyil", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, SetAside(w));
        // The address never reaches the page.
        Assert.DoesNotContain("10.77", await (await w.Admin.GetAsync(
            $"/api/admin/competitions/{w.Competition}/voting/suspicion/works/{w.Fraud.Id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Switched_on_an_admin_can_set_R2_votes_aside_with_a_reason_and_an_audit_entry_each()
    {
        using var w = await WorldAsync(r2: true);
        var review = await ReviewAsync(w, w.Fraud);
        Assert.True(review.GetProperty("evidence").GetProperty("coordinatedIdentityEnabled").GetBoolean());
        Assert.All(review.GetProperty("votes").EnumerateArray().Where(v => w.RingVotes.Contains(v.GetProperty("voteId").GetInt64())),
            v => Assert.Equal("CoordinatedIdentity", v.GetProperty("evidence").GetString()));

        var decided = await Json(await InvalidateAsync(w, w.RingVotes));
        Assert.Equal(5, decided.GetProperty("changed").GetInt32());
        Assert.Equal(5, SetAside(w));
        Assert.Equal(5, w.Factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteInvalidated")));
        Assert.All(w.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().Where(v => w.RingVotes.Contains(v.Id)).ToList()),
            v => Assert.Equal(Reason, v.InvalidationReason));

        var board = await Json(await w.Factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/leaderboard"));
        var counts = board.GetProperty("entries").EnumerateArray()
            .ToDictionary(x => x.GetProperty("reference").GetString()!, x => x.GetProperty("voteCount").GetInt32());
        Assert.Equal(3, counts[w.Fraud.Reference]);   // the three older namesakes still count
        Assert.Equal(3, counts[w.Honest.Reference]);

        // The public audit names the rule in public words, and nothing about the people, the address or the reason.
        var res = await w.Factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/audit");
        var body = await res.Content.ReadAsStringAsync();
        var audit = await Json(res);
        var entry = Assert.Single(audit.GetProperty("entries").EnumerateArray());
        Assert.Equal(5, entry.GetProperty("votes").GetInt32());
        Assert.Equal("CoordinatedAccounts", entry.GetProperty("reason").GetString());
        Assert.Equal(6, audit.GetProperty("validVotes").GetInt32());
        Assert.Equal(5, audit.GetProperty("invalidatedVotes").GetInt32());
        foreach (var hidden in new[] { "10.77", "Fədayə", "Fedaye", "Qurbanova", Reason, w.Fraud.Reference, "userId", "admin" })
            Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_vote_outside_the_rule_refuses_the_whole_batch()
    {
        using var w = await WorldAsync(r2: true);
        foreach (var extra in new[] { w.OldVotes[0], w.CrowdVotes[0] })
        {
            var res = await InvalidateAsync(w, [.. w.RingVotes, extra]);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Contains("nə telefon klasterinə, nə də R2", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, SetAside(w));
        Assert.Equal(0, w.Factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteInvalidated")));
    }

    [Fact]
    public async Task Votes_set_aside_before_R2_stay_exactly_as_they_were()
    {
        using var w = await WorldAsync(r2: true);
        // As the 131 phone-cluster votes on production look: set aside earlier, with their own reason and time.
        var earlier = DateTimeOffset.UtcNow.AddDays(-1);
        earlier = new DateTimeOffset(earlier.Ticks - earlier.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        const string phoneReason = "Eyni telefon nömrəsinin 3 və ya daha çox səsverən hesab arasında təkrar istifadəsi.";
        var legacy = w.CrowdVotes[0];
        await w.Factory.WithDbAsync(db => db.Votes.IgnoreQueryFilters().Where(v => v.Id == legacy)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.InvalidatedAt, earlier).SetProperty(v => v.InvalidationReason, phoneReason)));

        await Json(await InvalidateAsync(w, w.RingVotes));

        var row = w.Factory.WithDb(db => db.Votes.IgnoreQueryFilters().Single(v => v.Id == legacy));
        Assert.Equal((earlier, phoneReason), (row.InvalidatedAt!.Value, row.InvalidationReason));
        Assert.Equal(6, SetAside(w));
        // Choosing it again is a conflict, not a second decision.
        Assert.Equal(HttpStatusCode.Conflict, (await InvalidateAsync(w, [legacy])).StatusCode);
    }
}
