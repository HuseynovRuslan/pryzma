using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Meydan.Infrastructure.Persistence;

namespace Meydan.Api.Tests;

public sealed class SubmissionTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>A real, decodable PNG padded to <paramref name="size"/> bytes.</summary>
    private static byte[] Png(int size = 256) => TestImages.PaddedPng(size);

    private async Task<HttpClient> SignedInAsync(string email)
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync(email));
        return client;
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, string? category = "mastaga")
    {
        var res = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = category });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, string fileName, byte[] content,
        string contentType = "image/png", string fieldKey = "files")
    {
        var form = new MultipartFormDataContent { { new StringContent(fieldKey), "fieldKey" } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/submissions/{id}/files", form);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, int id, object answers) =>
        client.PutAsJsonAsync($"/api/submissions/{id}", new { answers });

    private static readonly object CompleteAnswers = new
    {
        title = "Maştağa şevronu",
        concept = "Qəsəbənin tarixi motivləri əsasında hazırlanıb.",
        symbols = "Nar — bolluq, dalğa — Xəzər.",
    };

    private static async Task<int> ReadyDraftAsync(HttpClient client, string category = "mastaga")
    {
        var id = (await StartAsync(client, category)).GetProperty("id").GetInt32();
        (await SaveAsync(client, id, CompleteAnswers)).EnsureSuccessStatusCode();
        (await UploadAsync(client, id, "eskiz.png", Png())).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<JsonElement> Errors(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

    // ---- Categories -------------------------------------------------------------------------

    [Fact]
    public async Task Chevron_competition_lists_the_59_settlements_with_stable_slugs()
    {
        var c = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/competitions/{Chevron}");
        var categories = c.GetProperty("categories").EnumerateArray()
            .Select(x => (Name: x.GetProperty("name").GetString()!, Slug: x.GetProperty("slug").GetString()!))
            .ToList();

        Assert.Equal(59, categories.Count);
        Assert.Equal("Qəsəbələr", c.GetProperty("categoryLabel").GetString());
        Assert.Equal("Biləcəri", categories[0].Name);
        Assert.Equal("Zığ", categories[^1].Name);
        Assert.Contains(("Maştağa", "mastaga"), categories);
        Assert.Contains(("Şüvəlan", "suvelan"), categories);
        Assert.Contains(("Əmircan", "emircan"), categories);
        Assert.Contains(("Kürdəxanı", "kurdexani"), categories);
        Assert.Contains(("M.Ə. Rəsulzadə", "m-e-resulzade"), categories);
        Assert.Contains(("28 May", "28-may"), categories);
        Assert.DoesNotContain(categories, x => x.Slug.StartsWith("numune"));
        Assert.Equal(59, categories.Select(x => x.Slug).Distinct().Count());
        Assert.All(categories, x => Assert.Matches("^[a-z0-9-]+$", x.Slug));
    }

    [Fact]
    public async Task Settlement_seed_runs_once_and_admin_edits_survive_a_restart()
    {
        await _factory.WithDbAsync(async db =>
        {
            var zig = await db.CompetitionCategories.SingleAsync(c => c.Slug == "zig");
            db.CompetitionCategories.Remove(zig);
            await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();

        Assert.Equal(58, _factory.WithDb(db => db.CompetitionCategories.Count()));
    }

    // ---- Drafts & ownership -----------------------------------------------------------------

    [Fact]
    public async Task Anonymous_users_cannot_start_a_submission()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = "mastaga" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Starting_creates_one_draft_and_resumes_it_on_the_next_click()
    {
        var client = await SignedInAsync("draft@example.com");

        var first = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "mastaga" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var draft = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("referenceNumber").ValueKind);
        Assert.Equal("Maştağa", draft.GetProperty("category").GetProperty("name").GetString());
        Assert.True(draft.GetProperty("isEditable").GetBoolean());
        Assert.Equal(["title", "concept", "symbols", "notes", "files"],
            draft.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()));

        // Upload policy comes from config exactly once (the binder must not append to defaults).
        var fileRules = draft.GetProperty("fields")[4].GetProperty("files");
        Assert.Equal(["png", "jpeg", "webp", "pdf"],
            fileRules.GetProperty("accept").EnumerateArray().Select(a => a.GetProperty("key").GetString()));
        Assert.Equal(5, fileRules.GetProperty("maxFiles").GetInt32());
        Assert.Equal(10 * 1024 * 1024, fileRules.GetProperty("maxFileSizeBytes").GetInt64());

        var again = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "mastaga" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(draft.GetProperty("id").GetInt32(),
            (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("yoxdur")]
    public async Task A_valid_settlement_is_required(string? category)
    {
        var client = await SignedInAsync("cat@example.com");
        var res = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = category });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await Errors(res)).TryGetProperty("category", out _));
    }

    [Fact]
    public async Task Another_user_cannot_see_change_upload_to_or_submit_a_draft()
    {
        var owner = await SignedInAsync("owner@example.com");
        var id = await ReadyDraftAsync(owner);
        var fileId = (await owner.GetFromJsonAsync<JsonElement>($"/api/submissions/{id}"))
            .GetProperty("files")[0].GetProperty("id").GetInt32();

        var intruder = await SignedInAsync("intruder@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/submissions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(intruder, id, new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(intruder, id, "a.png", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/submissions/{id}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.DeleteAsync($"/api/submissions/{id}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.PostAsync($"/api/submissions/{id}/submit", null)).StatusCode);

        var mine = await intruder.GetFromJsonAsync<JsonElement>("/api/me/submissions");
        Assert.Empty(mine.EnumerateArray());
    }

    // ---- Dynamic field validation -----------------------------------------------------------

    [Fact]
    public async Task Final_submit_reports_every_missing_required_field()
    {
        var client = await SignedInAsync("empty@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();

        var res = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = await Errors(res);
        foreach (var key in new[] { "title", "concept", "symbols", "files" })
            Assert.True(errors.TryGetProperty(key, out _), key);
        Assert.False(errors.TryGetProperty("notes", out _)); // optional
        Assert.Equal("“İşin adı” sahəsini doldurun.", errors.GetProperty("title")[0].GetString());
    }

    [Fact]
    public async Task Saving_rejects_unknown_fields_and_values_over_the_limit()
    {
        var client = await SignedInAsync("limits@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();

        var tooLong = await SaveAsync(client, id, new { title = new string('ş', 151) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("150", (await Errors(tooLong)).GetProperty("title")[0].GetString());

        var unknown = await SaveAsync(client, id, new { hacker = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var partial = await SaveAsync(client, id, new { title = "  Yarımçıq  " });
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        var body = await partial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Yarımçıq", body.GetProperty("answers").GetProperty("title").GetString());
        Assert.Equal("Yarımçıq", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Admin_defined_fields_drive_the_form_and_its_validation()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        var competitionId = _factory.WithDb(db => db.Competitions.Single().Id);

        var bad = await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields",
            new { label = "Texnika", fieldType = "Select", isRequired = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields", new
        {
            key = "technique", label = "Texnika", fieldType = "Select", isRequired = true, sortOrder = 6,
            optionsJson = "[\"Vektor\",\"Əl işi\"]",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var client = await SignedInAsync("dynamic@example.com");
        var id = await ReadyDraftAsync(client);
        var draft = await client.GetFromJsonAsync<JsonElement>($"/api/submissions/{id}");
        var technique = draft.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "technique");
        Assert.Equal(["Vektor", "Əl işi"], technique.GetProperty("options").EnumerateArray().Select(o => o.GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(client, id, new { technique = "Yağlı boya" })).StatusCode);
        var missing = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.True((await Errors(missing)).TryGetProperty("technique", out _));

        (await SaveAsync(client, id, new { technique = "Əl işi" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/submissions/{id}/submit", null)).StatusCode);

        // A field with answers can no longer be deleted.
        var fieldId = technique.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.DeleteAsync($"/api/admin/competitions/{competitionId}/fields/{fieldId}")).StatusCode);
    }

    // ---- Files --------------------------------------------------------------------------------

    [Fact]
    public async Task Valid_images_and_pdfs_upload_and_are_served_safely_to_the_owner()
    {
        var client = await SignedInAsync("files@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();

        var png = await UploadAsync(client, id, "../../..\\eskiz final.PNG", Png(512));
        Assert.Equal(HttpStatusCode.Created, png.StatusCode);
        var file = await png.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("eskiz final.PNG", file.GetProperty("originalFileName").GetString());
        Assert.Equal("image/png", file.GetProperty("contentType").GetString());
        Assert.Equal(512, file.GetProperty("sizeBytes").GetInt64());

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, id, "izah.pdf", PdfBytes, "application/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await UploadAsync(client, id, "foto.jpeg", TestImages.Jpeg(), "image/jpeg")).StatusCode);

        var download = await client.GetAsync($"/api/submissions/{id}/files/{file.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/png", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png(512), await download.Content.ReadAsByteArrayAsync());
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());

        // Stored under a random name inside the storage root, never under the client's name.
        var stored = Directory.GetFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories);
        Assert.Equal(3, stored.Length);
        Assert.All(stored, p => Assert.Matches(@"[0-9a-f]{32}\.(png|pdf|jpg)$", p));
    }

    [Theory]
    [InlineData("logo.svg", "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("setup.exe", "application/octet-stream", "MZ ")]
    [InlineData("sened.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "PK")]
    [InlineData("skript.png.js", "text/javascript", "alert(1)")]
    public async Task Disallowed_formats_are_rejected(string name, string contentType, string content)
    {
        var client = await SignedInAsync($"bad-{Guid.NewGuid():N}@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();
        var res = await UploadAsync(client, id, name, Encoding.Latin1.GetBytes(content), contentType);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("qəbul olunmur", (await Errors(res)).GetProperty("files")[0].GetString());
        Assert.Empty(Directory.GetFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Content_must_match_the_extension_and_the_declared_type()
    {
        var client = await SignedInAsync("disguise@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();

        var renamed = await UploadAsync(client, id, "sekil.png", Encoding.ASCII.GetBytes("MZ this is really an exe"));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Contains("uyğun deyil", (await Errors(renamed)).GetProperty("files")[0].GetString());

        var pdfAsPng = await UploadAsync(client, id, "sekil.png", PdfBytes, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, pdfAsPng.StatusCode);

        var lyingMime = await UploadAsync(client, id, "sekil.png", Png(), "application/pdf");
        Assert.Equal(HttpStatusCode.BadRequest, lyingMime.StatusCode);
    }

    [Fact]
    public async Task Oversized_files_and_a_sixth_file_are_rejected()
    {
        var client = await SignedInAsync("big@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();

        var big = await UploadAsync(client, id, "boyuk.png", Png(10 * 1024 * 1024 + 1));
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        Assert.Contains("10 MB", (await Errors(big)).GetProperty("files")[0].GetString());

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, id, "tam.png", Png(10 * 1024 * 1024))).StatusCode);
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, id, $"f{i}.png", Png())).StatusCode);

        var sixth = await UploadAsync(client, id, "artiq.png", Png());
        Assert.Equal(HttpStatusCode.BadRequest, sixth.StatusCode);
        Assert.Contains("5 fayl", (await Errors(sixth)).GetProperty("files")[0].GetString());
    }

    [Fact]
    public async Task A_real_photo_of_about_9_5_mb_is_accepted_and_gets_a_public_derivative()
    {
        var client = await SignedInAsync("photo@example.com");
        var id = await ReadyDraftAsync(client);
        var photo = TestImages.NoisePng(9_600_000);
        Assert.InRange(photo.Length, 9_000_000, 10 * 1024 * 1024);

        var res = await UploadAsync(client, id, "foto.png", photo);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var fileId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(photo, await (await client.GetAsync($"/api/submissions/{id}/files/{fileId}")).Content.ReadAsByteArrayAsync());

        (await client.PostAsync($"/api/submissions/{id}/submit", null)).EnsureSuccessStatusCode();
        PublicAssetStatus status = PublicAssetStatus.Pending;
        for (var i = 0; i < 300 && status == PublicAssetStatus.Pending; i++)
        {
            await Task.Delay(100);
            status = _factory.WithDb(db => db.SubmissionFiles.Single(f => f.Id == fileId).PublicAssetStatus);
        }
        Assert.Equal(PublicAssetStatus.Ready, status);
    }

    [Fact]
    public async Task One_upload_request_carries_exactly_one_file()
    {
        var client = await SignedInAsync("two@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        foreach (var name in new[] { "a.png", "b.png" })
        {
            var file = new ByteArrayContent(Png());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", name);
        }
        var res = await client.PostAsync($"/api/submissions/{id}/files", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("yalnız bir fayl", (await Errors(res)).GetProperty("file")[0].GetString());

        var none = await client.PostAsync($"/api/submissions/{id}/files",
            new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("Fayl seçilməyib.", (await Errors(none)).GetProperty("file")[0].GetString());
        Assert.Equal(0, _factory.WithDb(db => db.SubmissionFiles.Count(f => f.SubmissionId == id)));
    }

    [Fact]
    public async Task A_broken_multipart_body_gets_an_azerbaijani_answer_not_framework_text()
    {
        var client = await SignedInAsync("broken@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();
        var body = new ByteArrayContent(Encoding.ASCII.GetBytes("--xyz\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\n\r\nPNG..."));
        body.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=xyz");
        var res = await client.PostAsync($"/api/submissions/{id}/files", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("Göndərilən fayl oxunmadı", text);
        Assert.DoesNotContain("Failed to read", text);
        Assert.DoesNotContain("Unexpected end", text);
    }

    [Fact]
    public async Task Removing_a_file_deletes_it_from_storage()
    {
        var client = await SignedInAsync("remove@example.com");
        var id = (await StartAsync(client)).GetProperty("id").GetInt32();
        var file = await (await UploadAsync(client, id, "a.png", Png())).Content.ReadFromJsonAsync<JsonElement>();

        var res = await client.DeleteAsync($"/api/submissions/{id}/files/{file.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(Directory.GetFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories));
    }

    // ---- Final submission ---------------------------------------------------------------------

    [Fact]
    public async Task Final_submit_assigns_a_reference_and_locks_the_submission()
    {
        var client = await SignedInAsync("final@example.com");
        var id = await ReadyDraftAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/submissions/{id}/check", null)).StatusCode);

        var res = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var submitted = await res.Content.ReadFromJsonAsync<JsonElement>();
        var reference = submitted.GetProperty("referenceNumber").GetString()!;
        Assert.Matches(new Regex($@"^SUB-{_factory.Clock.Now.Year}-\d{{5}}$"), reference);
        Assert.Equal("Submitted", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("isEditable").GetBoolean());

        // Duplicate submit is refused and does not mint another number.
        var again = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Read-only from now on.
        Assert.Equal(HttpStatusCode.Conflict, (await SaveAsync(client, id, new { title = "Dəyişdim" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, id, "yeni.png", Png())).StatusCode);
        var fileId = submitted.GetProperty("files")[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/submissions/{id}/files/{fileId}")).StatusCode);

        var stored = _factory.WithDb(db => db.Submissions.Single(s => s.Id == id));
        Assert.Equal(reference, stored.ReferenceNumber);
        Assert.Equal("Maştağa şevronu", stored.Title);

        // İşlərim shows it with its reference, after a fresh request (i.e. a page refresh).
        var mine = (await client.GetFromJsonAsync<JsonElement>("/api/me/submissions"))!.EnumerateArray().Single();
        Assert.Equal(reference, mine.GetProperty("referenceNumber").GetString());
        Assert.Equal("Submitted", mine.GetProperty("status").GetString());
        Assert.Equal("Maştağa", mine.GetProperty("categoryName").GetString());
        Assert.Equal("Qəsəbələr", mine.GetProperty("categoryLabel").GetString());
        Assert.False(mine.GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task Reference_numbers_are_unique_and_sequential_across_participants()
    {
        var references = new List<string>();
        foreach (var (email, place) in new[] { ("a@example.com", "mastaga"), ("b@example.com", "suvelan"), ("c@example.com", "emircan") })
        {
            var client = await SignedInAsync(email);
            var id = await ReadyDraftAsync(client, place);
            var body = await (await client.PostAsync($"/api/submissions/{id}/submit", null)).Content.ReadFromJsonAsync<JsonElement>();
            references.Add(body.GetProperty("referenceNumber").GetString()!);
        }
        Assert.Equal(3, references.Distinct().Count());
        Assert.Equal(references.OrderBy(r => r), references);
    }

    [Fact]
    public async Task Two_writers_on_the_same_submission_cannot_both_win()
    {
        var client = await SignedInAsync("race@example.com");
        var id = await ReadyDraftAsync(client);

        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var a = await dbA.Submissions.SingleAsync(s => s.Id == id);
        var b = await dbB.Submissions.SingleAsync(s => s.Id == id);

        a.Status = SubmissionStatus.Submitted; a.ReferenceNumber = "SUB-TEST-A"; a.ConcurrencyStamp = Guid.NewGuid();
        await dbA.SaveChangesAsync();
        b.Status = SubmissionStatus.Submitted; b.ReferenceNumber = "SUB-TEST-B"; b.ConcurrencyStamp = Guid.NewGuid();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    // ---- Deadline & limit -------------------------------------------------------------------

    [Fact]
    public async Task Deadline_is_enforced_on_the_server()
    {
        var client = await SignedInAsync("late@example.com");
        var id = await ReadyDraftAsync(client);
        await _factory.WithDbAsync(async db =>
        {
            var c = await db.Competitions.SingleAsync();
            c.EndDate = _factory.Clock.Now.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/submissions/{id}/submit", null)).StatusCode);
        var start = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_limit_participants_may_submit_several_works()
    {
        var client = await SignedInAsync("many@example.com");
        foreach (var place in new[] { "mastaga", "qala", "zire" })
        {
            var id = await ReadyDraftAsync(client, place);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/submissions/{id}/submit", null)).StatusCode);
        }
    }

    [Fact]
    public async Task A_configured_per_user_limit_is_enforced()
    {
        await _factory.WithDbAsync(async db =>
        {
            (await db.Competitions.SingleAsync()).MaxSubmissionsPerUser = 1;
            await db.SaveChangesAsync();
        });
        var client = await SignedInAsync("limit@example.com");
        var first = await ReadyDraftAsync(client, "mastaga");
        (await client.PostAsync($"/api/submissions/{first}/submit", null)).EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Admins_see_submitted_work_but_never_drafts()
    {
        var client = await SignedInAsync("mix@example.com");
        var submitted = await ReadyDraftAsync(client, "mastaga");
        (await client.PostAsync($"/api/submissions/{submitted}/submit", null)).EnsureSuccessStatusCode();
        await ReadyDraftAsync(client, "qala"); // stays a draft

        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        var competitionId = _factory.WithDb(db => db.Competitions.Single().Id);
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/competitions/{competitionId}/submissions");
        var only = Assert.Single(list.EnumerateArray());
        Assert.Equal(submitted, only.GetProperty("id").GetInt32());
    }
}
