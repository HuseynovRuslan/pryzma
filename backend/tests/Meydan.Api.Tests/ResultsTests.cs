using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 7: one winner per category — every settlement of "Bakı kəndləri" can have its own — private until an admin
/// publishes the result; then a public results page that names the winners, and a gallery that exposes only what was
/// marked public (never a non-winner's identity).
/// </summary>
public sealed partial class ResultsTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private static readonly byte[] Png = TestImages.Png();
    private static readonly byte[] Pdf = "%PDF-1.4\n%âãÏÓ\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"u8.ToArray();

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---- Helpers --------------------------------------------------------------------------------

    private sealed record Entry(int Id, string Reference, string Email, string First, string Last)
    {
        public string Name => $"{First} {Last}";
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    /// <summary>A participant with a name of their own, so tests can tell whose name became public.</summary>
    private async Task<(HttpClient Client, string Email)> ParticipantAsync(string first = "Aysel", string last = "Məmmədova")
    {
        var email = $"p-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(), email,
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        var body = await Json(res);
        client.UseToken(body.GetProperty("accessToken").GetString()!);
        return (client, email);
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

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, string fileName, byte[] content, string type)
    {
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/submissions/{id}/files", form);
    }

    private static async Task<int> DraftAsync(HttpClient client, string settlement, string title)
    {
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title, concept = "Gizli konsept mətni", symbols = "Simvollar", notes = "Şəxsi əlavə qeyd" },
        }));
        await Json(await UploadAsync(client, id, "Aysel_Məmmədova_eskiz.png", Png, "image/png"));
        return id;
    }

    private async Task<Entry> SubmitAsync(string settlement, string title, string first = "Aysel", string last = "Məmmədova", bool withPdf = false)
    {
        var (client, email) = await ParticipantAsync(first, last);
        var id = await DraftAsync(client, settlement, title);
        if (withPdf) await Json(await UploadAsync(client, id, "izah.pdf", Pdf, "application/pdf"));
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return new Entry(id, submitted.GetProperty("referenceNumber").GetString()!, email, first, last);
    }

    private async Task<int> ChevronIdAsync(HttpClient admin)
    {
        var list = await Json(await admin.GetAsync("/api/admin/competitions"));
        return list.EnumerateArray().First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> SelectAsync(HttpClient admin, int competitionId, int submissionId, bool confirm = false) =>
        admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/result/winners", new { submissionId, confirmPublished = confirm });

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient admin, int competitionId, int submissionId, int current, bool confirm = false) =>
        admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/result/winners",
            new { submissionId, currentWinnerSubmissionId = current, confirmPublished = confirm });

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient admin, int competitionId, int submissionId, bool confirm = false) =>
        admin.DeleteAsync($"/api/admin/competitions/{competitionId}/result/winners/{submissionId}?confirmPublished={confirm.ToString().ToLowerInvariant()}");

    private static Task<HttpResponseMessage> CloseAsync(HttpClient admin, int competitionId) =>
        admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/status", new { status = "Closed" });

    private static Task<HttpResponseMessage> PublishAsync(HttpClient admin, int competitionId) =>
        admin.PostAsync($"/api/admin/competitions/{competitionId}/result/publish", null);

    private static async Task SetStatusAsync(HttpClient admin, int submissionId, string status) =>
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{submissionId}/status", new { status }));

    private static JsonElement Slot(JsonElement result, string category) =>
        result.GetProperty("slots").EnumerateArray().Single(s =>
            s.GetProperty("category").ValueKind != JsonValueKind.Null && s.GetProperty("category").GetProperty("name").GetString() == category);

    private static async Task<List<(string Action, string Summary)>> AuditAsync(HttpClient admin, int competitionId) =>
        (await Json(await admin.GetAsync($"/api/admin/audit?entityType=Competition&entityId={competitionId}"))).EnumerateArray()
        .Select(x => (x.GetProperty("action").GetString()!, x.GetProperty("summary").GetString() ?? "")).ToList();

    /// <summary>Three settlements with winners (Balaxanı, Biləcəri, Ramana), a second Balaxanı entry, a rejected one.</summary>
    private async Task<(HttpClient Admin, int Competition, Entry Balaxani, Entry Bileceri, Entry Ramana, Entry Other, Entry Rejected)> PublishedScenarioAsync()
    {
        var balaxani = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva", withPdf: true);
        var bileceri = await SubmitAsync("bileceri", "Biləcəri qalxanı", "Tural", "Əliyev");
        var ramana = await SubmitAsync("ramana", "Ramana qalası", "Nigar", "Həsənova");
        var other = await SubmitAsync("balaxani", "Balaxanı — ikinci iş", "Səbinə", "Rzayeva");
        var rejected = await SubmitAsync("nardaran", "Rədd olunacaq iş", "Kamran", "Babayev");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await SetStatusAsync(admin, rejected.Id, "Rejected");
        await Json(await admin.PostAsJsonAsync($"/api/admin/submissions/{balaxani.Id}/notes", new { text = "GİZLİ-ADMİN-QEYDİ" }));
        foreach (var w in new[] { balaxani, bileceri, ramana })
            await Json(await SelectAsync(admin, competition, w.Id));
        await Json(await CloseAsync(admin, competition));
        await Json(await PublishAsync(admin, competition));
        return (admin, competition, balaxani, bileceri, ramana, other, rejected);
    }

    [GeneratedRegex("[0-9a-f]{32}")]
    private static partial Regex StorageKey();

    // ---- Every settlement its own winner --------------------------------------------------------

    [Fact]
    public async Task Every_settlement_can_have_its_own_winner_and_they_coexist()
    {
        var balaxani = await SubmitAsync("balaxani", "Balaxanı şevronu", "Leyla", "Quliyeva");
        var bileceri = await SubmitAsync("bileceri", "Biləcəri qalxanı", "Tural", "Əliyev");
        var ramana = await SubmitAsync("ramana", "Ramana qalası", "Nigar", "Həsənova");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        foreach (var w in new[] { balaxani, bileceri, ramana })
            await Json(await SelectAsync(admin, competition, w.Id));

        var result = await Json(await admin.GetAsync($"/api/admin/competitions/{competition}/result"));
        Assert.Equal("Prepared", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("hasCategories").GetBoolean());
        Assert.Equal(59, result.GetProperty("categoryCount").GetInt32());
        Assert.Equal(3, result.GetProperty("winnerCount").GetInt32());
        Assert.Equal(59, result.GetProperty("slots").GetArrayLength());
        foreach (var (name, entry) in new[] { ("Balaxanı", balaxani), ("Biləcəri", bileceri), ("Ramana", ramana) })
        {
            var slot = Slot(result, name);
            Assert.Equal(entry.Reference, slot.GetProperty("winner").GetProperty("reference").GetString());
            Assert.Equal(entry.Name, slot.GetProperty("winner").GetProperty("participantName").GetString());
            Assert.Equal(1, slot.GetProperty("eligibleCount").GetInt32());
        }
        Assert.Equal(JsonValueKind.Null, Slot(result, "Maştağa").GetProperty("winner").ValueKind);
        Assert.Equal(3, _factory.WithDb(db => db.CompetitionCategoryWinners.Count()));
        Assert.Equal(0, _factory.WithDb(db => db.CompetitionResults.Count(r => r.OverallWinnerSubmissionId != null)));

        // Replacing Ramana's winner leaves Balaxanı's alone.
        var ramana2 = await SubmitAsync("ramana", "Ramana — ikinci", "Orxan", "Sultanov");
        var changed = await Json(await ChangeAsync(admin, competition, ramana2.Id, ramana.Id));
        Assert.Equal(ramana2.Reference, Slot(changed, "Ramana").GetProperty("winner").GetProperty("reference").GetString());
        Assert.Equal(balaxani.Reference, Slot(changed, "Balaxanı").GetProperty("winner").GetProperty("reference").GetString());
        Assert.Equal(3, changed.GetProperty("winnerCount").GetInt32());

        // Each submission's page shows its own settlement and that settlement's winner.
        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{ramana.Id}"));
        var r = detail.GetProperty("result");
        Assert.Equal("Ramana", r.GetProperty("category").GetProperty("name").GetString());
        Assert.False(r.GetProperty("isWinner").GetBoolean());
        Assert.Equal(ramana2.Reference, r.GetProperty("currentWinner").GetProperty("reference").GetString());
        var own = (await Json(await admin.GetAsync($"/api/admin/submissions/{balaxani.Id}"))).GetProperty("result");
        Assert.True(own.GetProperty("isWinner").GetBoolean());
        Assert.Equal(JsonValueKind.Null, own.GetProperty("currentWinner").ValueKind);
    }

    [Fact]
    public async Task A_settlement_has_one_winner_and_replacing_or_removing_it_is_explicit_and_audited()
    {
        var a = await SubmitAsync("mastaga", "Birinci", "Rəşad", "Quliyev");
        var b = await SubmitAsync("mastaga", "İkinci", "Aysel", "Kərimova");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        await Json(await SelectAsync(admin, competition, a.Id));
        Assert.Contains("Maştağa üzrə qalib artıq seçilib", await ErrorText(await SelectAsync(admin, competition, b.Id), HttpStatusCode.Conflict));
        await ErrorText(await SelectAsync(admin, competition, a.Id), HttpStatusCode.Conflict);

        // A change must name the current winner; a stale one is refused.
        await ErrorText(await ChangeAsync(admin, competition, b.Id, b.Id), HttpStatusCode.Conflict);
        var changed = await Json(await ChangeAsync(admin, competition, b.Id, a.Id));
        Assert.Equal(b.Reference, Slot(changed, "Maştağa").GetProperty("winner").GetProperty("reference").GetString());

        // The winner cannot be rejected while it is the winner.
        await ErrorText(await admin.PutAsJsonAsync($"/api/admin/submissions/{b.Id}/status", new { status = "Rejected" }), HttpStatusCode.Conflict);

        // Withdrawing the win empties the settlement again.
        var removed = await Json(await RemoveAsync(admin, competition, b.Id));
        Assert.Equal(JsonValueKind.Null, Slot(removed, "Maştağa").GetProperty("winner").ValueKind);
        Assert.Equal("NoWinner", removed.GetProperty("state").GetString());
        await ErrorText(await RemoveAsync(admin, competition, b.Id), HttpStatusCode.NotFound);
        await SetStatusAsync(admin, b.Id, "Rejected"); // no longer a winner: moderation is free again

        var audit = await AuditAsync(admin, competition);
        var selected = Assert.Single(audit, x => x.Action == "CategoryWinnerSelected");
        Assert.Contains("Maştağa", selected.Summary);
        Assert.Contains(a.Reference, selected.Summary);
        var change = Assert.Single(audit, x => x.Action == "CategoryWinnerChanged");
        Assert.Contains(a.Reference, change.Summary);
        Assert.Contains(b.Reference, change.Summary);
        Assert.Contains(b.Reference, Assert.Single(audit, x => x.Action == "CategoryWinnerRemoved").Summary);
        Assert.All(audit, x => Assert.DoesNotContain("Birinci", x.Summary));   // no submitted content in the audit
        Assert.All(audit, x => Assert.DoesNotContain("Quliyev", x.Summary));   // nor participants' names
    }

    [Fact]
    public async Task A_work_can_only_win_its_own_settlement_even_by_hand()
    {
        var balaxani = await SubmitAsync("balaxani", "Balaxanı şevronu");
        var ramana = await SubmitAsync("ramana", "Ramana qalası");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await Json(await SelectAsync(admin, competition, ramana.Id));

        // Through the API: a Balaxanı work can only ever be Balaxanı's winner — it cannot replace Ramana's.
        Assert.Contains("hələ qalib seçilməyib", await ErrorText(await ChangeAsync(admin, competition, balaxani.Id, ramana.Id), HttpStatusCode.Conflict));

        var ramanaCategory = _factory.WithDb(db => db.CompetitionCategories.Single(k => k.CompetitionId == competition && k.Slug == "ramana").Id);
        var mastagaCategory = _factory.WithDb(db => db.CompetitionCategories.Single(k => k.CompetitionId == competition && k.Slug == "mastaga").Id);
        await _factory.WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            // Balaxanı's work stored as Maştağa's winner: the (submission, category) key refuses it.
            var foreignCategory = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "CompetitionCategoryWinners" ("CompetitionId","CompetitionCategoryId","SubmissionId","SelectedAt") VALUES ({competition}, {mastagaCategory}, {balaxani.Id}, {now})"""));
            Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, foreignCategory.SqlState);
            // A second winner for Ramana: unique per category.
            var second = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "CompetitionCategoryWinners" ("CompetitionId","CompetitionCategoryId","SubmissionId","SelectedAt") VALUES ({competition}, {ramanaCategory}, {balaxani.Id}, {now})"""));
            Assert.Contains(second.SqlState, new[] { Npgsql.PostgresErrorCodes.UniqueViolation, Npgsql.PostgresErrorCodes.ForeignKeyViolation });
            // Moving the winning work to another settlement afterwards is refused as well (ON UPDATE RESTRICT).
            var moved = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Submissions" SET "CompetitionCategoryId" = {mastagaCategory} WHERE "Id" = {ramana.Id}"""));
            Assert.Equal(Npgsql.PostgresErrorCodes.RestrictViolation, moved.SqlState);
        });
    }

    [Fact]
    public async Task Concurrent_selections_cannot_give_one_settlement_two_winners()
    {
        var mastaga = new List<Entry>();
        for (var i = 0; i < 4; i++) mastaga.Add(await SubmitAsync("mastaga", $"Maştağa {i}"));
        var others = new List<Entry>();
        foreach (var s in new[] { "emircan", "suvelan", "ramana" }) others.Add(await SubmitAsync(s, $"İş {s}"));
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        using var start = new SemaphoreSlim(0);
        var all = mastaga.Concat(others).ToList();
        var tasks = all.Select(async e =>
        {
            await start.WaitAsync();
            return (e, (await SelectAsync(admin, competition, e.Id)).StatusCode);
        }).ToList();
        start.Release(all.Count);
        var codes = await Task.WhenAll(tasks);

        var forMastaga = codes.Where(c => mastaga.Contains(c.e)).Select(c => c.StatusCode).ToList();
        Assert.Equal(1, forMastaga.Count(c => c == HttpStatusCode.OK));
        Assert.All(forMastaga.Where(c => c != HttpStatusCode.OK), c => Assert.Equal(HttpStatusCode.Conflict, c));
        // Different settlements at the same moment never block each other.
        Assert.All(codes.Where(c => others.Contains(c.e)), c => Assert.Equal(HttpStatusCode.OK, c.StatusCode));
        Assert.Equal(4, _factory.WithDb(db => db.CompetitionCategoryWinners.Count()));
    }

    // ---- Before publication -------------------------------------------------------------------

    [Fact]
    public async Task Before_publication_nobody_but_admins_can_discover_the_winners_or_the_entries()
    {
        var winner = await SubmitAsync("mastaga", "Nar və külək", "Leyla", "Quliyeva");
        await SubmitAsync("suvelan", "Şüvəlan dalğası");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await Json(await SelectAsync(admin, competition, winner.Id));
        await Json(await CloseAsync(admin, competition));   // closed for entries, but results are not published

        var fileId = _factory.WithDb(db => db.SubmissionFiles.First(f => f.SubmissionId == winner.Id).Id);
        string[] urls =
        [
            $"/api/competitions/{Chevron}/results",
            $"/api/competitions/{Chevron}/gallery",
            $"/api/competitions/{Chevron}/gallery?category=mastaga",
            $"/api/competitions/{Chevron}/gallery/{winner.Reference}",
            $"/api/competitions/{Chevron}/gallery/{winner.Reference}/files/{fileId}",
            $"/api/competitions/{Chevron}/gallery/{winner.Reference}/files/{fileId}/thumb",
        ];

        var anonymous = _factory.CreateClient();
        var (participant, _) = await ParticipantAsync();
        foreach (var client in new[] { anonymous, participant })
        {
            foreach (var url in urls)
                Assert.DoesNotContain("Quliyeva", await ErrorText(await client.GetAsync(url), HttpStatusCode.NotFound));
            Assert.Empty((await Json(await client.GetAsync("/api/results"))).EnumerateArray());
            var detail = await Json(await client.GetAsync($"/api/competitions/{Chevron}"));
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("resultsPublishedAt").ValueKind);
            Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"/api/admin/competitions/{competition}/result")).StatusCode);
        }

        // The winner's own "İşlərim" says nothing yet.
        var winnerClient = _factory.CreateClient();
        winnerClient.UseToken(await winnerClient.LoginAsync(winner.Email, "secret123"));
        var mine = Assert.Single((await Json(await winnerClient.GetAsync("/api/me/submissions"))).EnumerateArray());
        Assert.False(mine.GetProperty("resultsPublished").GetBoolean());
        Assert.False(mine.GetProperty("isWinner").GetBoolean());

        // Admins see the prepared result.
        var result = await Json(await admin.GetAsync($"/api/admin/competitions/{competition}/result"));
        Assert.Equal("Prepared", result.GetProperty("state").GetString());
        Assert.Equal(winner.Reference, Slot(result, "Maştağa").GetProperty("winner").GetProperty("reference").GetString());
        Assert.Equal(2, result.GetProperty("eligibleCount").GetInt32());
        Assert.Empty(result.GetProperty("publishProblems").EnumerateArray());
    }

    // ---- Eligibility ----------------------------------------------------------------------------

    [Fact]
    public async Task Only_final_non_rejected_entries_of_the_same_competition_can_win()
    {
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        var (draftOwner, _) = await ParticipantAsync();
        var draftId = await DraftAsync(draftOwner, "mastaga", "Qaralama");
        Assert.Contains("Qaralama qalib seçilə bilməz", await ErrorText(await SelectAsync(admin, competition, draftId), HttpStatusCode.BadRequest));

        var rejected = await SubmitAsync("nardaran", "Rədd");
        await SetStatusAsync(admin, rejected.Id, "Rejected");
        Assert.Contains("Rədd edilmiş", await ErrorText(await SelectAsync(admin, competition, rejected.Id), HttpStatusCode.BadRequest));

        Assert.Contains("tapılmadı", await ErrorText(await SelectAsync(admin, competition, 999_999), HttpStatusCode.BadRequest));

        // A submission of another competition (a generic one created through the admin API).
        var project = await Json(await admin.GetAsync("/api/admin/projects"));
        var projectId = project.EnumerateArray().First().GetProperty("id").GetInt32();
        var other = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Laçın loqosu", slug = "lacin-loqosu", shortDescription = "Sınaq", hasCategories = false,
        }));
        var otherId = other.GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{otherId}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{otherId}/status", new { status = "Published" }));
        var (p, _) = await ParticipantAsync();
        var d = await Json(await p.PostAsJsonAsync("/api/submissions", new { competitionSlug = "lacin-loqosu" }));
        var foreignId = d.GetProperty("id").GetInt32();
        await Json(await p.PutAsJsonAsync($"/api/submissions/{foreignId}", new { answers = new { ad = "Qartal" } }));
        await Json(await p.PostAsync($"/api/submissions/{foreignId}/submit", null));
        Assert.Contains("bu müsabiqəyə aid deyil", await ErrorText(await SelectAsync(admin, competition, foreignId), HttpStatusCode.BadRequest));

        Assert.Equal(0, _factory.WithDb(db => db.CompetitionCategoryWinners.Count()));
        Assert.Equal(0, _factory.WithDb(db => db.CompetitionResults.Count()));
    }

    // ---- Publishing -----------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_needs_at_least_one_winner_a_closed_competition_and_an_admin_but_not_every_settlement()
    {
        var a = await SubmitAsync("mastaga", "Birinci");
        await SubmitAsync("suvelan", "Qalibsiz qəsəbə");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);

        Assert.Contains("Heç bir qalib seçilməyib", await ErrorText(await PublishAsync(admin, competition), HttpStatusCode.BadRequest));
        await Json(await SelectAsync(admin, competition, a.Id));
        // Still accepting entries: publishing would show work to people who can still submit.
        Assert.Contains("qəbulu bağlayın", await ErrorText(await PublishAsync(admin, competition), HttpStatusCode.BadRequest));
        await Json(await CloseAsync(admin, competition));

        var (participant, _) = await ParticipantAsync();
        var publish = $"/api/admin/competitions/{competition}/result/publish";
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.PostAsync(publish, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync(publish, null)).StatusCode);
        await ErrorText(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/results"), HttpStatusCode.NotFound);

        // 1 winner of 59 settlements is enough: the admin decides; the other 58 simply have none yet.
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var published = await Json(await PublishAsync(admin, competition));
        Assert.Equal("Published", published.GetProperty("state").GetString());
        Assert.True(published.GetProperty("isPublicNow").GetBoolean());
        Assert.Equal(1, published.GetProperty("winnerCount").GetInt32());
        Assert.Equal(59, published.GetProperty("categoryCount").GetInt32());
        var at = published.GetProperty("publishedAt").GetDateTimeOffset();
        Assert.True(at > before && at.Offset == TimeSpan.Zero);

        var audit = await AuditAsync(admin, competition);
        Assert.Contains(audit, x => x.Action == "ResultsPublished" && x.Summary.Contains("1 of 59"));
    }

    [Fact]
    public async Task Changing_a_published_result_needs_explicit_confirmation_and_keeps_at_least_one_winner()
    {
        var (admin, competition, balaxani, bileceri, ramana, other, _) = await PublishedScenarioAsync();
        var mastaga = await SubmitClosedAsync(admin, competition, "mastaga", "Maştağa sonradan");
        var anonymous = _factory.CreateClient();

        // Without the confirmation nothing changes — a stale screen cannot silently alter public winners.
        Assert.Contains("dərhal görünəcək", await ErrorText(await SelectAsync(admin, competition, mastaga.Id), HttpStatusCode.Conflict));
        Assert.Contains("dərhal görünəcək", await ErrorText(await ChangeAsync(admin, competition, other.Id, balaxani.Id), HttpStatusCode.Conflict));
        Assert.Contains("dərhal görünəcək", await ErrorText(await RemoveAsync(admin, competition, ramana.Id), HttpStatusCode.Conflict));
        Assert.Equal(3, (await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/results"))).GetProperty("winnerCount").GetInt32());

        // Confirmed: visible at once.
        await Json(await SelectAsync(admin, competition, mastaga.Id, confirm: true));
        await Json(await ChangeAsync(admin, competition, other.Id, balaxani.Id, confirm: true));
        await Json(await RemoveAsync(admin, competition, ramana.Id, confirm: true));
        var result = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/results"));
        Assert.Equal(3, result.GetProperty("winnerCount").GetInt32());
        var winners = result.GetProperty("categories").EnumerateArray()
            .Where(c => c.GetProperty("winner").ValueKind != JsonValueKind.Null)
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("winner").GetProperty("reference").GetString());
        Assert.Equal(other.Reference, winners["Balaxanı"]);
        Assert.Equal(bileceri.Reference, winners["Biləcəri"]);
        Assert.Equal(mastaga.Reference, winners["Maştağa"]);
        Assert.False(winners.ContainsKey("Ramana"));

        // The last winner of a published result cannot be removed; withdraw the result first.
        await Json(await RemoveAsync(admin, competition, other.Id, confirm: true));
        await Json(await RemoveAsync(admin, competition, mastaga.Id, confirm: true));
        Assert.Contains("ən azı bir qalib", await ErrorText(await RemoveAsync(admin, competition, bileceri.Id, confirm: true), HttpStatusCode.Conflict));

        var audit = await AuditAsync(admin, competition);
        Assert.Contains(audit, x => x.Action == "CategoryWinnerSelected" && x.Summary.Contains("published result"));
        Assert.Contains(audit, x => x.Action == "CategoryWinnerRemoved" && x.Summary.Contains("Ramana"));
    }

    /// <summary>An entry added to a competition that is already closed: reopen for a moment, submit, close again.</summary>
    private async Task<Entry> SubmitClosedAsync(HttpClient admin, int competition, string settlement, string title)
    {
        var published = await Json(await admin.GetAsync($"/api/admin/competitions/{competition}/result"));
        Assert.Equal("Published", published.GetProperty("state").GetString());
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competition}/status", new { status = "Published" }));
        var entry = await SubmitAsync(settlement, title);
        await Json(await CloseAsync(admin, competition));
        return entry;
    }

    // ---- After publication ----------------------------------------------------------------------

    [Fact]
    public async Task After_publication_winners_are_named_and_only_public_data_is_exposed()
    {
        var (_, _, balaxani, bileceri, ramana, other, rejected) = await PublishedScenarioAsync();
        var anonymous = _factory.CreateClient();

        var result = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/results"));
        Assert.Equal(4, result.GetProperty("entryCount").GetInt32());   // rejected work is not counted
        Assert.Equal(3, result.GetProperty("winnerCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("overallWinner").ValueKind);
        var categories = result.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(59, categories.Count);                              // every settlement, in order, winner or not
        var balaxaniCategory = categories.Single(c => c.GetProperty("name").GetString() == "Balaxanı");
        Assert.Equal(2, balaxaniCategory.GetProperty("entryCount").GetInt32());
        var w = balaxaniCategory.GetProperty("winner");
        Assert.Equal(balaxani.Reference, w.GetProperty("reference").GetString());
        Assert.Equal("Balaxanı şevronu", w.GetProperty("title").GetString());
        Assert.Equal("Leyla Quliyeva", w.GetProperty("winnerName").GetString());
        Assert.True(w.GetProperty("isWinner").GetBoolean());
        // Only the title field is public by default: no image yet, and the card says why.
        Assert.Equal(JsonValueKind.Null, w.GetProperty("preview").ValueKind);
        Assert.Equal("None", w.GetProperty("previewState").GetString());
        Assert.Equal(JsonValueKind.Null, categories.Single(c => c.GetProperty("name").GetString() == "Maştağa").GetProperty("winner").ValueKind);

        var list = await Json(await anonymous.GetAsync("/api/results"));
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(59, item.GetProperty("categoryCount").GetInt32());
        Assert.Equal(3, item.GetProperty("winnerCount").GetInt32());
        Assert.Equal(4, item.GetProperty("entryCount").GetInt32());
        Assert.Equal("Bakı kəndləri", item.GetProperty("competition").GetProperty("project").GetProperty("name").GetString());
        Assert.False(item.TryGetProperty("winner", out _));              // no single "the winner" of the competition

        var gallery = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery"));
        var items = gallery.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal(3, gallery.GetProperty("winnerCount").GetInt32());
        Assert.DoesNotContain(items, i => i.GetProperty("reference").GetString() == rejected.Reference);
        var nonWinner = items.Single(i => i.GetProperty("reference").GetString() == other.Reference);
        Assert.False(nonWinner.GetProperty("isWinner").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nonWinner.GetProperty("winnerName").ValueKind);
        // Within Balaxanı the winner comes first.
        var balaxaniRefs = items.Where(i => i.GetProperty("category").GetProperty("name").GetString() == "Balaxanı")
            .Select(i => i.GetProperty("reference").GetString()).ToList();
        Assert.Equal([balaxani.Reference, other.Reference], balaxaniRefs);

        var work = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{ramana.Reference}"));
        Assert.Equal("Nigar Həsənova", work.GetProperty("winnerName").GetString());
        var plain = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{other.Reference}"));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("winnerName").ValueKind);
        await ErrorText(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{rejected.Reference}"), HttpStatusCode.NotFound);

        // Winners' names are public; nothing else private is — and no non-winner's name, anywhere.
        foreach (var url in new[]
                 {
                     $"/api/competitions/{Chevron}/results", $"/api/competitions/{Chevron}/gallery", "/api/results",
                     $"/api/competitions/{Chevron}/gallery/{balaxani.Reference}", $"/api/competitions/{Chevron}/gallery/{other.Reference}",
                     $"/api/competitions/{Chevron}",
                 })
        {
            var body = await (await anonymous.GetAsync(url)).Content.ReadAsStringAsync();
            foreach (var secret in new[]
                     {
                         balaxani.Email, bileceri.Email, other.Email, "Rzayeva", "Səbinə", "Babayev", "+994", "GİZLİ-ADMİN-QEYDİ",
                         "Gizli konsept", "Şəxsi əlavə qeyd", "Aysel_Məmmədova_eskiz", "UnderReview", "Submitted", "userId",
                         "phone", "email",
                     })
                Assert.DoesNotContain(secret, body);
            Assert.False(StorageKey().IsMatch(body), $"{url} exposes a storage key");
        }
    }

    [Fact]
    public async Task Participants_learn_the_result_from_Islerim_only_after_publication()
    {
        var (_, _, balaxani, _, ramana, other, _) = await PublishedScenarioAsync();
        foreach (var (entry, isWinner) in new[] { (balaxani, true), (ramana, true), (other, false) })
        {
            var client = _factory.CreateClient();
            client.UseToken(await client.LoginAsync(entry.Email, "secret123"));
            var mine = Assert.Single((await Json(await client.GetAsync("/api/me/submissions"))).EnumerateArray());
            Assert.True(mine.GetProperty("resultsPublished").GetBoolean());
            Assert.Equal(isWinner, mine.GetProperty("isWinner").GetBoolean());
        }
    }

    [Fact]
    public async Task Unpublishing_or_reopening_hides_the_result_and_the_winners_names_again()
    {
        var (admin, competition, balaxani, _, _, _, _) = await PublishedScenarioAsync();
        var anonymous = _factory.CreateClient();
        var results = $"/api/competitions/{Chevron}/results";
        Assert.Contains("Leyla Quliyeva", await (await anonymous.GetAsync(results)).Content.ReadAsStringAsync());

        // Reopening entries hides results and gallery instead of showing them to people who can still submit.
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competition}/status", new { status = "Published" }));
        await ErrorText(await anonymous.GetAsync(results), HttpStatusCode.NotFound);
        // The admin is told the truth: still published, but not visible right now.
        var hidden = (await Json(await admin.GetAsync($"/api/admin/submissions/{balaxani.Id}"))).GetProperty("result");
        Assert.True(hidden.GetProperty("resultPublished").GetBoolean());
        Assert.False(hidden.GetProperty("resultPublicNow").GetBoolean());
        await ErrorText(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery"), HttpStatusCode.NotFound);
        await Json(await CloseAsync(admin, competition));
        await Json(await anonymous.GetAsync(results));

        var withdrawn = await Json(await admin.PostAsync($"/api/admin/competitions/{competition}/result/unpublish", null));
        Assert.Equal("Prepared", withdrawn.GetProperty("state").GetString());
        Assert.Equal(3, withdrawn.GetProperty("winnerCount").GetInt32());   // winners are kept
        Assert.DoesNotContain("Quliyeva", await ErrorText(await anonymous.GetAsync(results), HttpStatusCode.NotFound));
        await ErrorText(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{balaxani.Reference}"), HttpStatusCode.NotFound);
        Assert.Contains(await AuditAsync(admin, competition), x => x.Action == "ResultsUnpublished");
    }

    [Fact]
    public async Task Admin_submission_list_filters_winners_of_every_settlement()
    {
        var (admin, competition, balaxani, bileceri, ramana, other, _) = await PublishedScenarioAsync();
        var winners = await Json(await admin.GetAsync($"/api/admin/submissions?competitionId={competition}&winner=true"));
        Assert.Equal(new[] { balaxani.Id, bileceri.Id, ramana.Id }.Order(),
            winners.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).Order());
        Assert.All(winners.GetProperty("items").EnumerateArray(), x => Assert.True(x.GetProperty("isWinner").GetBoolean()));
        var rest = await Json(await admin.GetAsync($"/api/admin/submissions?competitionId={competition}&winner=false"));
        Assert.Contains(rest.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetInt32() == other.Id);
        Assert.DoesNotContain(rest.GetProperty("items").EnumerateArray(), x => x.GetProperty("isWinner").GetBoolean());

        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{other.Id}"));
        var r = detail.GetProperty("result");
        Assert.Equal("Balaxanı", r.GetProperty("category").GetProperty("name").GetString());
        Assert.False(r.GetProperty("isWinner").GetBoolean());
        Assert.True(r.GetProperty("canBeWinner").GetBoolean());
        Assert.True(r.GetProperty("resultPublished").GetBoolean());
        Assert.True(r.GetProperty("resultPublicNow").GetBoolean());
        Assert.Equal(balaxani.Reference, r.GetProperty("currentWinner").GetProperty("reference").GetString());
        Assert.Equal("Leyla Quliyeva", r.GetProperty("currentWinner").GetProperty("participantName").GetString());
    }

    [Fact]
    public async Task A_generic_competition_without_categories_has_one_overall_winner()
    {
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Laçın loqosu", slug = "lacin-loqosu", shortDescription = "Sınaq", hasCategories = false,
        }));
        var id = c.GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var ids = new List<int>();
        foreach (var (title, first) in new[] { ("Qartal", "Elvin"), ("Dağ", "Fidan") })
        {
            var (p, _) = await ParticipantAsync(first, "Nəsirova");
            var d = await Json(await p.PostAsJsonAsync("/api/submissions", new { competitionSlug = "lacin-loqosu" }));
            var sid = d.GetProperty("id").GetInt32();
            await Json(await p.PutAsJsonAsync($"/api/submissions/{sid}", new { answers = new { ad = title } }));
            await Json(await p.PostAsync($"/api/submissions/{sid}/submit", null));
            ids.Add(sid);
        }
        var chosen = await Json(await SelectAsync(admin, id, ids[1]));
        var slot = Assert.Single(chosen.GetProperty("slots").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("category").ValueKind);
        Assert.Equal(0, chosen.GetProperty("categoryCount").GetInt32());
        Assert.Contains("artıq qalib seçilib", await ErrorText(await SelectAsync(admin, id, ids[0]), HttpStatusCode.Conflict));
        await Json(await CloseAsync(admin, id));
        await Json(await PublishAsync(admin, id));

        var result = await Json(await _factory.CreateClient().GetAsync("/api/competitions/lacin-loqosu/results"));
        Assert.Equal("Dağ", result.GetProperty("overallWinner").GetProperty("title").GetString());
        Assert.Equal("Fidan Nəsirova", result.GetProperty("overallWinner").GetProperty("winnerName").GetString());
        Assert.Empty(result.GetProperty("categories").EnumerateArray());
        var gallery = await Json(await _factory.CreateClient().GetAsync("/api/competitions/lacin-loqosu/gallery"));
        Assert.Equal(2, gallery.GetProperty("total").GetInt32());
        Assert.Empty(gallery.GetProperty("categories").EnumerateArray());
        Assert.DoesNotContain("Elvin", gallery.GetRawText());   // the non-winner stays anonymous
        // The Bakı competition is unaffected.
        await ErrorText(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/results"), HttpStatusCode.NotFound);
    }

    // ---- Results cover ----------------------------------------------------------------------------

    [Fact]
    public async Task Admins_upload_a_results_cover_that_is_metadata_free_and_preferred_over_other_covers()
    {
        var (admin, competition, _, _, _, _, _) = await PublishedScenarioAsync();
        var anonymous = _factory.CreateClient();
        var cover = $"/api/admin/competitions/{competition}/result/cover";

        // No banner yet: the list falls back to the project's cover (or none) — never a submission's image.
        var item = Assert.Single((await Json(await anonymous.GetAsync("/api/results"))).EnumerateArray());
        Assert.NotEqual("Result", item.GetProperty("coverKind").ToString());

        var jpeg = TestImages.JpegWithExif(240, 120, orientation: 1);
        Assert.NotEmpty(TestImages.LeakedMarkers(jpeg));
        HttpContent Form(byte[] bytes, string name, string type)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(file, "cover", name);
            return form;
        }

        // Only admins; only real raster images.
        var (participant, _) = await ParticipantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.PostAsync(cover, Form(jpeg, "urra.jpg", "image/jpeg"))).StatusCode);
        await ErrorText(await admin.PostAsync(cover, Form("<svg/>"u8.ToArray(), "urra.svg", "image/svg+xml")), HttpStatusCode.BadRequest);

        var updated = await Json(await admin.PostAsync(cover, Form(jpeg, "urra.jpg", "image/jpeg")));
        var url = updated.GetProperty("coverImage").GetString()!;
        Assert.StartsWith("/api/media/covers/", url);
        Assert.EndsWith(".webp", url);
        var served = await anonymous.GetAsync(url);
        Assert.Equal("image/webp", served.Content.Headers.ContentType?.MediaType);
        Assert.Empty(TestImages.LeakedMarkers(await served.Content.ReadAsByteArrayAsync()));   // no EXIF/GPS

        item = Assert.Single((await Json(await anonymous.GetAsync("/api/results"))).EnumerateArray());
        Assert.Equal(url, item.GetProperty("coverImage").GetString());
        Assert.Equal("Result", item.GetProperty("coverKind").GetString());
        Assert.Equal(url, (await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/results"))).GetProperty("coverImage").GetString());

        // Replacing deletes the old file; removing falls back again.
        var replaced = await Json(await admin.PostAsync(cover, Form(TestImages.Png(300, 100), "yeni.png", "image/png")));
        Assert.NotEqual(url, replaced.GetProperty("coverImage").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        var removed = await Json(await admin.DeleteAsync(cover));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("coverImage").ValueKind);
        Assert.Equal(3, (await AuditAsync(admin, competition)).Count(x => x.Action == "ResultCoverChanged"));
    }
}
