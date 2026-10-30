using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// D12: an admin sets fraudulent votes aside — never deletes them — with a reason, in the audit log, all or none,
/// and only on identity evidence. What is set aside counts nowhere public, at once, and the voter's one vote stays
/// spent. One phone, one account for new registrations.
/// </summary>
public sealed class VoteInvalidationTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory;
    private readonly DateTimeOffset _t0;

    public VoteInvalidationTests() : this(cacheSeconds: 0) { }

    private VoteInvalidationTests(int cacheSeconds)
    {
        _factory = new MeydanFactory
        {
            Settings =
            {
                ["Email:OutboxWorkerEnabled"] = "false",
                ["Voting:LeaderboardCacheSeconds"] = cacheSeconds.ToString(),
            },
        };
        var now = DateTimeOffset.UtcNow;
        _t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        _factory.Clock.Now = _t0;
    }

    public void Dispose() => _factory.Dispose();

    // ---- Helpers --------------------------------------------------------------------------------

    private sealed record Work(int Id, string Reference, HttpClient Owner);

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

    private async Task<HttpClient> AccountAsync(string first, string last, string? phone = null)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = phone ?? TestPhones.Next(),
            email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await Json(res)).GetProperty("accessToken").GetString()!);
        return client;
    }

    private async Task<Work> SubmitAsync(string settlement, string title, string first, string last)
    {
        var client = await AccountAsync(first, last);
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title, concept = "K", symbols = "S" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return new Work(id, submitted.GetProperty("referenceNumber").GetString()!, client);
    }

    private async Task<Guid> IdOf(HttpClient client) =>
        (await Json(await client.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();

    /// <summary>Accounts opened before D12 could share one phone; this is how they look in the database.</summary>
    private async Task GivePhoneAsync(string phone, params HttpClient[] accounts)
    {
        var ids = new List<Guid>();
        foreach (var a in accounts) ids.Add(await IdOf(a));
        await _factory.WithDbAsync(async db =>
        {
            foreach (var u in await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync()) u.PhoneNumber = phone;
            await db.SaveChangesAsync();
        });
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private async Task<int> CompetitionIdAsync(HttpClient admin) =>
        (await Json(await admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
        .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();

    private Task OpenWindowAsync() =>
        _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = _t0.AddSeconds(5);
            c.VotingEndsAt = _t0.AddSeconds(20);
            await db.SaveChangesAsync();
        });

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, string reference) =>
        client.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null);

    private static async Task<(int Rank, string Reference, int Votes)[]> RankingAsync(HttpClient client) =>
        (await Json(await client.GetAsync($"/api/competitions/{Chevron}/voting/leaderboard")))
        .GetProperty("entries").EnumerateArray()
        .Select(e => (e.GetProperty("rank").GetInt32(), e.GetProperty("reference").GetString()!, e.GetProperty("voteCount").GetInt32()))
        .ToArray();

    private static string Url(int competition, string tail) => $"/api/admin/competitions/{competition}/voting/{tail}";

    private sealed record Race(
        HttpClient Admin, int Competition, Work Fraud, Work Honest,
        List<HttpClient> Ring, List<HttpClient> Crowd, HttpClient Spare);

    /// <summary>
    /// Two works. "Fraud" gets four votes from a ring of accounts on one phone (identity evidence) and one honest
    /// vote; "Honest" gets three votes from ordinary accounts — sharing only the test server's connection, which is
    /// suspicion and never evidence. Everyone is registered before the window, then the clock moves into it.
    /// </summary>
    private async Task<Race> RaceAsync()
    {
        var fraud = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var honest = await SubmitAsync("ramana", "Ramana qalası", "Nigar", "Həsənova");
        var ring = new List<HttpClient>();
        for (var i = 0; i < 4; i++) ring.Add(await AccountAsync("Saxta", "Hesab"));
        var crowd = new List<HttpClient>();
        foreach (var (f, l) in new[] { ("Orxan", "Məmmədov"), ("Aysel", "Əliyeva"), ("Kamran", "Babayev") })
            crowd.Add(await AccountAsync(f, l));
        var spare = await AccountAsync("Rəşad", "İsmayılov");
        await GivePhoneAsync("+994 55 999 88 77", [.. ring]);

        var admin = await AdminAsync();
        var competition = await CompetitionIdAsync(admin);
        await OpenWindowAsync();
        _factory.Clock.Now = _t0.AddSeconds(10);

        foreach (var c in ring) await Json(await VoteAsync(c, fraud.Reference));
        await Json(await VoteAsync(spare, fraud.Reference));
        foreach (var c in crowd) await Json(await VoteAsync(c, honest.Reference));
        return new Race(admin, competition, fraud, honest, ring, crowd, spare);
    }

    private async Task<JsonElement> ReviewAsync(Race r, Work w) =>
        await Json(await r.Admin.GetAsync(Url(r.Competition, $"suspicion/works/{w.Id}")));

    private static long[] RingVoteIds(JsonElement review) =>
        review.GetProperty("votes").EnumerateArray()
            .Where(v => v.GetProperty("evidence").GetString() == "PhoneCluster")
            .Select(v => v.GetProperty("voteId").GetInt64()).ToArray();

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, int competition, string action, object body) =>
        client.PostAsJsonAsync(Url(competition, $"votes/{action}"), body);

    private const string Reason = "Eyni telefonla açılmış 4 hesab — bir nəfər, bir neçə səs.";

    // ---- Tests ----------------------------------------------------------------------------------

    [Fact]
    public async Task Set_aside_votes_stay_in_the_database_and_leave_every_public_count_at_once()
    {
        var r = await RaceAsync();
        Assert.Equal([(1, r.Fraud.Reference, 5), (2, r.Honest.Reference, 3)], await RankingAsync(r.Spare));

        var ring = RingVoteIds(await ReviewAsync(r, r.Fraud));
        Assert.Equal(4, ring.Length);
        var decided = await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring, reason = Reason }));
        Assert.Equal(4, decided.GetProperty("changed").GetInt32());

        // Kept, all of them, with who, when and why.
        var rows = _factory.WithDb(db => db.Votes.IgnoreQueryFilters().OrderBy(v => v.Id).ToList());
        Assert.Equal(8, rows.Count);
        var setAside = rows.Where(v => ring.Contains(v.Id)).ToList();
        Assert.All(setAside, v =>
        {
            Assert.Equal(_factory.Clock.Now, v.InvalidatedAt);
            Assert.NotNull(v.InvalidatedById);
            Assert.Equal(Reason, v.InvalidationReason);
            Assert.Equal(r.Fraud.Id, v.SubmissionId);   // the vote's own facts are untouched
        });

        // The ranking re-ranks on valid votes; "Honest" now leads. The ballot and the summary agree.
        Assert.Equal([(1, r.Honest.Reference, 3), (2, r.Fraud.Reference, 1)], await RankingAsync(_factory.CreateClient()));
        var works = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/works"));
        Assert.Equal(1, works.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("reference").GetString() == r.Fraud.Reference).GetProperty("voteCount").GetInt32());
        var summary = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal(4, summary.GetProperty("voteCount").GetInt32());
        Assert.Equal(4, summary.GetProperty("voterCount").GetInt32());

        // Nothing about the decision or the people reaches the public answer.
        var body = await (await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/leaderboard")).Content.ReadAsStringAsync();
        foreach (var hidden in new[] { "invalid", "reason", "Saxta", "+994", "score", "evidence" })
            Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
    }

    private Task<HttpResponseMessage> AuditAsync() => _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting/audit");

    [Fact]
    public async Task The_public_audit_counts_every_vote_and_names_each_decision_by_its_rule_and_nothing_else()
    {
        var r = await RaceAsync();
        var before = await Json(await AuditAsync());
        Assert.Equal(8, before.GetProperty("validVotes").GetInt32());
        Assert.Equal(0, before.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(8, before.GetProperty("totalVotes").GetInt32());
        Assert.Empty(before.GetProperty("entries").EnumerateArray());

        var ring = RingVoteIds(await ReviewAsync(r, r.Fraud));
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring, reason = Reason }));

        // At once — the decision drops the shared copy — and from the votes themselves.
        var res = await AuditAsync();
        var body = await res.Content.ReadAsStringAsync();
        var after = await Json(res);
        Assert.Equal(4, after.GetProperty("validVotes").GetInt32());
        Assert.Equal(4, after.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(8, after.GetProperty("totalVotes").GetInt32());
        var entry = Assert.Single(after.GetProperty("entries").EnumerateArray());
        Assert.Equal(4, entry.GetProperty("votes").GetInt32());
        Assert.Equal("DuplicatePhone", entry.GetProperty("reason").GetString());
        Assert.Equal(_factory.Clock.Now, entry.GetProperty("at").GetDateTimeOffset());

        // Aggregates only: no voter, phone, account, admin, work or the admin's own words.
        var ids = new List<string>();
        foreach (var c in r.Ring) ids.Add((await IdOf(c)).ToString());
        foreach (var hidden in new[] { "Saxta", "Hesab", "+994", "559998877", "Eyni telefonla", r.Fraud.Reference, r.Honest.Reference,
                     "admin", "userId", "invalidatedBy", "reason\":\"Eyni" }.Concat(ids))
            Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);

        // The ranking and the audit agree: valid votes only.
        var ranking = await RankingAsync(_factory.CreateClient());
        Assert.Equal(after.GetProperty("validVotes").GetInt32(), ranking.Sum(x => x.Votes));

        // Still served once the vote is over; and nothing here ever touches the official result.
        _factory.Clock.Now = _t0.AddDays(2);
        var closed = await Json(await AuditAsync());
        Assert.Equal("Closed", closed.GetProperty("state").GetString());
        Assert.Equal(4, closed.GetProperty("invalidatedVotes").GetInt32());
        Assert.Equal(0, _factory.WithDb(db => db.CompetitionResults.Count(x => x.CompetitionId == r.Competition)));
        Assert.Equal(0, _factory.WithDb(db => db.CompetitionCategoryWinners.Count(x => x.CompetitionId == r.Competition)));
        _factory.Clock.Now = _t0.AddSeconds(10);

        // Restored, the line goes and the counts come back.
        await Json(await DecideAsync(r.Admin, r.Competition, "restore", new { voteIds = ring, reason = "Yoxlama səhvi — geri qaytarıldı." }));
        var restored = await Json(await AuditAsync());
        Assert.Equal(8, restored.GetProperty("validVotes").GetInt32());
        Assert.Empty(restored.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task The_admin_sees_raw_valid_and_set_aside_totals_side_by_side()
    {
        var r = await RaceAsync();
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = RingVoteIds(await ReviewAsync(r, r.Fraud)), reason = Reason }));

        var review = await ReviewAsync(r, r.Fraud);
        var e = review.GetProperty("evidence");
        Assert.Equal((5, 1, 4), (e.GetProperty("rawVotes").GetInt32(), e.GetProperty("validVotes").GetInt32(), e.GetProperty("invalidatedVotes").GetInt32()));
        Assert.Equal((4, 4), (e.GetProperty("repeatedPhoneVotes").GetInt32(), e.GetProperty("largestPhoneGroup").GetInt32()));
        // Groups are labels with sizes; the phone itself is never in the answer.
        var raw = await (await r.Admin.GetAsync(Url(r.Competition, $"suspicion/works/{r.Fraud.Id}"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("999", raw);
        Assert.Contains("\"phoneGroup\":\"T1\"", raw);
        var aside = review.GetProperty("votes").EnumerateArray().Where(v => v.GetProperty("invalidatedAt").ValueKind != JsonValueKind.Null).ToList();
        Assert.Equal(4, aside.Count);
        Assert.All(aside, v => Assert.Equal(Reason, v.GetProperty("invalidationReason").GetString()));

        var overview = await Json(await r.Admin.GetAsync(Url(r.Competition, "suspicion")));
        Assert.Equal((4, 4), (overview.GetProperty("voteCount").GetInt32(), overview.GetProperty("invalidatedCount").GetInt32()));
        var fraud = overview.GetProperty("works").EnumerateArray().Single(w => w.GetProperty("reference").GetString() == r.Fraud.Reference);
        Assert.Equal((1, 5, 4), (fraud.GetProperty("votes").GetInt32(), fraud.GetProperty("rawVotes").GetInt32(), fraud.GetProperty("invalidatedVotes").GetInt32()));

        var tally = await Json(await r.Admin.GetAsync(Url(r.Competition, "")));
        Assert.Equal((4, 4), (tally.GetProperty("voteCount").GetInt32(), tally.GetProperty("invalidatedVoteCount").GetInt32()));
        var row = tally.GetProperty("nominations").EnumerateArray().SelectMany(n => n.GetProperty("works").EnumerateArray())
            .Single(w => w.GetProperty("reference").GetString() == r.Fraud.Reference);
        Assert.Equal((1, 4), (row.GetProperty("voteCount").GetInt32(), row.GetProperty("invalidatedVoteCount").GetInt32()));
    }

    [Fact]
    public async Task Only_an_admin_decides_and_always_with_a_reason_and_the_decision_is_audited()
    {
        var r = await RaceAsync();
        var ring = RingVoteIds(await ReviewAsync(r, r.Fraud));
        var body = new { voteIds = ring, reason = Reason };

        Assert.Equal(HttpStatusCode.Unauthorized, (await DecideAsync(_factory.CreateClient(), r.Competition, "invalidate", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(r.Spare, r.Competition, "invalidate", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(r.Fraud.Owner, r.Competition, "invalidate", body)).StatusCode);

        foreach (var reason in new string?[] { null, "", "   ", "saxta" })
            Assert.Contains("\"reason\"", await Refused(
                await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring, reason }), HttpStatusCode.BadRequest));
        Assert.Contains("\"voteIds\"", await Refused(
            await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = Array.Empty<long>(), reason = Reason }), HttpStatusCode.BadRequest));
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));

        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", body));
        var entries = _factory.WithDb(db => db.AuditLogs.Where(a => a.Action == "VoteInvalidated").ToList());
        Assert.Equal(4, entries.Count);
        Assert.All(entries, a =>
        {
            Assert.Equal("Vote", a.EntityType);
            Assert.Equal($"{r.Fraud.Reference}: {Reason}", a.Summary);
            Assert.NotNull(a.UserId);
        });
        Assert.Equal(ring.Select(id => id.ToString()).Order(), entries.Select(a => a.EntityId).Order());
    }

    [Fact]
    public async Task A_decision_is_all_or_nothing()
    {
        var r = await RaceAsync();
        var review = await ReviewAsync(r, r.Fraud);
        var ring = RingVoteIds(review);
        var honestVote = review.GetProperty("votes").EnumerateArray()
            .Single(v => v.GetProperty("evidence").GetString() != "PhoneCluster").GetProperty("voteId").GetInt64();

        // One weak vote among four strong ones: refused whole.
        await Refused(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring.Append(honestVote), reason = Reason }),
            HttpStatusCode.UnprocessableEntity);
        // An id that is not a vote of this competition: refused whole.
        await Refused(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring.Append(987654L), reason = Reason }),
            HttpStatusCode.NotFound);
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
        Assert.Equal(0, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteInvalidated")));

        // Two of the four already set aside: a second decision over all four changes nothing.
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring.Take(2), reason = Reason }));
        await Refused(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring, reason = Reason }), HttpStatusCode.Conflict);
        Assert.Equal(2, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
        Assert.Equal(2, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteInvalidated")));
    }

    [Fact]
    public async Task A_shared_connection_alone_never_sets_a_vote_aside()
    {
        var r = await RaceAsync();
        // Every account here came through the same test connection, and the three on "Honest" voted together.
        var review = await ReviewAsync(r, r.Honest);
        Assert.All(review.GetProperty("votes").EnumerateArray(), v => Assert.NotEqual("PhoneCluster", v.GetProperty("evidence").GetString()));
        // Reading the review sets nothing aside by itself…
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
        // …and an admin cannot do it either.
        var ids = review.GetProperty("votes").EnumerateArray().Select(v => v.GetProperty("voteId").GetInt64()).ToArray();
        Assert.Contains("telefon klasterinə aid deyil", await Refused(
            await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ids, reason = "Hamısı eyni IP-dən gəlib, şübhəlidir." }),
            HttpStatusCode.UnprocessableEntity));
        Assert.Equal(3, (await RankingAsync(r.Spare)).Single(x => x.Reference == r.Honest.Reference).Votes);
    }

    // ---- D12 as narrowed: a phone cluster of three or more, and nothing else ---------------------------

    /// <summary>A work whose votes carry every review-only sign the scorer knows, but no phone cluster.</summary>
    private async Task<(HttpClient Admin, int Competition, Work Target, List<HttpClient> Voters)> ReviewOnlyAsync(
        Func<Work, List<HttpClient>, Task> shape, int voters, string first = "Səbinə", string last = "Rzayeva", bool openedDuringVote = false)
    {
        var target = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var admin = await AdminAsync();
        var competition = await CompetitionIdAsync(admin);
        await OpenWindowAsync();
        if (openedDuringVote) _factory.Clock.Now = _t0.AddSeconds(10);
        var list = new List<HttpClient>();
        for (var i = 0; i < voters; i++) list.Add(await AccountAsync(first, last));
        await shape(target, list);
        _factory.Clock.Now = _t0.AddSeconds(10);
        foreach (var c in list) await Json(await VoteAsync(c, target.Reference));
        return (admin, competition, target, list);
    }

    private async Task<long[]> AllVoteIdsAsync(HttpClient admin, int competition, Work w) =>
        (await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{w.Id}"))))
        .GetProperty("votes").EnumerateArray().Select(v => v.GetProperty("voteId").GetInt64()).ToArray();

    private async Task AssertRefusedAsync(HttpClient admin, int competition, long[] ids)
    {
        Assert.Contains("telefon klasterinə aid deyil", await Refused(
            await DecideAsync(admin, competition, "invalidate", new { voteIds = ids, reason = "Yoxlama: bu əlamət kifayət etməməlidir." }),
            HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
    }

    [Fact]
    public async Task A_phone_shared_by_only_two_accounts_is_refused()
    {
        var (admin, competition, target, _) = await ReviewOnlyAsync((_, voters) => GivePhoneAsync("+994 51 777 66 55", [.. voters]), 2, "Elvin", "Abbasov");
        var review = await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{target.Id}")));
        Assert.All(review.GetProperty("votes").EnumerateArray(), v => Assert.Equal(2, v.GetProperty("phoneGroupSize").GetInt32()));
        await AssertRefusedAsync(admin, competition, await AllVoteIdsAsync(admin, competition, target));
    }

    [Fact]
    public async Task A_name_repeated_on_new_accounts_is_review_only_and_refused()
    {
        // Three accounts opened during the vote, one name, three different phones.
        var (admin, competition, target, _) = await ReviewOnlyAsync((_, _) => Task.CompletedTask, 3, openedDuringVote: true);
        var review = await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{target.Id}")));
        Assert.All(review.GetProperty("votes").EnumerateArray(), v =>
        {
            Assert.Equal("Weak", v.GetProperty("evidence").GetString());
            Assert.Equal(3, v.GetProperty("nameGroupSize").GetInt32());
        });
        await AssertRefusedAsync(admin, competition, await AllVoteIdsAsync(admin, competition, target));
    }

    [Fact]
    public async Task The_authors_own_phone_on_a_voting_account_is_review_only_and_refused()
    {
        var (admin, competition, target, voters) = await ReviewOnlyAsync(async (work, list) =>
        {
            var authorPhone = (await Json(await work.Owner.GetAsync("/api/me/profile"))).GetProperty("phone").GetString()!;
            await GivePhoneAsync(authorPhone, list[0]);
        }, 1, "Tofiq", "Vəliyev");
        var e = (await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{target.Id}")))).GetProperty("evidence");
        Assert.Equal(1, e.GetProperty("authorPhoneVotes").GetInt32());
        Assert.Equal(0, e.GetProperty("phoneClusterVotes").GetInt32());
        await AssertRefusedAsync(admin, competition, await AllVoteIdsAsync(admin, competition, target));
    }

    [Fact]
    public async Task Every_weak_sign_together_without_a_phone_cluster_is_still_refused()
    {
        // One name on three new accounts, one of them on the author's phone, all through one connection, voting at once.
        var (admin, competition, target, _) = await ReviewOnlyAsync(async (work, list) =>
        {
            var authorPhone = (await Json(await work.Owner.GetAsync("/api/me/profile"))).GetProperty("phone").GetString()!;
            await GivePhoneAsync(authorPhone, list[0]);
        }, 3, openedDuringVote: true);
        var review = await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{target.Id}")));
        Assert.All(review.GetProperty("votes").EnumerateArray(), v => Assert.Equal("Weak", v.GetProperty("evidence").GetString()));
        await AssertRefusedAsync(admin, competition, await AllVoteIdsAsync(admin, competition, target));
    }

    /// <summary>
    /// A vote set aside under the earlier, wider rule (a repeated name, say) stays set aside: narrowing the rule
    /// re-decides nothing. It is listed, counted as set aside, and can still be restored by a person, with a reason.
    /// </summary>
    [Fact]
    public async Task Votes_set_aside_earlier_stay_set_aside_when_the_rule_narrows()
    {
        var (admin, competition, target, _) = await ReviewOnlyAsync((_, _) => Task.CompletedTask, 3, openedDuringVote: true);
        var adminId = await IdOf(admin);
        var ids = await AllVoteIdsAsync(admin, competition, target);
        await _factory.WithDbAsync(async db =>
        {
            var v = await db.Votes.IgnoreQueryFilters().SingleAsync(x => x.Id == ids[0]);
            v.InvalidatedAt = _factory.Clock.Now;
            v.InvalidatedById = adminId;
            v.InvalidationReason = "Əvvəlki qayda: eyni ad üç yeni hesabda.";
            await db.SaveChangesAsync();
        });

        var review = await Json(await admin.GetAsync(Url(competition, $"suspicion/works/{target.Id}")));
        Assert.Equal((3, 2, 1), (review.GetProperty("evidence").GetProperty("rawVotes").GetInt32(),
            review.GetProperty("evidence").GetProperty("validVotes").GetInt32(),
            review.GetProperty("evidence").GetProperty("invalidatedVotes").GetInt32()));
        Assert.Equal(2, (await RankingAsync(_factory.CreateClient())).Single(x => x.Reference == target.Reference).Votes);
        await Json(await DecideAsync(admin, competition, "restore", new { voteIds = new[] { ids[0] }, reason = "Yoxlama: qayda daraldı, səs bərpa olunur." }));
        Assert.Equal(3, (await RankingAsync(_factory.CreateClient())).Single(x => x.Reference == target.Reference).Votes);
    }

    [Fact]
    public async Task A_set_aside_vote_still_spends_the_voters_one_vote_and_can_be_restored_with_a_reason()
    {
        var r = await RaceAsync();
        var ring = RingVoteIds(await ReviewAsync(r, r.Fraud));
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring, reason = Reason }));

        // Setting a vote aside does not hand its voter a new one.
        Assert.Contains("artıq səs vermisiniz", await Refused(await VoteAsync(r.Ring[0], r.Honest.Reference), HttpStatusCode.Conflict));
        var viewer = (await Json(await r.Ring[0].GetAsync($"/api/competitions/{Chevron}/voting"))).GetProperty("viewer");
        Assert.False(viewer.GetProperty("mayVote").GetBoolean());

        // Restoring needs a reason too, then counts again — and is audited.
        await Refused(await DecideAsync(r.Admin, r.Competition, "restore", new { voteIds = ring, reason = "" }), HttpStatusCode.BadRequest);
        await Json(await DecideAsync(r.Admin, r.Competition, "restore", new { voteIds = ring.Take(1), reason = "Yoxlama: bu hesab ayrıca şəxsə məxsusdur." }));
        Assert.Equal(2, (await RankingAsync(r.Spare)).Single(x => x.Reference == r.Fraud.Reference).Votes);
        var restored = _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Single(v => v.Id == ring[0]));
        Assert.Null(restored.InvalidatedAt);
        Assert.Null(restored.InvalidationReason);
        Assert.Equal(1, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteRestored")));
        // A vote that counts cannot be "restored" again.
        await Refused(await DecideAsync(r.Admin, r.Competition, "restore", new { voteIds = ring.Take(1), reason = "Yenidən bərpa cəhdi." }), HttpStatusCode.Conflict);
    }

    /// <summary>Whatever writes the row, the database will not hold a vote set aside without a reason.</summary>
    [Fact]
    public async Task The_database_refuses_a_vote_set_aside_without_a_reason()
    {
        var r = await RaceAsync();
        var id = RingVoteIds(await ReviewAsync(r, r.Fraud))[0];
        foreach (var reason in new string?[] { null, "   " })
        {
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => _factory.WithDbAsync(db =>
                db.Database.ExecuteSqlAsync(
                    $"UPDATE \"Votes\" SET \"InvalidatedAt\" = now(), \"InvalidationReason\" = {reason} WHERE \"Id\" = {id}")));
            Assert.Equal("CK_Votes_InvalidationHasReason", ex.ConstraintName);
        }
        Assert.Equal(0, _factory.WithDb(db => db.Votes.IgnoreQueryFilters().Count(v => v.InvalidatedAt != null)));
    }

    [Fact]
    public async Task Ties_and_ranks_are_recomputed_from_valid_votes()
    {
        var r = await RaceAsync();
        var ring = RingVoteIds(await ReviewAsync(r, r.Fraud));
        // Two of the ring set aside: 5 − 2 = 3, level with "Honest" — a shared first place.
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate", new { voteIds = ring.Take(2), reason = Reason }));
        var ranking = await RankingAsync(r.Spare);
        Assert.All(ranking.Where(x => x.Reference == r.Fraud.Reference || x.Reference == r.Honest.Reference),
            x => Assert.Equal((1, 3), (x.Rank, x.Votes)));
    }

    /// <summary>The shared ranking at its production setting (kept a minute here): a decision drops it at once.</summary>
    [Fact]
    public async Task A_set_aside_vote_never_reappears_through_the_shared_ranking()
    {
        using var t = new VoteInvalidationTests(cacheSeconds: 60);
        var r = await t.RaceAsync();
        Assert.Equal(5, (await RankingAsync(r.Spare)).Single(x => x.Reference == r.Fraud.Reference).Votes);   // now shared
        await Json(await DecideAsync(r.Admin, r.Competition, "invalidate",
            new { voteIds = RingVoteIds(await t.ReviewAsync(r, r.Fraud)), reason = Reason }));
        Assert.Equal(1, (await RankingAsync(r.Spare)).Single(x => x.Reference == r.Fraud.Reference).Votes);
        Assert.Equal(1, (await RankingAsync(t._factory.CreateClient())).Single(x => x.Reference == r.Fraud.Reference).Votes);
    }

    [Fact]
    public async Task Registration_and_profile_refuse_a_phone_another_account_holds()
    {
        await AccountAsync("Aysel", "Məmmədova", "+994 50 123 45 67");
        // The same number written another way.
        var again = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Aysel", lastName = "Məmmədova", phone = "050-123-45-67",
            email = $"p-{Guid.NewGuid():N}@example.com", password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        Assert.Contains("\"phone\"", await Refused(again, HttpStatusCode.BadRequest));

        var other = await AccountAsync("Kamran", "Babayev");
        var change = await other.PutAsJsonAsync("/api/me/profile", new { firstName = "Kamran", lastName = "Babayev", phone = "0501234567" });
        Assert.Contains("\"phone\"", await Refused(change, HttpStatusCode.BadRequest));
        // Keeping one's own number, written differently, is fine.
        var own = (await Json(await other.GetAsync("/api/me/profile"))).GetProperty("phone").GetString()!;
        await Json(await other.PutAsJsonAsync("/api/me/profile", new { firstName = "Kamran", lastName = "B.", phone = own.Replace(" ", "") }));
    }

    [Fact]
    public async Task Accounts_that_already_share_a_phone_keep_working()
    {
        var a = await AccountAsync("Fədayə", "Qurbanova");
        var b = await AccountAsync("Fədayə", "Qurbanova");
        await GivePhoneAsync("+994 51 222 33 44", a, b);
        // Both still read and update their profile; neither is locked out by the new rule.
        foreach (var c in new[] { a, b })
        {
            var me = await Json(await c.GetAsync("/api/me/profile"));
            await Json(await c.PutAsJsonAsync("/api/me/profile",
                new { firstName = "Fədayə", lastName = "Qurbanova", phone = me.GetProperty("phone").GetString() }));
        }
    }
}
