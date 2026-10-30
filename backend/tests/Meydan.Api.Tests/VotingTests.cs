using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 12: the public vote. One vote per person per nomination, enforced by the database rather than by a
/// disabled button; a ballot that shows the work and never its author; and a window an admin opens and closes
/// without touching the entry deadline. The rules and where each came from are in docs/voting/OWNER-DECISIONS.md.
/// </summary>
public sealed class VotingTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    // EF's own command log is on, so one test can count what a page of the ballot costs; the outbox worker is
    // off, because its poll would otherwise be counted as part of whatever request happened to be measured.
    private readonly MeydanFactory _factory = new()
    {
        Settings =
        {
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information",
            ["Email:OutboxWorkerEnabled"] = "false",
            // Every read of the live ranking builds it afresh here, so a test that changes a work and reads again
            // sees the change; the shared copy has its own test below.
            ["Voting:LeaderboardCacheSeconds"] = "0",
        },
    };

    /// <summary>
    /// When the tests start: accounts are opened here and the window opens five seconds later. Whole seconds, so
    /// a date read back out of Postgres is the one that went in, and fixed once, so moving the clock in a test
    /// cannot move the window with it.
    ///
    /// <para>
    /// The whole window is seconds wide rather than hours because an access token is minted on this clock and
    /// checked against the real one, which allows thirty seconds of difference. Nothing here needs longer: the
    /// states are decided by comparisons, not by durations.
    /// </para>
    /// </summary>
    private readonly DateTimeOffset _t0;

    public VotingTests()
    {
        var now = DateTimeOffset.UtcNow;
        _t0 = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        _factory.Clock.Now = _t0;
    }

    public void Dispose() => _factory.Dispose();

    // ---- Helpers --------------------------------------------------------------------------------

    private sealed record Work(int Id, string Reference, HttpClient Owner, string Name);

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

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private async Task<HttpClient> ParticipantAsync(string first = "Elçin", string last = "Nəbiyev")
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(),
            email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await Json(res)).GetProperty("accessToken").GetString()!);
        return client;
    }

    private async Task<Work> SubmitAsync(string settlement, string title, string first, string last)
    {
        var client = await ParticipantAsync(first, last);
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title, concept = "Gizli konsept mətni", symbols = "Simvollar", notes = "Şəxsi qeyd" },
        }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", $"{first}_{last}_eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return new Work(id, submitted.GetProperty("referenceNumber").GetString()!, client, $"{first} {last}");
    }

    /// <summary>Turns a form field's "public after results" switch on or off — the gallery's switch, and the ballot's.</summary>
    private static async Task SetFieldPublicAsync(HttpClient admin, int competitionId, string key, bool isPublic)
    {
        var f = (await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}/fields"))).EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == key);
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/fields/{f.GetProperty("id").GetInt32()}", new
        {
            key, label = f.GetProperty("label").GetString(), fieldType = f.GetProperty("fieldType").GetString(),
            isRequired = f.GetProperty("isRequired").GetBoolean(), isSubmissionTitle = f.GetProperty("isSubmissionTitle").GetBoolean(),
            isActive = true, isPublicAfterResults = isPublic,
            optionsJson = f.GetProperty("optionsJson").ValueKind == JsonValueKind.Null ? null : f.GetProperty("optionsJson").GetString(),
        }));
    }

    private async Task<int> ChevronIdAsync(HttpClient admin) =>
        (await Json(await admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
        .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();

    /// <summary>Sets the window directly: the admin screen for it is Phase 7D's, the rule it expresses is this one's.</summary>
    private Task WindowAsync(DateTimeOffset? from, DateTimeOffset? to) =>
        _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = from;
            c.VotingEndsAt = to;
            await db.SaveChangesAsync();
        });

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, string reference) =>
        client.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null);

    private static Task<HttpResponseMessage> WorksAsync(HttpClient client, string query = "") =>
        client.GetAsync($"/api/competitions/{Chevron}/voting/works{query}");

    private int VoteCount() => _factory.WithDb(db => db.Votes.Count());

    /// <summary>
    /// Gives <paramref name="account"/> the phone of <paramref name="source"/>, straight in the database: how accounts
    /// opened before one phone meant one account (D12) still look.
    /// </summary>
    private async Task SharePhoneAsync(HttpClient account, HttpClient source)
    {
        var target = (await Json(await account.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();
        var from = (await Json(await source.GetAsync("/api/me/profile"))).GetProperty("id").GetGuid();
        await _factory.WithDbAsync(async db =>
        {
            var phone = await db.Users.Where(u => u.Id == from).Select(u => u.PhoneNumber).SingleAsync();
            var user = await db.Users.SingleAsync(u => u.Id == target);
            user.PhoneNumber = phone;
            await db.SaveChangesAsync();
        });
    }

    private static Task<HttpResponseMessage> LeaderboardAsync(HttpClient client) =>
        client.GetAsync($"/api/competitions/{Chevron}/voting/leaderboard");

    /// <summary>The ranking as (rank, reference, votes), top first.</summary>
    private static (int Rank, string Reference, int Votes)[] Ranked(JsonElement board) =>
        board.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("rank").GetInt32(), e.GetProperty("reference").GetString()!, e.GetProperty("voteCount").GetInt32()))
            .ToArray();

    private static JsonElement Item(JsonElement works, string reference) =>
        works.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("reference").GetString() == reference);

    private sealed record Ballot(
        HttpClient Admin, int Competition, HttpClient Voter, HttpClient SecondVoter,
        Work Balaxani, Work BalaxaniOther, Work Ramana, Work Rejected);

    /// <summary>
    /// Three works that can be voted for and one that cannot, everyone registered before the window; then the
    /// clock is moved into an open window. The voter is a bystander, not a participant.
    /// </summary>
    private async Task<Ballot> OpenBallotAsync()
    {
        var balaxani = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var other = await SubmitAsync("balaxani", "Balaxanı — ikinci iş", "Səbinə", "Rzayeva");
        var ramana = await SubmitAsync("ramana", "Ramana qalası", "Nigar", "Həsənova");
        var rejected = await SubmitAsync("nardaran", "Rədd olunan iş", "Kamran", "Babayev");
        var voter = await ParticipantAsync();
        // A second bystander, opened before the window like the first: D11 refuses anybody who arrives later.
        var second = await ParticipantAsync("Rəşad", "İsmayılov");

        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{rejected.Id}/status", new { status = "Rejected" }));

        await WindowAsync(_t0.AddSeconds(5), _t0.AddSeconds(20));
        _factory.Clock.Now = _t0.AddSeconds(10);
        return new Ballot(admin, competition, voter, second, balaxani, other, ramana, rejected);
    }

    // ---- The ballot -----------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_window_the_vote_does_not_exist_at_all()
    {
        await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var anonymous = _factory.CreateClient();

        // The same 404 for the competition that has no vote and for one that was never heard of.
        var body = await Refused(await anonymous.GetAsync($"/api/competitions/{Chevron}/voting"), HttpStatusCode.NotFound);
        Assert.Contains("səsvermə yoxdur", body);
        await Refused(await WorksAsync(anonymous), HttpStatusCode.NotFound);
        await Refused(await anonymous.GetAsync("/api/competitions/belə-müsabiqə-yoxdur/voting"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Before_it_opens_the_dates_are_public_but_the_works_are_not()
    {
        var work = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var voter = await ParticipantAsync();
        // A second bystander, opened before the window like the first: D11 refuses anybody who arrives later.
        var second = await ParticipantAsync("Rəşad", "İsmayılov");
        await WindowAsync(_t0.AddSeconds(5), _t0.AddSeconds(20));

        var summary = await Json(await voter.GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal("Upcoming", summary.GetProperty("state").GetString());
        Assert.Equal(_t0.AddSeconds(5), summary.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(_t0.AddSeconds(20), summary.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(0, summary.GetProperty("voteCount").GetInt32());
        Assert.Equal("VotingNotOpen", summary.GetProperty("viewer").GetProperty("reason").GetString());

        // The works are not a preview: until the window opens they are not public at all.
        Assert.Contains("hələ başlamayıb", await Refused(await WorksAsync(voter), HttpStatusCode.NotFound));
        await Refused(await VoteAsync(voter, work.Reference), HttpStatusCode.NotFound);
        Assert.Equal(0, VoteCount());
    }

    [Fact]
    public async Task The_ballot_shows_the_work_and_never_its_author()
    {
        var b = await OpenBallotAsync();
        var anonymous = _factory.CreateClient();

        var works = await Json(await WorksAsync(anonymous));
        Assert.Equal("Open", works.GetProperty("state").GetString());
        Assert.Equal(3, works.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, works.GetProperty("viewer").ValueKind);

        var card = Item(works, b.Balaxani.Reference);
        Assert.Equal("Balaxanı şevronu", card.GetProperty("title").GetString());
        Assert.Equal("Balaxanı", card.GetProperty("nomination").GetProperty("name").GetString());
        Assert.Equal(0, card.GetProperty("voteCount").GetInt32());
        Assert.Equal("NotSignedIn", card.GetProperty("eligibility").GetString());

        // A rejected work is not on the ballot, and cannot be voted for by guessing its reference.
        Assert.DoesNotContain(works.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("reference").GetString() == b.Rejected.Reference);
        await Refused(await VoteAsync(b.Voter, b.Rejected.Reference), HttpStatusCode.NotFound);
        await Refused(await WorksAsync(anonymous, $"/{b.Rejected.Reference}"), HttpStatusCode.NotFound);

        // Nothing of the participant reaches the page: no name, no e-mail, and no answer to a field the admin
        // has not made public — the concept and the private note are both still private here.
        var page = await (await WorksAsync(anonymous)).Content.ReadAsStringAsync();
        var detail = await (await WorksAsync(anonymous, $"/{b.Balaxani.Reference}")).Content.ReadAsStringAsync();
        foreach (var text in new[] { page, detail })
            foreach (var secret in new[] { "Leyla", "Quliyeva", "@example.com", "Gizli konsept", "Şəxsi qeyd" })
                Assert.DoesNotContain(secret, text);
        Assert.Empty((await Json(await WorksAsync(anonymous, $"/{b.Balaxani.Reference}"))).GetProperty("answers").EnumerateArray());
    }

    [Fact]
    public async Task An_answer_reaches_the_ballot_only_when_the_admin_has_made_that_field_public()
    {
        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", true);

        var detail = await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"));
        var answer = Assert.Single(detail.GetProperty("answers").EnumerateArray());
        Assert.Equal("Gizli konsept mətni", answer.GetProperty("value").GetString());

        // The note beside it was not made public, and is still not on the ballot.
        Assert.DoesNotContain("Şəxsi qeyd", await (await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}")).Content.ReadAsStringAsync());

        // Untick it and the ballot forgets it again — the gallery's own switch, governing both surfaces.
        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", false);
        Assert.Empty((await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"))).GetProperty("answers").EnumerateArray());
    }

    [Fact]
    public async Task A_card_carries_the_public_concept_and_none_of_the_private_answers()
    {
        var b = await OpenBallotAsync();

        // Before the admin publishes the field, a card describes nothing.
        var before = await Json(await WorksAsync(b.Voter));
        Assert.All(before.GetProperty("items").EnumerateArray(),
            w => Assert.Equal(JsonValueKind.Null, w.GetProperty("publicConceptDescription").ValueKind));

        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", true);

        var page = await Json(await WorksAsync(b.Voter));
        var card = Item(page, b.Balaxani.Reference);
        Assert.Equal("Gizli konsept mətni", card.GetProperty("publicConceptDescription").GetString());

        // The other long-text answers are private, and the list never carries them — nor the whole answer sheet.
        var body = await (await WorksAsync(b.Voter)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Simvollar", body);
        Assert.DoesNotContain("Şəxsi qeyd", body);
        Assert.DoesNotContain("answers", body);
        foreach (var identity in new[] { "Leyla", "Quliyeva", "@example.com", "userId" })
            Assert.DoesNotContain(identity, body, StringComparison.OrdinalIgnoreCase);

        // Untick it and the line goes, on the same switch that governs the gallery.
        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", false);
        Assert.Equal(JsonValueKind.Null,
            Item(await Json(await WorksAsync(b.Voter)), b.Balaxani.Reference)
                .GetProperty("publicConceptDescription").ValueKind);
    }

    [Fact]
    public async Task A_long_concept_is_cut_to_a_card_and_read_in_full_on_the_work_page()
    {
        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", true);

        // A participant's own answer, as long as they like, with the paragraph breaks they typed.
        var full = string.Join("\n\n", Enumerable.Repeat("Əmircanın memarlıq ənənəsi və Xilə xalçaçılıq xətləri.", 12));
        await _factory.WithDbAsync(async db =>
        {
            var field = await db.CompetitionFields.SingleAsync(f => f.CompetitionId == b.Competition && f.Key == "concept");
            var answer = await db.SubmissionAnswers.SingleAsync(a => a.SubmissionId == b.Balaxani.Id && a.CompetitionFieldId == field.Id);
            answer.Value = full;
            await db.SaveChangesAsync();
        });

        var card = Item(await Json(await WorksAsync(b.Voter)), b.Balaxani.Reference)
            .GetProperty("publicConceptDescription").GetString()!;
        Assert.True(card.Length < full.Length, "the card should carry a preview, not the whole answer");
        Assert.True(card.Length <= 221, $"the preview is {card.Length} characters");
        Assert.EndsWith("…", card);
        Assert.DoesNotContain("\n", card);   // a paragraph break must not stretch a card
        Assert.StartsWith("Əmircanın memarlıq", card);

        // The whole text is still there, on the work's own page, through the answers it already carried.
        var detail = await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"));
        Assert.Equal(full, Assert.Single(detail.GetProperty("answers").EnumerateArray()).GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_draft_and_a_withdrawn_work_are_never_on_the_ballot()
    {
        var b = await OpenBallotAsync();
        // Withdrawal is not a moderation decision and has no admin endpoint; the row is what the ballot must
        // honour, however it came to say this.
        await _factory.WithDbAsync(async db =>
        {
            var withdrawn = await db.Submissions.SingleAsync(x => x.Id == b.Ramana.Id);
            withdrawn.Status = SubmissionStatus.Withdrawn;
            await db.SaveChangesAsync();
        });

        // A draft that was never submitted has no reference at all, so it cannot even be named.
        var drafter = await ParticipantAsync("Kənan", "Səfərov");
        await Json(await drafter.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "bileceri" }));

        var works = await Json(await WorksAsync(b.Voter));
        Assert.Equal(2, works.GetProperty("total").GetInt32());
        Assert.DoesNotContain(works.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("reference").GetString() == b.Ramana.Reference);
        await Refused(await VoteAsync(b.Voter, b.Ramana.Reference), HttpStatusCode.NotFound);
        Assert.Equal(0, VoteCount());
    }

    [Fact]
    public async Task Nomination_and_title_narrow_the_ballot_and_an_unknown_filter_is_simply_empty()
    {
        var b = await OpenBallotAsync();

        var balaxani = await Json(await WorksAsync(b.Voter, "?nomination=balaxani"));
        Assert.Equal(2, balaxani.GetProperty("total").GetInt32());
        Assert.All(balaxani.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("balaxani", i.GetProperty("nomination").GetProperty("slug").GetString()));

        var search = await Json(await WorksAsync(b.Voter, "?q=ramana"));
        Assert.Equal(b.Ramana.Reference, Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());

        Assert.Equal(0, (await Json(await WorksAsync(b.Voter, "?nomination=belə-kənd-yoxdur"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await WorksAsync(b.Voter, "?q=%25"))).GetProperty("total").GetInt32());

        var paged = await Json(await WorksAsync(b.Voter, "?page=1&pageSize=1"));
        Assert.Equal(3, paged.GetProperty("total").GetInt32());
        Assert.Single(paged.GetProperty("items").EnumerateArray());
        Assert.Equal(1, paged.GetProperty("pageSize").GetInt32());
    }

    // ---- Casting --------------------------------------------------------------------------------

    /// <summary>
    /// The owner's rule (4 October 2026): one vote per person per competition. Categories only sort the ballot —
    /// a vote in one is a vote spent in all of them, and every later attempt is refused by the server itself, not
    /// merely hidden by the page.
    /// </summary>
    [Fact]
    public async Task One_vote_per_person_for_the_whole_competition_whatever_the_category()
    {
        var b = await OpenBallotAsync();

        var receipt = await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        Assert.Equal(b.Balaxani.Reference, receipt.GetProperty("reference").GetString());
        Assert.Equal("balaxani", receipt.GetProperty("nominationSlug").GetString());
        Assert.Equal(1, receipt.GetProperty("voteCount").GetInt32());
        Assert.Equal(1, VoteCount());

        // The same work again, another work of the same category, a work of another category: all refused alike.
        foreach (var reference in new[] { b.Balaxani.Reference, b.BalaxaniOther.Reference, b.Ramana.Reference })
            Assert.Contains("artıq səs vermisiniz",
                await Refused(await VoteAsync(b.Voter, reference), HttpStatusCode.Conflict));
        Assert.Equal(1, VoteCount());
        Assert.Equal(1, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteCast")));

        // A fresh read — what a reload gets — says the vote is spent, whatever the filter.
        var works = await Json(await WorksAsync(b.Voter));
        Assert.Equal(1, Item(works, b.Balaxani.Reference).GetProperty("voteCount").GetInt32());
        Assert.Equal("AlreadyVotedForThisWork", Item(works, b.Balaxani.Reference).GetProperty("eligibility").GetString());
        Assert.Equal("AlreadyVotedInNomination", Item(works, b.BalaxaniOther.Reference).GetProperty("eligibility").GetString());
        Assert.Equal("AlreadyVotedInNomination", Item(works, b.Ramana.Reference).GetProperty("eligibility").GetString());
        var viewer = works.GetProperty("viewer");
        Assert.False(viewer.GetProperty("mayVote").GetBoolean());
        Assert.Equal("AlreadyVotedInNomination", viewer.GetProperty("reason").GetString());
        Assert.Equal([b.Balaxani.Reference], viewer.GetProperty("votedWorks").EnumerateArray().Select(x => x.GetString()!).ToArray());

        var ramanaOnly = await Json(await WorksAsync(b.Voter, "?nomination=ramana"));
        Assert.Equal("AlreadyVotedInNomination", Item(ramanaOnly, b.Ramana.Reference).GetProperty("eligibility").GetString());
        Assert.False(ramanaOnly.GetProperty("viewer").GetProperty("mayVote").GetBoolean());
        var ramanaPage = await Json(await WorksAsync(b.Voter, $"/{b.Ramana.Reference}"));
        Assert.Equal("AlreadyVotedInNomination", ramanaPage.GetProperty("work").GetProperty("eligibility").GetString());

        var summary = await Json(await b.Voter.GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal(1, summary.GetProperty("voteCount").GetInt32());
        Assert.False(summary.GetProperty("viewer").GetProperty("mayVote").GetBoolean());

        // Somebody else's vote is untouched by this one.
        Assert.Equal(1, (await Json(await VoteAsync(b.SecondVoter, b.Ramana.Reference))).GetProperty("voteCount").GetInt32());
        Assert.Equal(2, VoteCount());
    }

    /// <summary>
    /// The race the rule must survive: one person, two works in two different categories, the same instant. The
    /// per-category index cannot see this; the lock on (competition, voter) does. Run for several people so a lucky
    /// interleaving cannot pass it: each time one 201, one 409 and one row.
    /// </summary>
    [Fact]
    public async Task Two_votes_in_different_categories_at_the_same_moment_leave_exactly_one_row()
    {
        var b = await OpenBallotAsync();
        var voters = new List<HttpClient> { b.Voter, b.SecondVoter };
        for (var i = 0; i < 4; i++) voters.Add(await ParticipantAsync("Paralel", $"Səsverən{i}"));

        foreach (var voter in voters)
        {
            var results = await Task.WhenAll(
                VoteAsync(voter, b.Balaxani.Reference),
                VoteAsync(voter, b.Ramana.Reference));
            var codes = results.Select(r => r.StatusCode).Order().ToArray();
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, codes);
            var refused = results.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("artıq səs vermisiniz", await refused.Content.ReadAsStringAsync());
        }

        Assert.Equal(voters.Count, VoteCount());
        Assert.Equal(voters.Count, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteCast")));
        // Exactly one vote per person in this competition, as the database itself counts it.
        Assert.True(_factory.WithDb(db => db.Votes
            .GroupBy(v => new { v.UserId, v.Category.CompetitionId })
            .All(g => g.Count() == 1)));
    }

    /// <summary>
    /// The rule the prize depends on. Two requests that both pass every check race into one insert: the unique
    /// index decides, and whichever loses is told it already voted rather than being handed a silent second row.
    /// </summary>
    [Fact]
    public async Task Two_votes_cast_at_the_same_moment_leave_exactly_one_row()
    {
        var b = await OpenBallotAsync();

        var results = await Task.WhenAll(
            VoteAsync(b.Voter, b.Balaxani.Reference),
            VoteAsync(b.Voter, b.BalaxaniOther.Reference));

        var codes = results.Select(r => r.StatusCode).Order().ToArray();
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, codes);
        Assert.Equal(1, VoteCount());
        Assert.Equal(1, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == "VoteCast")));
    }

    [Fact]
    public async Task Signing_in_is_required_and_nobody_votes_for_their_own_work()
    {
        var b = await OpenBallotAsync();

        await Refused(await VoteAsync(_factory.CreateClient(), b.Balaxani.Reference), HttpStatusCode.Unauthorized);
        Assert.Contains("Öz işinizə",
            await Refused(await VoteAsync(b.Balaxani.Owner, b.Balaxani.Reference), HttpStatusCode.Forbidden));
        Assert.Equal(0, VoteCount());

        // A participant may still vote in a nomination that is not their own.
        Assert.Equal("OwnWork",
            Item(await Json(await WorksAsync(b.Balaxani.Owner)), b.Balaxani.Reference).GetProperty("eligibility").GetString());
        await Json(await VoteAsync(b.Balaxani.Owner, b.Ramana.Reference));
        Assert.Equal(1, VoteCount());
    }

    /// <summary>
    /// D11 was lifted on 4 October 2026: an account opened after voting began votes like any other. What it buys
    /// instead is visibility — the admin tally counts those votes apart, per work and in total, so a work carried by
    /// fresh accounts can be looked at before it is given a prize.
    /// </summary>
    [Fact]
    public async Task An_account_opened_after_voting_began_votes_and_the_admin_sees_it_apart()
    {
        var b = await OpenBallotAsync();
        var latecomer = await ParticipantAsync("Gec", "Gələn");

        var works = await Json(await WorksAsync(latecomer));
        Assert.Equal("Allowed", Item(works, b.Balaxani.Reference).GetProperty("eligibility").GetString());
        Assert.True(works.GetProperty("viewer").GetProperty("mayVote").GetBoolean());
        await Json(await VoteAsync(latecomer, b.Balaxani.Reference));
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        Assert.Equal(2, VoteCount());

        var tally = await Json(await b.Admin.GetAsync($"/api/admin/competitions/{b.Competition}/voting"));
        Assert.Equal(2, tally.GetProperty("voterCount").GetInt32());
        Assert.Equal(1, tally.GetProperty("lateVoterCount").GetInt32());
        var row = Nomination(tally, "Balaxanı").GetProperty("works").EnumerateArray()
            .Single(w => w.GetProperty("reference").GetString() == b.Balaxani.Reference);
        Assert.Equal(2, row.GetProperty("voteCount").GetInt32());
        Assert.Equal(1, row.GetProperty("lateVoteCount").GetInt32());

        // The public summary says how many have voted and when the last vote came — a number and a time, no name.
        var summary = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/voting"));
        Assert.Equal(2, summary.GetProperty("voterCount").GetInt32());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("lastVoteAt").ValueKind);
        Assert.DoesNotContain("Gələn", summary.GetRawText());
    }

    [Fact]
    public async Task Once_the_window_shuts_the_ballot_is_readable_and_nothing_more_is_counted()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));

        _factory.Clock.Now = _t0.AddSeconds(25);

        var works = await Json(await WorksAsync(b.Voter));
        Assert.Equal("Closed", works.GetProperty("state").GetString());
        Assert.Equal(3, works.GetProperty("total").GetInt32());
        // The count stands, and so does the answer to "did mine count?".
        Assert.Equal(1, Item(works, b.Balaxani.Reference).GetProperty("voteCount").GetInt32());
        Assert.Equal("AlreadyVotedForThisWork", Item(works, b.Balaxani.Reference).GetProperty("eligibility").GetString());
        Assert.Equal("VotingNotOpen", Item(works, b.Ramana.Reference).GetProperty("eligibility").GetString());

        Assert.Contains("başa çatıb",
            await Refused(await VoteAsync(b.Voter, b.Ramana.Reference), HttpStatusCode.UnprocessableEntity));
        Assert.Equal(1, VoteCount());
    }

    private static Task<HttpResponseMessage> SummaryAsync(HttpClient client) =>
        client.GetAsync($"/api/competitions/{Chevron}/voting");

    [Fact]
    public async Task The_vote_closes_at_the_exact_end_and_the_page_is_told_the_servers_time()
    {
        var b = await OpenBallotAsync();
        var end = _t0.AddSeconds(20);

        // A millisecond before the end (Postgres keeps microseconds, so a tick would round up onto the end itself).
        _factory.Clock.Now = end.AddMilliseconds(-1);
        var before = await Json(await SummaryAsync(b.Voter));
        Assert.Equal("Open", before.GetProperty("state").GetString());
        Assert.Equal(end.AddMilliseconds(-1), before.GetProperty("serverNow").GetDateTimeOffset());
        Assert.Equal(end, before.GetProperty("endsAt").GetDateTimeOffset());
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        Assert.True(_factory.WithDb(db => db.Votes.Single().CreatedAt) < end);

        // At the end itself the vote is over — for the page and for a request sent straight to the endpoint.
        _factory.Clock.Now = end;
        Assert.Equal("Closed", (await Json(await SummaryAsync(b.SecondVoter))).GetProperty("state").GetString());
        Assert.Contains("başa çatıb",
            await Refused(await VoteAsync(b.SecondVoter, b.Ramana.Reference), HttpStatusCode.UnprocessableEntity));
        Assert.Equal(1, VoteCount());
    }

    [Fact]
    public async Task A_vote_still_waiting_in_its_transaction_when_the_vote_ends_is_not_counted()
    {
        var b = await OpenBallotAsync();

        // Hold the votes table, so the request passes the gate while voting is open and then waits inside its
        // transaction — the moment between "allowed" and "written" where the end can pass.
        await using var holder = new NpgsqlConnection(TestDatabase.ConnectionStringFor(_factory.DatabaseName));
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lockVotes = new NpgsqlCommand("LOCK TABLE \"Votes\" IN ACCESS EXCLUSIVE MODE", holder, tx))
            await lockVotes.ExecuteNonQueryAsync();

        var pending = VoteAsync(b.Voter, b.Balaxani.Reference);
        await WaitForWaiterOnVotesAsync();

        _factory.Clock.Now = _t0.AddSeconds(20);
        await tx.RollbackAsync();

        Assert.Contains("başa çatıb", await Refused(await pending, HttpStatusCode.UnprocessableEntity));
        Assert.Equal(0, VoteCount());
    }

    /// <summary>Until some session is queued behind the lock on "Votes" — the vote request, inside its transaction.</summary>
    private async Task WaitForWaiterOnVotesAsync()
    {
        await using var probe = new NpgsqlConnection(TestDatabase.ConnectionStringFor(_factory.DatabaseName));
        await probe.OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE NOT granted AND relation = '\"Votes\"'::regclass", probe);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("the vote request never reached the votes table");
    }

    [Fact]
    public async Task A_closed_vote_is_in_review_until_an_admin_publishes_the_result()
    {
        var b = await OpenBallotAsync();
        _factory.Clock.Now = _t0.AddSeconds(25);
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.Status = CompetitionStatus.Closed; // entries are over, as they are once a vote has run
            await db.SaveChangesAsync();
        });

        async Task<(string State, bool Published)> Read()
        {
            var s = await Json(await SummaryAsync(b.Voter));
            return (s.GetProperty("state").GetString()!, s.GetProperty("resultsPublished").GetBoolean());
        }

        Assert.Equal(("Closed", false), await Read());

        // A result being prepared is a row with no publication date; it is not a result yet.
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            if (!await db.CompetitionResults.AnyAsync(r => r.CompetitionId == c.Id))
                db.CompetitionResults.Add(new CompetitionResult { CompetitionId = c.Id });
            await db.SaveChangesAsync();
        });
        Assert.Equal(("Closed", false), await Read());

        await _factory.WithDbAsync(async db =>
        {
            var r = await db.CompetitionResults.SingleAsync(x => x.Competition.Slug == Chevron);
            r.PublishedAt = _t0.AddSeconds(24);
            await db.SaveChangesAsync();
        });
        Assert.Equal(("Closed", true), await Read());

        // Reopened for entries, the results pages hide the result again — and so does this answer.
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.Status = CompetitionStatus.Published;
            c.StartDate = null;
            c.EndDate = null;
            await db.SaveChangesAsync();
        });
        Assert.Equal(("Closed", false), await Read());
    }

    [Fact]
    public async Task An_unknown_reference_is_refused_and_reading_the_ballot_writes_nothing()
    {
        var b = await OpenBallotAsync();
        await Refused(await WorksAsync(b.Voter, "/SUB-000000"), HttpStatusCode.NotFound);
        await Refused(await VoteAsync(b.Voter, "SUB-000000"), HttpStatusCode.NotFound);

        var before = _factory.WithDb(db => (db.Votes.Count(), db.AuditLogs.Count(), db.Submissions.Count()));
        for (var i = 0; i < 3; i++)
        {
            await Json(await b.Voter.GetAsync($"/api/competitions/{Chevron}/voting"));
            await Json(await WorksAsync(b.Voter));
            await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"));
        }
        Assert.Equal(before, _factory.WithDb(db => (db.Votes.Count(), db.AuditLogs.Count(), db.Submissions.Count())));
    }

    [Fact]
    public async Task A_vote_is_final_there_is_nothing_to_undo_it_with()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));

        var vote = $"/api/competitions/{Chevron}/voting/works/{b.Balaxani.Reference}/vote";
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await b.Voter.DeleteAsync(vote)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await b.Voter.PutAsJsonAsync(vote, new { })).StatusCode);
        Assert.Equal(1, VoteCount());
    }

    /// <summary>
    /// The ballot in each visitor's own shuffled order: the same for one seed on every page and every request, so
    /// paging neither repeats nor skips a work — and different from one visitor to the next, so no work owns the
    /// first screen. Without a seed the old order (by nomination, then submission) stands.
    /// </summary>
    [Fact]
    public async Task Each_visitor_gets_their_own_stable_shuffle_of_the_ballot()
    {
        var b = await OpenBallotAsync();
        _factory.Clock.Now = _t0;
        for (var i = 0; i < 5; i++)
            await SubmitAsync(i % 2 == 0 ? "balaxani" : "ramana", $"Əlavə iş {i}", "Anar", $"Əliyev{i}");
        _factory.Clock.Now = _t0.AddSeconds(10);

        async Task<List<string>> Order(string query) =>
            (await Json(await WorksAsync(b.Voter, query))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("reference").GetString()!).ToList();

        var all = await Order("?pageSize=60&seed=7");
        Assert.Equal(8, all.Count);
        Assert.Equal(all, await Order("?pageSize=60&seed=7"));
        // Paged with the same seed: the same works, once each, in the same order.
        var paged = new List<string>();
        for (var page = 1; page <= 3; page++) paged.AddRange(await Order($"?pageSize=3&page={page}&seed=7"));
        Assert.Equal(all, paged);
        Assert.Equal(8, (await Json(await WorksAsync(b.Voter, "?pageSize=3&seed=7"))).GetProperty("total").GetInt32());

        // Nominations keep their order around the shuffle; within one, different visitors see different orders.
        var firsts = new HashSet<string>();
        for (var seed = 1; seed <= 20; seed++) firsts.Add((await Order($"?pageSize=60&seed={seed}"))[0]);
        Assert.True(firsts.Count > 2, $"only {firsts.Count} different first works over 20 seeds");

        // A filtered page keeps the visitor's relative order.
        var balaxani = await Order("?pageSize=60&seed=7&nomination=balaxani");
        Assert.Equal(all.Where(balaxani.Contains).ToList(), balaxani);

        // No seed: by nomination, then by submission, as before.
        var plain = await Order("?pageSize=60");
        Assert.Equal(b.Balaxani.Reference, plain[0]);
        Assert.Equal(b.BalaxaniOther.Reference, plain[1]);
    }

    // ---- Pictures -------------------------------------------------------------------------------

    [Fact]
    public async Task The_ballot_shows_only_what_the_gallery_would_show_and_never_an_original()
    {
        var b = await OpenBallotAsync();

        // The file field is private by default, so the ballot carries no picture and says why.
        var card = Item(await Json(await WorksAsync(b.Voter)), b.Balaxani.Reference);
        Assert.Equal(JsonValueKind.Null, card.GetProperty("preview").ValueKind);
        Assert.Equal("None", card.GetProperty("previewState").GetString());
        Assert.Empty((await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"))).GetProperty("files").EnumerateArray());

        // Once an admin makes the field public, the server's own copy appears — and only once it is ready.
        await SetFieldPublicAsync(b.Admin, b.Competition, "files", true);
        await WaitForDerivativesAsync(b.Balaxani.Id);

        var detail = await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"));
        var file = Assert.Single(detail.GetProperty("files").EnumerateArray());
        Assert.Equal("image", file.GetProperty("kind").GetString());
        // The name carries the reference and the upload position, never the participant's own file name.
        Assert.Equal("Fayl 1", file.GetProperty("name").GetString());

        var url = file.GetProperty("url").GetString()!;
        var served = await b.Voter.GetAsync(url);
        Assert.True(served.IsSuccessStatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("Leyla", served.Content.Headers.ContentDisposition!.FileName ?? "");
        Assert.NotEqual(TestImages.Png(), await served.Content.ReadAsByteArrayAsync());
        Assert.True((await b.Voter.GetAsync(url + "/thumb")).IsSuccessStatusCode);

        // Closing the window does not make the picture public forever: before it opened, it was not served at all.
        _factory.Clock.Now = _t0;
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync(url)).StatusCode);
    }

    /// <summary>
    /// A picture that names its author would undo D2 for that work. An admin takes that one file off the public
    /// side — the ballot, the work's page and the file's own address — without touching the work or the file.
    /// </summary>
    [Fact]
    public async Task A_file_an_admin_hides_leaves_the_ballot_and_its_address_stops_working()
    {
        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "files", true);
        await WaitForDerivativesAsync(b.Balaxani.Id);
        var url = Assert.Single((await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"))).GetProperty("files").EnumerateArray())
            .GetProperty("url").GetString()!;
        var fileId = _factory.WithDb(db => db.SubmissionFiles.Single(f => f.SubmissionId == b.Balaxani.Id).Id);
        var visibility = $"/api/admin/submissions/{b.Balaxani.Id}/files/{fileId}/visibility";
        await Json(await b.Admin.PutAsJsonAsync($"/api/admin/submissions/{b.Balaxani.Id}/preview", new { fileId }));

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Voter.PutAsJsonAsync(visibility, new { hidden = true })).StatusCode);
        var detail = await Json(await b.Admin.PutAsJsonAsync(visibility, new { hidden = true }));
        var hidden = Assert.Single(detail.GetProperty("files").EnumerateArray());
        Assert.True(hidden.GetProperty("hiddenFromPublic").GetBoolean());
        Assert.False(hidden.GetProperty("canBePreview").GetBoolean());
        // It was the chosen picture: the choice goes back to automatic rather than pointing at a hidden file.
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("previewFileId").ValueKind);

        Assert.Empty((await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}"))).GetProperty("files").EnumerateArray());
        Assert.Equal("None", Item(await Json(await WorksAsync(b.Voter)), b.Balaxani.Reference).GetProperty("previewState").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync(url + "/thumb")).StatusCode);
        // The work itself is still on the ballot, and nothing was deleted.
        Assert.Equal(b.Balaxani.Reference, Item(await Json(await WorksAsync(b.Voter)), b.Balaxani.Reference).GetProperty("reference").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await b.Admin.PutAsJsonAsync($"/api/admin/submissions/{b.Balaxani.Id}/preview", new { fileId })).StatusCode);

        await Json(await b.Admin.PutAsJsonAsync(visibility, new { hidden = false }));
        Assert.True((await b.Voter.GetAsync(url)).IsSuccessStatusCode);
        var audit = (await Json(await b.Admin.GetAsync($"/api/admin/audit?entityType=Submission&entityId={b.Balaxani.Id}")))
            .EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToList();
        Assert.Contains("FileHidden", audit);
        Assert.Contains("FileShown", audit);
    }

    /// <summary>
    /// A PDF entry is shown whole, not by its cover: every page after the first is on the work's page, each made
    /// exactly as the first one is (a server-made, metadata-free WEBP) and behind the same gate. The PDF itself is
    /// never served, and a page that is not there — page 1, past the end, a hidden file — is a 404.
    /// </summary>
    [Fact]
    public async Task A_pdf_work_shows_every_page_and_never_the_document()
    {
        // A three-page document, entered before the window like the others.
        var owner = await ParticipantAsync("Səməd", "Brendov");
        var draft = await Json(await owner.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "ramana" }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await owner.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title = "Brendbuk", concept = "Gizli konsept mətni", symbols = "Simvollar", notes = "Şəxsi qeyd" },
        }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.PdfWithPages(3));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "Səməd_Brendov_brendbuk.pdf");
        await Json(await owner.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await owner.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;

        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "files", true);
        await WaitForDerivativesAsync(id);

        var detail = await Json(await WorksAsync(b.Voter, $"/{reference}"));
        var files = detail.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["document-preview", "document-page", "document-page"], files.Select(f => f.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal([2, 3], files.Skip(1).Select(f => f.GetProperty("page").GetInt32()).ToArray());
        var fileId = files[0].GetProperty("id").GetInt32();
        Assert.All(files, f => Assert.Equal(fileId, f.GetProperty("id").GetInt32()));

        foreach (var f in files.Skip(1))
        {
            var url = f.GetProperty("url").GetString()!;
            Assert.EndsWith($"/files/{fileId}/pages/{f.GetProperty("page").GetInt32()}", url);
            var served = await b.Voter.GetAsync(url);
            Assert.True(served.IsSuccessStatusCode, $"{url}: {(int)served.StatusCode}");
            Assert.Equal("image/webp", served.Content.Headers.ContentType!.MediaType);
            Assert.Equal("no-cache", served.Headers.CacheControl!.ToString());
            var bytes = await served.Content.ReadAsByteArrayAsync();
            Assert.Empty(TestImages.LeakedMarkers(bytes));                       // pixels only, no metadata
            Assert.DoesNotContain("Səməd", served.Content.Headers.ContentDisposition!.FileName ?? "");
            Assert.True((await b.Voter.GetAsync(f.GetProperty("thumbUrl").GetString()!)).IsSuccessStatusCode);
        }

        var pages = $"/api/competitions/{Chevron}/voting/works/{reference}/files/{fileId}/pages";
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync($"{pages}/1")).StatusCode);   // the file's own address
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync($"{pages}/4")).StatusCode);   // past the end

        // A work with pictures only has no pages to offer.
        var png = _factory.WithDb(db => db.SubmissionFiles.Single(f => f.SubmissionId == b.Balaxani.Id).Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.Voter.GetAsync($"/api/competitions/{Chevron}/voting/works/{b.Balaxani.Reference}/files/{png}/pages/2")).StatusCode);

        // Hidden by an admin: the document is gone with all its pages.
        await Json(await b.Admin.PutAsJsonAsync($"/api/admin/submissions/{id}/files/{fileId}/visibility", new { hidden = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync($"{pages}/2")).StatusCode);
        Assert.Empty((await Json(await WorksAsync(b.Voter, $"/{reference}"))).GetProperty("files").EnumerateArray());
        await Json(await b.Admin.PutAsJsonAsync($"/api/admin/submissions/{id}/files/{fileId}/visibility", new { hidden = false }));

        // Before the window opens, the pages are not served at all.
        _factory.Clock.Now = _t0;
        Assert.Equal(HttpStatusCode.NotFound, (await b.Voter.GetAsync($"{pages}/2")).StatusCode);
    }

    // ---- What it costs, and what it lets out ------------------------------------------------------

    /// <summary>
    /// The ballot costs the same whether it holds three works or thirty. Not a benchmark — a guard against the
    /// one mistake that would matter here: a query per work, which would turn a page of a hundred entries into a
    /// hundred round trips on the day the most people are looking.
    /// </summary>
    [Fact]
    public async Task A_page_of_the_ballot_costs_a_fixed_number_of_queries()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        // Every upload wakes the derivative worker; measuring while it is busy would count its queries as well.
        await WaitForDerivativesAsync(b.Balaxani.Id, b.BalaxaniOther.Id, b.Ramana.Id, b.Rejected.Id);

        var three = await QueriesForAsync(() => WorksAsync(b.Voter));

        // Ten more works, in two nominations, each with its own file.
        _factory.Clock.Now = _t0;
        var added = new List<int>();
        for (var i = 0; i < 10; i++)
            added.Add((await SubmitAsync(i % 2 == 0 ? "balaxani" : "ramana", $"Əlavə iş {i}", "Anar", $"Əliyev{i}")).Id);
        _factory.Clock.Now = _t0.AddSeconds(10);
        await WaitForDerivativesAsync([.. added]);

        var thirteen = await QueriesForAsync(() => WorksAsync(b.Voter));
        Assert.Equal(13, (await Json(await WorksAsync(b.Voter))).GetProperty("total").GetInt32());
        Assert.Equal(three, thirteen);
        Assert.InRange(thirteen, 1, 12);
    }

    private async Task<int> QueriesForAsync(Func<Task<HttpResponseMessage>> request)
    {
        var before = _factory.Logs.Lines.Count;
        Assert.True((await request()).IsSuccessStatusCode);
        return _factory.Logs.Lines.Skip(before).Count(l => l.Contains("Executed DbCommand", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every public answer of the voting module, read once and searched for everything it must never carry. The
    /// per-endpoint tests say what is shown; this says what is not, in one place, so a field added later to a
    /// shared shape cannot slip onto this surface unnoticed.
    /// </summary>
    [Fact]
    public async Task No_public_answer_of_the_module_carries_anything_private()
    {
        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "files", true);
        await SetFieldPublicAsync(b.Admin, b.Competition, "concept", true);
        await WaitForDerivativesAsync(b.Balaxani.Id);
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));

        var anonymous = _factory.CreateClient();
        string[] addresses =
        [
            $"/api/competitions/{Chevron}/voting",
            $"/api/competitions/{Chevron}/voting/works",
            $"/api/competitions/{Chevron}/voting/works?nomination=balaxani",
            $"/api/competitions/{Chevron}/voting/works/{b.Balaxani.Reference}",
            $"/api/competitions/{Chevron}/voting/leaderboard",
        ];

        foreach (var client in new[] { anonymous, b.Voter, b.Balaxani.Owner })
            foreach (var address in addresses)
            {
                var body = await (await client.GetAsync(address)).Content.ReadAsStringAsync();

                // Whose work it is, and how to reach them.
                foreach (var identity in new[] { "Leyla", "Quliyeva", "Səbinə", "Rzayeva", "@example.com", "+994" })
                    Assert.DoesNotContain(identity, body);
                // What the participant wrote where an admin did not make it public, and what staff wrote at all.
                Assert.DoesNotContain("Şəxsi qeyd", body);
                // The machinery: storage keys, original file names, user ids, moderation status.
                foreach (var internals in new[] { "userId", "storageKey", "publicAssetKey", "thumbnailKey", "originalFileName", "\"status\"" })
                    Assert.DoesNotContain(internals, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("eskiz.png", body);
            }
    }

    /// <summary>
    /// The picture a voter sees is the server's own copy, served so that it cannot be cached past a change and
    /// cannot run anything in the browser. The same headers the gallery uses, on a route that opens earlier.
    /// </summary>
    [Fact]
    public async Task A_pictures_headers_let_nothing_be_cached_past_a_change_and_nothing_run()
    {
        var b = await OpenBallotAsync();
        await SetFieldPublicAsync(b.Admin, b.Competition, "files", true);
        await WaitForDerivativesAsync(b.Balaxani.Id);

        var url = (await Json(await WorksAsync(b.Voter, $"/{b.Balaxani.Reference}")))
            .GetProperty("files").EnumerateArray().First().GetProperty("url").GetString()!;
        var res = await _factory.CreateClient().GetAsync(url);

        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("no-cache", res.Headers.CacheControl!.ToString());
        Assert.NotNull(res.Headers.ETag);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; img-src 'self'; sandbox",
            res.Headers.GetValues("Content-Security-Policy").Single());

        // The same ETag comes back as 304, and a work taken off the ballot stops serving at once.
        var again = _factory.CreateClient();
        again.DefaultRequestHeaders.Add("If-None-Match", res.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await again.GetAsync(url)).StatusCode);

        await Json(await b.Admin.PutAsJsonAsync($"/api/admin/submissions/{b.Balaxani.Id}/status", new { status = "Rejected" }));
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync(url)).StatusCode);
    }

    // ---- The administration's side --------------------------------------------------------------

    /// <summary>Saves the competition back with a voting window, through the ordinary edit form.</summary>
    private static Task<HttpResponseMessage> SaveWindowAsync(HttpClient admin, int id, JsonElement c, object? from, object? to) =>
        admin.PutAsJsonAsync($"/api/admin/competitions/{id}", new
        {
            projectId = c.GetProperty("projectId").GetInt32(),
            title = c.GetProperty("title").GetString(),
            slug = c.GetProperty("slug").GetString(),
            shortDescription = c.GetProperty("shortDescription").GetString(),
            hasCategories = c.GetProperty("hasCategories").GetBoolean(),
            categoryLabel = c.GetProperty("categoryLabel").GetString(),
            sortOrder = c.GetProperty("sortOrder").GetInt32(),
            votingStartsAt = from,
            votingEndsAt = to,
        });

    private static async Task<JsonElement> SetWindowAsync(HttpClient admin, int id, object? from, object? to)
    {
        var c = await Json(await admin.GetAsync($"/api/admin/competitions/{id}"));
        return await Json(await SaveWindowAsync(admin, id, c, from, to));
    }

    private static JsonElement Nomination(JsonElement tally, string name) =>
        tally.GetProperty("nominations").EnumerateArray().Single(n => n.GetProperty("name").GetString() == name);

    private static async Task<List<(string Action, string Summary)>> AuditAsync(HttpClient admin, int competitionId) =>
        (await Json(await admin.GetAsync($"/api/admin/audit?entityType=Competition&entityId={competitionId}")))
        .EnumerateArray()
        .Select(x => (x.GetProperty("action").GetString()!, x.GetProperty("summary").GetString() ?? ""))
        .ToList();

    [Fact]
    public async Task An_admin_opens_the_window_on_the_competition_and_it_is_written_down()
    {
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        var saved = await SetWindowAsync(admin, competition, _t0.AddSeconds(5), _t0.AddSeconds(20));
        Assert.Equal(_t0.AddSeconds(5), saved.GetProperty("votingStartsAt").GetDateTimeOffset());
        Assert.Equal("Upcoming", saved.GetProperty("votingState").GetString());

        _factory.Clock.Now = _t0.AddSeconds(10);
        Assert.Equal("Open", (await Json(await admin.GetAsync($"/api/admin/competitions/{competition}")))
            .GetProperty("votingState").GetString());

        // Moving the window gets a line of its own; an edit that leaves it alone adds nothing.
        var moved = Assert.Single((await AuditAsync(admin, competition)).Where(a => a.Action == "VotingWindowChanged"));
        Assert.StartsWith("yoxdur \u2192 ", moved.Summary);
        await SetWindowAsync(admin, competition, _t0.AddSeconds(5), _t0.AddSeconds(20));
        Assert.Single((await AuditAsync(admin, competition)).Where(a => a.Action == "VotingWindowChanged"));

        // Clearing it shuts the vote without touching the deadline participants were given.
        await SetWindowAsync(admin, competition, null, null);
        Assert.Equal("NotConfigured", (await Json(await admin.GetAsync($"/api/admin/competitions/{competition}")))
            .GetProperty("votingState").GetString());
        await Refused(await WorksAsync(_factory.CreateClient()), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Half_a_window_and_a_backwards_one_are_both_refused()
    {
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        var c = await Json(await admin.GetAsync($"/api/admin/competitions/{competition}"));

        foreach (var (from, to) in new (object?, object?)[] { (_t0.AddSeconds(5), null), (null, _t0.AddSeconds(20)) })
            Assert.Contains("hər ikisini boş buraxın",
                await Refused(await SaveWindowAsync(admin, competition, c, from, to), HttpStatusCode.BadRequest));
        Assert.Contains("sonra olmalıdır",
            await Refused(await SaveWindowAsync(admin, competition, c, _t0.AddSeconds(20), _t0.AddSeconds(5)), HttpStatusCode.BadRequest));

        Assert.Null(_factory.WithDb(db => db.Competitions.Single(x => x.Id == competition).VotingStartsAt));
    }

    [Fact]
    public async Task The_tally_names_the_participants_counts_the_voters_and_never_says_who_voted()
    {
        var b = await OpenBallotAsync();
        var third = await ParticipantAsync("Ülviyyə", "Səmədova");
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        await Json(await VoteAsync(b.SecondVoter, b.Balaxani.Reference));
        await Json(await VoteAsync(third, b.Ramana.Reference));
        // A second vote by the same person — in another category — is refused, so it is never counted.
        await Refused(await VoteAsync(b.SecondVoter, b.Ramana.Reference), HttpStatusCode.Conflict);

        var tally = await Json(await b.Admin.GetAsync($"/api/admin/competitions/{b.Competition}/voting"));
        Assert.Equal("Open", tally.GetProperty("state").GetString());
        Assert.Equal(3, tally.GetProperty("voteCount").GetInt32());
        Assert.Equal(3, tally.GetProperty("voterCount").GetInt32());

        var balaxani = Nomination(tally, "Balaxanı");
        Assert.Equal(2, balaxani.GetProperty("workCount").GetInt32());
        Assert.Equal(2, balaxani.GetProperty("voteCount").GetInt32());
        Assert.False(balaxani.GetProperty("isTie").GetBoolean());

        var works = balaxani.GetProperty("works").EnumerateArray().ToList();
        Assert.Equal(b.Balaxani.Reference, works[0].GetProperty("reference").GetString());
        Assert.Equal(2, works[0].GetProperty("voteCount").GetInt32());
        Assert.Equal(1, works[0].GetProperty("place").GetInt32());
        Assert.Equal("Leyla Quliyeva", works[0].GetProperty("participantName").GetString());
        Assert.False(works[0].GetProperty("isWinner").GetBoolean());
        // A work nobody voted for is second, not missing.
        Assert.Equal(b.BalaxaniOther.Reference, works[1].GetProperty("reference").GetString());
        Assert.Equal(0, works[1].GetProperty("voteCount").GetInt32());
        Assert.Equal(2, works[1].GetProperty("place").GetInt32());

        // No vote is attributed to anybody: the voters are counted, never named.
        var text = await (await b.Admin.GetAsync($"/api/admin/competitions/{b.Competition}/voting")).Content.ReadAsStringAsync();
        foreach (var voter in new[] { "Elçin", "Nəbiyev", "Rəşad", "İsmayılov", "Ülviyyə", "Səmədova" })
            Assert.DoesNotContain(voter, text);
        Assert.DoesNotContain("userId", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The suspicion score reaches an admin, and only an admin: per work, about how many votes are not genuine; per
    /// suspicious vote, the account and every reason. The voter is given the author's phone directly in the database
    /// — an account opened before one phone meant one account (D12) — which is the sign the score weighs most.
    /// </summary>
    [Fact]
    public async Task The_admin_sees_a_suspicion_score_for_each_vote_with_its_reasons()
    {
        var b = await OpenBallotAsync();
        await SharePhoneAsync(b.Voter, b.Balaxani.Owner);
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));       // the author's phone: the strongest sign
        await Json(await VoteAsync(b.SecondVoter, b.Ramana.Reference));

        var url = $"/api/admin/competitions/{b.Competition}/voting/suspicion";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Voter.GetAsync(url)).StatusCode);

        var report = await Json(await b.Admin.GetAsync(url));
        Assert.Equal(2, report.GetProperty("voteCount").GetInt32());
        var balaxani = report.GetProperty("works").EnumerateArray().Single(w => w.GetProperty("reference").GetString() == b.Balaxani.Reference);
        Assert.Equal(1, balaxani.GetProperty("votes").GetInt32());
        Assert.True(balaxani.GetProperty("expectedFake").GetDouble() >= 0.9);

        var votes = report.GetProperty("votes").EnumerateArray().ToList();
        var top = votes.First(v => v.GetProperty("reference").GetString() == b.Balaxani.Reference);
        Assert.True(top.GetProperty("score").GetInt32() >= 90);
        Assert.Equal("Elçin Nəbiyev", top.GetProperty("voterName").GetString());
        Assert.Contains(top.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.StartsWith("Telefonu işin müəllifi ilə eynidir"));
        // Reading it writes nothing and changes no vote.
        Assert.Equal(2, VoteCount());
    }

    [Fact]
    public async Task A_level_nomination_is_shown_as_a_tie_and_no_winner_is_implied()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        await Json(await VoteAsync(b.SecondVoter, b.BalaxaniOther.Reference));

        var tally = await Json(await b.Admin.GetAsync($"/api/admin/competitions/{b.Competition}/voting"));
        var balaxani = Nomination(tally, "Balaxanı");
        Assert.True(balaxani.GetProperty("isTie").GetBoolean());
        // Level works share first place, and the count alone marks neither of them a winner.
        Assert.Equal([1, 1], balaxani.GetProperty("works").EnumerateArray().Select(w => w.GetProperty("place").GetInt32()).ToArray());
        Assert.DoesNotContain(balaxani.GetProperty("works").EnumerateArray(), w => w.GetProperty("isWinner").GetBoolean());

        // A nomination nobody voted in is not a tie; it is simply empty of votes.
        Assert.False(Nomination(tally, "Ramana").GetProperty("isTie").GetBoolean());
    }

    [Fact]
    public async Task The_tally_shows_the_winner_an_admin_chose_and_the_works_no_nomination_holds()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        await Json(await b.Admin.PostAsJsonAsync($"/api/admin/competitions/{b.Competition}/result/winners",
            new { submissionId = b.Balaxani.Id }));

        // A final work in no nomination: nobody can vote for it, so the tally says so rather than dropping it.
        await _factory.WithDbAsync(async db =>
        {
            var stray = await db.Submissions.SingleAsync(x => x.Id == b.Ramana.Id);
            stray.CompetitionCategoryId = null;
            await db.SaveChangesAsync();
        });

        var tally = await Json(await b.Admin.GetAsync($"/api/admin/competitions/{b.Competition}/voting"));
        Assert.True(Nomination(tally, "Balaxanı").GetProperty("works").EnumerateArray().First().GetProperty("isWinner").GetBoolean());
        Assert.Equal(b.Ramana.Reference,
            Assert.Single(tally.GetProperty("unplaced").EnumerateArray()).GetProperty("reference").GetString());
    }

    // ---- The live ranking -----------------------------------------------------------------------

    /// <summary>
    /// Two works racing: one leads, the other overtakes it, and the list follows. Before any vote every work is
    /// level, so all share the first rank, in the order of their ids — the same order on every read.
    /// </summary>
    [Fact]
    public async Task The_ranking_follows_the_votes_and_a_tie_shares_its_rank_in_a_stable_order()
    {
        var b = await OpenBallotAsync();
        var third = await ParticipantAsync("Orxan", "Məmmədov");
        var byId = new[] { b.Balaxani, b.BalaxaniOther, b.Ramana }.OrderBy(w => w.Id).Select(w => w.Reference).ToArray();

        var level = await Json(await LeaderboardAsync(b.Voter));
        Assert.Equal("Open", level.GetProperty("state").GetString());
        Assert.Equal(0, level.GetProperty("totalVotes").GetInt32());
        Assert.Equal(byId, Ranked(level).Select(r => r.Reference).ToArray());
        Assert.All(Ranked(level), r => Assert.Equal(1, r.Rank));
        Assert.Equal(Ranked(level), Ranked(await Json(await LeaderboardAsync(b.Voter))));

        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        var first = Ranked(await Json(await LeaderboardAsync(b.Voter)));
        Assert.Equal((1, b.Balaxani.Reference, 1), first[0]);
        Assert.All(first.Skip(1), r => Assert.Equal((2, 0), (r.Rank, r.Votes)));

        // Another category's work takes two votes and passes it: the ranking is one list for the competition.
        await Json(await VoteAsync(b.SecondVoter, b.Ramana.Reference));
        await Json(await VoteAsync(third, b.Ramana.Reference));
        var after = await Json(await LeaderboardAsync(b.Voter));
        Assert.Equal(
            [(1, b.Ramana.Reference, 2), (2, b.Balaxani.Reference, 1), (3, b.BalaxaniOther.Reference, 0)],
            Ranked(after));
        Assert.Equal(3, after.GetProperty("totalVotes").GetInt32());
    }

    [Fact]
    public async Task The_ranking_holds_only_the_works_on_the_ballot()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Ramana.Reference));
        var drafter = await ParticipantAsync("Kənan", "Səfərov");
        await Json(await drafter.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "bileceri" }));

        var references = Ranked(await Json(await LeaderboardAsync(b.Voter))).Select(r => r.Reference).ToArray();
        Assert.Equal(3, references.Length);
        Assert.DoesNotContain(b.Rejected.Reference, references);

        // A work withdrawn after it was voted for leaves the ranking, and its votes leave the total with it.
        await _factory.WithDbAsync(async db =>
        {
            (await db.Submissions.SingleAsync(x => x.Id == b.Ramana.Id)).Status = SubmissionStatus.Withdrawn;
            await db.SaveChangesAsync();
        });
        var after = await Json(await LeaderboardAsync(b.Voter));
        Assert.Equal(
            new[] { b.Balaxani, b.BalaxaniOther }.OrderBy(w => w.Id).Select(w => w.Reference).ToArray(),
            Ranked(after).Select(r => r.Reference).ToArray());
        Assert.Equal(0, after.GetProperty("totalVotes").GetInt32());
    }

    [Fact]
    public async Task The_ranking_tells_a_voter_their_own_work_and_nobody_else()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.BalaxaniOther.Reference));

        Assert.Equal(b.BalaxaniOther.Reference,
            (await Json(await LeaderboardAsync(b.Voter))).GetProperty("myVoteReference").GetString());
        // Not yet voted, and signed out: nothing — and the list itself is the same for all three.
        Assert.Equal(JsonValueKind.Null,
            (await Json(await LeaderboardAsync(b.SecondVoter))).GetProperty("myVoteReference").ValueKind);
        var anonymous = await Json(await LeaderboardAsync(_factory.CreateClient()));
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("myVoteReference").ValueKind);
        Assert.Equal(Ranked(await Json(await LeaderboardAsync(b.Voter))), Ranked(anonymous));
    }

    [Fact]
    public async Task The_ranking_is_hidden_until_the_window_opens_and_stays_readable_once_it_shuts()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));

        _factory.Clock.Now = _t0;
        await Refused(await LeaderboardAsync(b.Voter), HttpStatusCode.NotFound);

        _factory.Clock.Now = _t0.AddSeconds(25);
        string Places() => _factory.WithDb(db => string.Join(';', db.CompetitionResults.OrderBy(r => r.Id)
            .Select(r => $"{r.OverallWinnerSubmissionId},{r.SecondPlaceSubmissionId},{r.ThirdPlaceSubmissionId}").ToList()));
        var places = Places();
        var closed = await Json(await LeaderboardAsync(_factory.CreateClient()));
        Assert.Equal("Closed", closed.GetProperty("state").GetString());
        Assert.Equal((1, b.Balaxani.Reference, 1), Ranked(closed)[0]);
        // A ranking of the public vote, not a result: reading it awards nothing.
        Assert.Equal(places, Places());
    }

    [Fact]
    public async Task The_ranking_costs_a_fixed_number_of_queries_however_many_works_it_holds()
    {
        var b = await OpenBallotAsync();
        await Json(await VoteAsync(b.Voter, b.Balaxani.Reference));
        await WaitForDerivativesAsync(b.Balaxani.Id, b.BalaxaniOther.Id, b.Ramana.Id, b.Rejected.Id);

        var three = await QueriesForAsync(() => LeaderboardAsync(b.Voter));

        _factory.Clock.Now = _t0;
        var added = new List<int>();
        for (var i = 0; i < 10; i++)
            added.Add((await SubmitAsync(i % 2 == 0 ? "balaxani" : "ramana", $"Əlavə iş {i}", "Anar", $"Əliyev{i}")).Id);
        _factory.Clock.Now = _t0.AddSeconds(10);
        await WaitForDerivativesAsync([.. added]);

        var thirteen = await QueriesForAsync(() => LeaderboardAsync(b.Voter));
        Assert.Equal(13, Ranked(await Json(await LeaderboardAsync(b.Voter))).Length);
        Assert.Equal(three, thirteen);
        Assert.InRange(thirteen, 1, 8);
    }

    private async Task WaitForDerivativesAsync(params int[] submissionIds)
    {
        for (var i = 0; i < 200; i++)
        {
            var pending = _factory.WithDb(db => db.SubmissionFiles.Count(f =>
                submissionIds.Contains(f.SubmissionId) && f.PublicAssetStatus == PublicAssetStatus.Pending));
            if (pending == 0) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("public derivatives were not produced");
    }
}

/// <summary>The vote's own throttle, at its production setting: a script cannot walk the ballot.</summary>
public sealed class VotingRateLimitTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory = new() { Settings = { ["RateLimiting:VotesPerMinute"] = "3" } };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Repeated_votes_from_one_account_are_throttled()
    {
        var now = _factory.Clock.Now;
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Elçin", lastName = "Nəbiyev", phone = TestPhones.Next(),
            email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = now.AddSeconds(5);
            c.VotingEndsAt = now.AddSeconds(20);
            await db.SaveChangesAsync();
        });
        _factory.Clock.Now = now.AddSeconds(10);

        // Nothing here is a valid reference, so no vote can be stored: only the throttle can end this.
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await client.PostAsync($"/api/competitions/{Chevron}/voting/works/SUB-00000{i}/vote", null)).StatusCode);

        Assert.Equal(new[] { HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound }, codes.Take(3).ToArray());
        Assert.All(codes.Skip(3), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
        Assert.Equal(0, _factory.WithDb(db => db.Votes.Count()));
    }
}

/// <summary>
/// The live ranking's shared copy, at its production setting: kept for a few seconds for everybody, and dropped
/// the moment a vote is cast, so a voter never reads a ranking that does not yet hold their own vote.
/// </summary>
public sealed class VotingLeaderboardCacheTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory = new() { Settings = { ["Voting:LeaderboardCacheSeconds"] = "60" } };

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> RegisterAsync(string first, string last)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(),
            email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        return client;
    }

    [Fact]
    public async Task A_cast_vote_is_in_the_very_next_read_of_the_shared_ranking()
    {
        var now = _factory.Clock.Now;
        var author = await RegisterAsync("Leyla", "Quliyeva");
        var draft = await (await author.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = "balaxani" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = draft.GetProperty("id").GetInt32();
        await author.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title = "Şevron", concept = "K", symbols = "S" } });
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await author.PostAsync($"/api/submissions/{id}/files", form);
        var reference = (await (await author.PostAsync($"/api/submissions/{id}/submit", null)).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;
        var voter = await RegisterAsync("Elçin", "Nəbiyev");

        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync(x => x.Slug == Chevron);
            c.VotingStartsAt = now.AddSeconds(5);
            c.VotingEndsAt = now.AddSeconds(20);
            await db.SaveChangesAsync();
        });
        _factory.Clock.Now = now.AddSeconds(10);

        async Task<int> CountAsync() =>
            (await (await voter.GetAsync($"/api/competitions/{Chevron}/voting/leaderboard")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("reference").GetString() == reference)
            .GetProperty("voteCount").GetInt32();

        Assert.Equal(0, await CountAsync());   // now shared, for a minute
        Assert.Equal(HttpStatusCode.Created,
            (await voter.PostAsync($"/api/competitions/{Chevron}/voting/works/{reference}/vote", null)).StatusCode);
        Assert.Equal(1, await CountAsync());
    }
}
