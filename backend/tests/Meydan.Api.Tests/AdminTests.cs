using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>Phase 3 administration: permissions, CRUD with guards, moderation, audit and the generic-platform proof.</summary>
public sealed class AdminTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private static readonly byte[] Png = TestImages.Png();

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private async Task<HttpClient> ParticipantAsync(string email)
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync(email));
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Errors(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string url, string fieldName, string fileName,
        byte[] content, string contentType, params (string Key, string Value)[] extra)
    {
        var form = new MultipartFormDataContent();
        foreach (var (k, v) in extra) form.Add(new StringContent(v), k);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, fieldName, fileName);
        return client.PostAsync(url, form);
    }

    /// <summary>A participant submits a complete chevron entry for the given settlement; returns its id.</summary>
    private static async Task<int> SubmitChevronAsync(HttpClient client, string settlement, string title = "Şevron")
    {
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title, concept = "Konsept", symbols = "Simvollar" },
        }));
        await Json(await UploadAsync(client, $"/api/submissions/{id}/files", "file", "eskiz.png", Png, "image/png", ("fieldKey", "files")));
        await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return id;
    }

    // ---- Permissions --------------------------------------------------------------------------

    public static TheoryData<string, string> AdminEndpoints => new()
    {
        { "GET", "/api/admin/dashboard" },
        { "GET", "/api/admin/projects" },
        { "POST", "/api/admin/projects" },
        { "GET", "/api/admin/competitions" },
        { "POST", "/api/admin/competitions" },
        { "GET", "/api/admin/competitions/1/categories" },
        { "POST", "/api/admin/competitions/1/categories/import" },
        { "GET", "/api/admin/competitions/1/fields" },
        { "PUT", "/api/admin/competitions/1/status" },
        { "GET", "/api/admin/submissions" },
        { "GET", "/api/admin/submissions/1" },
        { "PUT", "/api/admin/submissions/1/status" },
        { "POST", "/api/admin/submissions/1/notes" },
        { "GET", "/api/admin/participants" },
        { "GET", "/api/admin/audit" },
        { "GET", "/api/admin/upload-policy" },
        { "DELETE", "/api/admin/projects/1" },
        { "GET", "/api/admin/competitions/1/result" },
        { "POST", "/api/admin/competitions/1/result/winners" },
        { "PUT", "/api/admin/competitions/1/result/winners" },
        { "DELETE", "/api/admin/competitions/1/result/winners/1" },
        { "POST", "/api/admin/competitions/1/result/cover" },
        { "DELETE", "/api/admin/competitions/1/result/cover" },
        { "PUT", "/api/admin/competitions/1/result/announcement" },
        { "POST", "/api/admin/competitions/1/result/publish" },
        { "POST", "/api/admin/competitions/1/result/unpublish" },
        { "PUT", "/api/admin/submissions/1/preview" },
        { "GET", "/api/admin/submissions/1/files/1/public" },
        { "PUT", "/api/admin/projects/1/status" },
        { "PUT", "/api/admin/competitions/1/fields/1" },
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_api_is_denied_to_anonymous_users_and_participants(string method, string url)
    {
        var anonymous = _factory.CreateClient();
        var participant = await ParticipantAsync($"p-{Guid.NewGuid():N}@example.com");
        HttpRequestMessage Request() => new(new HttpMethod(method), url)
        {
            Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { }),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.SendAsync(Request())).StatusCode);
    }

    [Fact]
    public async Task Admins_are_allowed_and_see_the_existing_baku_data()
    {
        var admin = await AdminAsync();
        var projects = await Json(await admin.GetAsync("/api/admin/projects"));
        var baku = Assert.Single(projects.EnumerateArray());
        Assert.Equal("Bakı kəndləri", baku.GetProperty("name").GetString());
        Assert.Equal("Published", baku.GetProperty("status").GetString());
        Assert.False(baku.GetProperty("canDelete").GetBoolean());

        var competitions = await Json(await admin.GetAsync("/api/admin/competitions"));
        var chevron = Assert.Single(competitions.EnumerateArray());
        Assert.Equal(59, chevron.GetProperty("categoryCount").GetInt32());
        Assert.Equal(5, chevron.GetProperty("fieldCount").GetInt32());
        Assert.Equal("Open", chevron.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task The_refresh_cookie_alone_cannot_call_the_admin_api()
    {
        // Admin endpoints authenticate only with the bearer header. The HttpOnly refresh cookie is scoped to
        // /api/auth and SameSite=Strict, so a forged cross-site request carrying cookies gets nowhere.
        var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await browser.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/admin/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    // ---- Projects -----------------------------------------------------------------------------

    [Fact]
    public async Task Projects_are_created_as_drafts_published_on_purpose_and_audited()
    {
        var admin = await AdminAsync();
        var created = await Json(await admin.PostAsJsonAsync("/api/admin/projects", new
        {
            name = "Laçın", shortDescription = "Laçın üzrə yaradıcı müsabiqələr.", accentColor = "#8a3b2e",
        }));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("lacin", created.GetProperty("slug").GetString());
        Assert.Equal("Draft", created.GetProperty("status").GetString());
        Assert.True(created.GetProperty("canDelete").GetBoolean());

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/projects/lacin")).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/admin/projects/{id}/status", new { status = "Published" }));
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/projects/lacin")).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/admin/projects/{id}/status", new { status = "Archived" }));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/projects/lacin")).StatusCode);

        var audit = await Json(await admin.GetAsync($"/api/admin/audit?entityType=Project&entityId={id}"));
        var actions = audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Equal(["Archived", "Published", "Created"], actions);
        Assert.All(audit.EnumerateArray(), a => Assert.Equal("Admin MEYDAN", a.GetProperty("userName").GetString()));
    }

    [Fact]
    public async Task Project_validation_uses_field_errors()
    {
        var admin = await AdminAsync();
        var errors = await Errors(await admin.PostAsJsonAsync("/api/admin/projects", new { name = "", shortDescription = "", accentColor = "qırmızı" }));
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("shortDescription", out _));
        Assert.True(errors.TryGetProperty("accentColor", out _));
    }

    [Fact]
    public async Task Projects_with_content_cannot_be_deleted_empty_ones_can()
    {
        var admin = await AdminAsync();
        var bakuId = _factory.WithDb(db => db.Projects.Single().Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/projects/{bakuId}")).StatusCode);

        var empty = await Json(await admin.PostAsJsonAsync("/api/admin/projects", new { name = "Sınaq", shortDescription = "x" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/projects/{empty.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(1, _factory.WithDb(db => db.Projects.Count()));
    }

    [Fact]
    public async Task Covers_upload_through_file_storage_are_served_publicly_and_only_from_the_covers_area()
    {
        var admin = await AdminAsync();
        var id = _factory.WithDb(db => db.Projects.Single().Id);

        var svg = await UploadAsync(admin, $"/api/admin/projects/{id}/cover", "file", "logo.svg",
            "<svg><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml");
        Assert.True((await Errors(svg)).TryGetProperty("cover", out _));

        // A phone photo with GPS/camera EXIF: only a re-encoded, metadata-free WEBP is stored and served.
        var project = await Json(await UploadAsync(admin, $"/api/admin/projects/{id}/cover", "file", "cover.jpg",
            TestImages.JpegWithExif(), "image/jpeg"));
        var url = project.GetProperty("coverImage").GetString()!;
        Assert.StartsWith("/api/media/covers/", url);
        Assert.EndsWith(".webp", url);

        var anonymous = _factory.CreateClient();
        var image = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/webp", image.Content.Headers.ContentType?.MediaType);
        Assert.Contains("nosniff", image.Headers.GetValues("X-Content-Type-Options"));
        Assert.Empty(TestImages.LeakedMarkers(await image.Content.ReadAsByteArrayAsync()));
        Assert.Equal((await anonymous.GetFromJsonAsync<JsonElement>("/api/projects/baki-kendleri")).GetProperty("coverImage").GetString(), url);

        // Submission files and traversal attempts are never reachable here.
        var participant = await ParticipantAsync("media@example.com");
        await SubmitChevronAsync(participant, "mastaga");
        var submissionKey = _factory.WithDb(db => db.SubmissionFiles.Single().StoragePath);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/media/{submissionKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/media/covers/..%2F{submissionKey}")).StatusCode);

        var removed = await Json(await admin.DeleteAsync($"/api/admin/projects/{id}/cover"));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("coverImage").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
    }

    // ---- Competitions -------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_requires_an_active_form_and_categories_when_they_are_enabled()
    {
        var admin = await AdminAsync();
        var projectId = _factory.WithDb(db => db.Projects.Single().Id);
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Poster müsabiqəsi", shortDescription = "Test", hasCategories = true, categoryLabel = "Nominasiyalar",
        }));
        var id = c.GetProperty("id").GetInt32();
        Assert.Equal("Draft", c.GetProperty("status").GetString());

        var refused = await Errors(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));
        Assert.Equal(2, refused.GetProperty("status").GetArrayLength());

        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new { label = "İşin adı", fieldType = "ShortText", isRequired = true }));
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/categories", new { name = "Ən yaxşı poster" }));
        var published = await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));
        Assert.Equal("Published", published.GetProperty("status").GetString());
        Assert.Equal("Open", published.GetProperty("phase").GetString());

        var closed = await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Closed" }));
        Assert.Equal("Closed", closed.GetProperty("phase").GetString());
        Assert.False(closed.GetProperty("isAcceptingSubmissions").GetBoolean());
    }

    [Fact]
    public async Task Stored_status_and_computed_phase_stay_separate()
    {
        var admin = await AdminAsync();
        var competitionId = _factory.WithDb(db => db.Competitions.Single().Id);
        await _factory.WithDbAsync(async db =>
        {
            (await db.Competitions.SingleAsync()).EndDate = _factory.Clock.Now.AddDays(-1);
            await db.SaveChangesAsync();
        });
        var c = await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}"));
        Assert.Equal("Published", c.GetProperty("status").GetString());
        Assert.Equal("Closed", c.GetProperty("phase").GetString());

        var publicView = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/competitions/{Chevron}");
        Assert.Equal("Closed", publicView.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Draft_competitions_never_reach_the_public_and_nothing_is_invented()
    {
        var admin = await AdminAsync();
        var projectId = _factory.WithDb(db => db.Projects.Single().Id);
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new { projectId, title = "Gizli", shortDescription = "x" }));
        Assert.Equal(JsonValueKind.Null, c.GetProperty("endDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("prize").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("maxSubmissionsPerUser").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.CreateClient().GetAsync("/api/competitions/gizli")).StatusCode);
    }

    [Fact]
    public async Task Competitions_with_started_submissions_cannot_be_deleted()
    {
        var admin = await AdminAsync();
        var participant = await ParticipantAsync("starter@example.com");
        await Json(await participant.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" }));
        var id = _factory.WithDb(db => db.Competitions.Single().Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/competitions/{id}")).StatusCode);
    }

    // ---- Categories ---------------------------------------------------------------------------

    [Fact]
    public async Task Category_import_is_all_or_nothing_and_names_every_duplicate()
    {
        var admin = await AdminAsync();
        var id = _factory.WithDb(db => db.Competitions.Single().Id);

        var errors = await Errors(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/categories/import", new
        {
            items = new object[]
            {
                new { name = "Yeni qəsəbə" },
                new { name = "maştağa" },                         // exists, different case
                new { name = "Yeni qəsəbə" },                     // duplicate inside the list
                new { name = "Başqa", slug = "zig" },             // slug taken
                new { name = "  " },                              // empty
            },
        }));
        Assert.Equal(["items[1]", "items[2]", "items[3]", "items[4]"], errors.EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("Maştağa", errors.GetProperty("items[1]")[0].GetString());
        Assert.Equal(59, _factory.WithDb(db => db.CompetitionCategories.Count()));

        var imported = await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/categories/import", new
        {
            items = new object[] { new { name = "Nardaran bağları", description = "Test" }, new { name = "Şıx sahili", slug = "six-sahili" } },
        }));
        var last = imported.EnumerateArray().TakeLast(2).ToList();
        Assert.Equal(["nardaran-baglari", "six-sahili"], last.Select(c => c.GetProperty("slug").GetString()));
        Assert.Equal([60, 61], last.Select(c => c.GetProperty("sortOrder").GetInt32()));
    }

    [Fact]
    public async Task Categories_can_be_reordered_deactivated_and_only_unused_ones_deleted()
    {
        var admin = await AdminAsync();
        var id = _factory.WithDb(db => db.Competitions.Single().Id);
        var participant = await ParticipantAsync("cat@example.com");
        await SubmitChevronAsync(participant, "mastaga");

        var list = await Json(await admin.GetAsync($"/api/admin/competitions/{id}/categories"));
        var ids = list.EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToList();
        ids.Reverse();
        var reordered = await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/categories/order", new { ids }));
        Assert.Equal("Zığ", reordered[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/categories/order", new { ids = ids.Take(3) })).StatusCode);

        var mastaga = list.EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "mastaga");
        Assert.Equal(1, mastaga.GetProperty("submissionCount").GetInt32());
        var mastagaId = mastaga.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/competitions/{id}/categories/{mastagaId}")).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/categories/{mastagaId}", new { name = "Maştağa", isActive = false }));
        var publicView = await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/competitions/{Chevron}");
        Assert.DoesNotContain(publicView.GetProperty("categories").EnumerateArray(), c => c.GetProperty("slug").GetString() == "mastaga");
        Assert.Equal(1, _factory.WithDb(db => db.Submissions.Count(s => s.Category!.Slug == "mastaga"))); // history kept

        var zig = list.EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "zig").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/competitions/{id}/categories/{zig}")).StatusCode);

        var duplicate = await Errors(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/categories/{mastagaId}", new { name = "QALA" }));
        Assert.True(duplicate.TryGetProperty("name", out _));
    }

    // ---- Form builder -------------------------------------------------------------------------

    [Fact]
    public async Task Upload_settings_can_only_tighten_the_global_policy()
    {
        var admin = await AdminAsync();
        var id = _factory.WithDb(db => db.Competitions.Single().Id);
        var policy = await Json(await admin.GetAsync("/api/admin/upload-policy"));
        Assert.Equal(10, policy.GetProperty("maxFileSizeMb").GetInt32());
        Assert.DoesNotContain(policy.GetProperty("allowedTypes").EnumerateArray(), t => t.GetProperty("key").GetString() == "svg");

        var tooBig = await Errors(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            label = "Əlavə fayllar", fieldType = "File", optionsJson = """{"maxFileSizeMb":20,"maxFiles":9,"accept":["svg"]}""",
        }));
        Assert.Equal(3, tooBig.GetProperty("optionsJson").GetArrayLength());

        var tight = await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            label = "Sənəd", fieldType = "File", optionsJson = """{"maxFileSizeMb":2,"maxFiles":1,"accept":["pdf"]}""",
        }));
        Assert.Equal("sened", tight.GetProperty("key").GetString());

        var participant = await ParticipantAsync("tight@example.com");
        var draft = await Json(await participant.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" }));
        var rules = draft.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "sened").GetProperty("files");
        Assert.Equal(2 * 1024 * 1024, rules.GetProperty("maxFileSizeBytes").GetInt64());
        var png = await UploadAsync(participant, $"/api/submissions/{draft.GetProperty("id").GetInt32()}/files", "file", "a.png",
            Png, "image/png", ("fieldKey", "sened"));
        Assert.Equal(HttpStatusCode.BadRequest, png.StatusCode);
    }

    [Fact]
    public async Task Changing_the_form_never_rewrites_submitted_answers()
    {
        var admin = await AdminAsync();
        var competitionId = _factory.WithDb(db => db.Competitions.Single().Id);
        var participant = await ParticipantAsync("history@example.com");
        var submissionId = await SubmitChevronAsync(participant, "mastaga", "Tarixi iş");

        var fields = await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}/fields"));
        var concept = fields.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "concept");
        var conceptId = concept.GetProperty("id").GetInt32();
        Assert.True(concept.GetProperty("isLocked").GetBoolean());

        // Type and key are locked; deleting is refused; renaming and disabling are allowed.
        var typeChange = await Errors(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/fields/{conceptId}",
            new { label = "Konsept", fieldType = "Number" }));
        Assert.True(typeChange.TryGetProperty("fieldType", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/competitions/{competitionId}/fields/{conceptId}")).StatusCode);
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/fields/{conceptId}",
            new { label = "Yeni ad", fieldType = "LongText", isActive = false }));

        // New drafts no longer see the disabled field…
        var other = await ParticipantAsync("newcomer@example.com");
        var draft = await Json(await other.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" }));
        Assert.DoesNotContain(draft.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "concept");

        // …while the submitted work keeps it, under the question it was answered to.
        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{submissionId}"));
        var answer = detail.GetProperty("answers").EnumerateArray().Single(a => a.GetProperty("key").GetString() == "concept");
        Assert.Equal("Konseptin izahı", answer.GetProperty("label").GetString());
        Assert.Equal("Yeni ad", answer.GetProperty("currentLabel").GetString());
        Assert.Equal("Konsept", answer.GetProperty("value").GetString());
        Assert.False(answer.GetProperty("fieldActive").GetBoolean());

        var own = await Json(await participant.GetAsync($"/api/submissions/{submissionId}"));
        Assert.Contains(own.GetProperty("fields").EnumerateArray(), f => f.GetProperty("label").GetString() == "Konseptin izahı");
    }

    [Fact]
    public async Task Fields_can_be_reordered_and_unused_ones_deleted()
    {
        var admin = await AdminAsync();
        var id = _factory.WithDb(db => db.Competitions.Single().Id);
        var extra = await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields",
            new { label = "Müəllifin şəhəri", fieldType = "ShortText" }));
        Assert.Equal(6, extra.GetProperty("sortOrder").GetInt32());

        var ids = (await Json(await admin.GetAsync($"/api/admin/competitions/{id}/fields"))).EnumerateArray()
            .Select(f => f.GetProperty("id").GetInt32()).ToList();
        ids.Insert(0, ids[^1]);
        ids.RemoveAt(ids.Count - 1);
        var reordered = await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/fields/order", new { ids }));
        Assert.Equal("muellifin_seheri", reordered[0].GetProperty("key").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/admin/competitions/{id}/fields/{extra.GetProperty("id").GetInt32()}")).StatusCode);
    }

    // ---- Submissions --------------------------------------------------------------------------

    [Fact]
    public async Task Submissions_filter_by_category_status_date_and_search()
    {
        var admin = await AdminAsync();
        var a = await ParticipantAsync("nigar@example.com");
        var b = await ParticipantAsync("orxan@example.com");
        var mastaga = await SubmitChevronAsync(a, "mastaga", "Maştağa nar");
        var qala = await SubmitChevronAsync(b, "qala", "Qala qülləsi");
        await Json(await a.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "zire" })); // draft — never listed

        var all = await Json(await admin.GetAsync("/api/admin/submissions"));
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        var categoryId = _factory.WithDb(db => db.CompetitionCategories.Single(c => c.Slug == "mastaga").Id);
        var byCategory = await Json(await admin.GetAsync($"/api/admin/submissions?categoryId={categoryId}"));
        Assert.Equal(mastaga, Assert.Single(byCategory.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());

        var reference = _factory.WithDb(db => db.Submissions.Single(s => s.Id == qala).ReferenceNumber!);
        foreach (var q in new[] { reference, "orxan@", "QALA" })
        {
            var found = await Json(await admin.GetAsync($"/api/admin/submissions?q={Uri.EscapeDataString(q)}"));
            var hits = found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
            Assert.True(hits.Count == 1, $"q={q}: {string.Join(", ", hits)}");
            Assert.Equal(qala, found.GetProperty("items")[0].GetProperty("id").GetInt32());
        }

        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{qala}/status", new { status = "UnderReview" }));
        var reviewing = await Json(await admin.GetAsync("/api/admin/submissions?status=UnderReview"));
        Assert.Equal(qala, Assert.Single(reviewing.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());

        var today = _factory.Clock.Now.ToOffset(TimeSpan.FromHours(4)).Date;
        var from = Uri.EscapeDataString(new DateTimeOffset(today, TimeSpan.FromHours(4)).ToString("O"));
        var tomorrow = Uri.EscapeDataString(new DateTimeOffset(today.AddDays(1), TimeSpan.FromHours(4)).ToString("O"));
        Assert.Equal(2, (await Json(await admin.GetAsync($"/api/admin/submissions?from={from}"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await admin.GetAsync($"/api/admin/submissions?from={tomorrow}"))).GetProperty("total").GetInt32());

        var page = await Json(await admin.GetAsync("/api/admin/submissions?pageSize=1&page=2"));
        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Status_changes_are_recorded_with_who_and_when_and_notes_stay_internal()
    {
        var admin = await AdminAsync();
        var participant = await ParticipantAsync("moderated@example.com");
        var id = await SubmitChevronAsync(participant, "mastaga");

        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{id}"));
        Assert.Equal("Submitted", detail.GetProperty("status").GetString());
        Assert.Equal(["UnderReview", "Approved", "Rejected"], detail.GetProperty("allowedStatuses").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("moderated@example.com", detail.GetProperty("participant").GetProperty("email").GetString());
        Assert.Equal("Maştağa", detail.GetProperty("category").GetProperty("name").GetString());
        Assert.Single(detail.GetProperty("files").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status", new { status = "Winner" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status", new { status = "Draft" })).StatusCode);

        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status", new { status = "UnderReview" }));
        var approved = await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status",
            new { status = "Approved", comment = "Tələblərə uyğundur" }));
        var history = approved.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Submitted", "UnderReview", "Approved"], history.Select(h => h.GetProperty("to").GetString()));
        Assert.Equal("Aysel Məmmədova", history[0].GetProperty("changedBy").GetString()); // the participant's own submit
        Assert.Equal("Admin MEYDAN", history[2].GetProperty("changedBy").GetString());
        Assert.Equal("Tələblərə uyğundur", history[2].GetProperty("comment").GetString());

        var withNote = await Json(await admin.PostAsJsonAsync($"/api/admin/submissions/{id}/notes", new { text = "PDF keyfiyyəti yoxlanmalıdır." }));
        Assert.Equal("PDF keyfiyyəti yoxlanmalıdır.", withNote.GetProperty("notes")[0].GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/submissions/{id}/notes", new { text = " " })).StatusCode);

        // The participant sees the new status, never the note.
        var own = await participant.GetAsync($"/api/submissions/{id}");
        var body = await own.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Approved\"", body);
        Assert.DoesNotContain("keyfiyyəti", body);
        Assert.DoesNotContain("Admin MEYDAN", body); // no note author or moderation history either
        var mine = await participant.GetStringAsync("/api/me/submissions");
        Assert.DoesNotContain("keyfiyyəti", mine);

        var audit = await Json(await admin.GetAsync($"/api/admin/audit?entityType=Submission&entityId={id}"));
        var entries = audit.EnumerateArray().ToList();
        Assert.Equal(["NoteAdded", "StatusChanged", "StatusChanged"], entries.Select(a => a.GetProperty("action").GetString()));
        Assert.Equal("UnderReview → Approved", entries[1].GetProperty("summary").GetString());
        Assert.All(entries, a => Assert.DoesNotContain("keyfiyyəti", a.GetProperty("summary").GetString() ?? ""));
    }

    [Fact]
    public async Task Admin_file_access_is_controlled_and_drafts_stay_private()
    {
        var admin = await AdminAsync();
        var owner = await ParticipantAsync("owner2@example.com");
        var submitted = await SubmitChevronAsync(owner, "mastaga");
        var fileId = _factory.WithDb(db => db.SubmissionFiles.Single(f => f.SubmissionId == submitted).Id);

        var file = await admin.GetAsync($"/api/submissions/{submitted}/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Contains("sandbox", file.Headers.GetValues("Content-Security-Policy").Single());

        var intruder = await ParticipantAsync("intruder2@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/submissions/{submitted}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await intruder.GetAsync($"/api/admin/submissions/{submitted}")).StatusCode);

        // A draft: private even to admins.
        var draft = await Json(await owner.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" }));
        var draftId = draft.GetProperty("id").GetInt32();
        var draftFile = await Json(await UploadAsync(owner, $"/api/submissions/{draftId}/files", "file", "d.png", Png, "image/png", ("fieldKey", "files")));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/submissions/{draftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/submissions/{draftId}/files/{draftFile.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/submissions/{draftId}/status", new { status = "Approved" })).StatusCode);
    }

    [Fact]
    public async Task Dashboard_counts_reflect_the_platform()
    {
        var admin = await AdminAsync();
        var participant = await ParticipantAsync("dash@example.com");
        var submitted = await SubmitChevronAsync(participant, "mastaga");
        await Json(await participant.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "qala" }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{submitted}/status", new { status = "UnderReview" }));

        var d = await Json(await admin.GetAsync("/api/admin/dashboard"));
        Assert.Equal(1, d.GetProperty("publishedProjects").GetInt32());
        Assert.Equal(1, d.GetProperty("openCompetitions").GetInt32());
        Assert.Equal(1, d.GetProperty("totalSubmissions").GetInt32());
        Assert.Equal(1, d.GetProperty("draftSubmissions").GetInt32());
        Assert.Equal(0, d.GetProperty("submittedSubmissions").GetInt32());
        Assert.Equal(1, d.GetProperty("underReviewSubmissions").GetInt32());
        Assert.Single(d.GetProperty("recentSubmissions").EnumerateArray());

        var participants = await Json(await admin.GetAsync("/api/admin/participants?q=dash@"));
        var p = Assert.Single(participants.GetProperty("items").EnumerateArray());
        Assert.Equal(1, p.GetProperty("submissionCount").GetInt32());
        Assert.False(p.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Dashboard_narrows_to_one_competition_and_lists_the_ones_to_choose_from()
    {
        var admin = await AdminAsync();
        var participant = await ParticipantAsync("dash-one@example.com");
        var submitted = await SubmitChevronAsync(participant, "mastaga");
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{submitted}/status", new { status = "Approved" }));
        // A second competition of the same project, archived and still empty.
        var (chevronId, otherId) = await _factory.QueryDbAsync(async db =>
        {
            var chevron = await db.Competitions.SingleAsync(c => c.Slug == Chevron);
            var other = new Meydan.Domain.Entities.Competition
            {
                ProjectId = chevron.ProjectId, Title = "Başqa müsabiqə", Slug = "basqa-musabiqe", ShortDescription = "x",
                Status = CompetitionStatus.Archived, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Competitions.Add(other);
            await db.SaveChangesAsync();
            return (chevron.Id, other.Id);
        });

        var one = await Json(await admin.GetAsync($"/api/admin/dashboard?competitionId={chevronId}"));
        Assert.Equal(chevronId, one.GetProperty("competitionId").GetInt32());
        Assert.Equal(1, one.GetProperty("totalSubmissions").GetInt32());
        Assert.Equal(1, one.GetProperty("approvedSubmissions").GetInt32());
        Assert.Equal(0, one.GetProperty("rejectedSubmissions").GetInt32());
        Assert.Single(one.GetProperty("recentSubmissions").EnumerateArray());

        var empty = await Json(await admin.GetAsync($"/api/admin/dashboard?competitionId={otherId}"));
        Assert.Equal(0, empty.GetProperty("totalSubmissions").GetInt32());
        Assert.Empty(empty.GetProperty("recentSubmissions").EnumerateArray());
        // The platform's own counts stay the platform's.
        Assert.Equal(1, empty.GetProperty("openCompetitions").GetInt32());

        // The choice: the open competition first, then the rest.
        var choices = one.GetProperty("competitions").EnumerateArray().ToList();
        Assert.Equal(chevronId, choices[0].GetProperty("id").GetInt32());
        Assert.True(choices[0].GetProperty("isOpen").GetBoolean());
        Assert.Contains(choices, c => c.GetProperty("id").GetInt32() == otherId && !c.GetProperty("isOpen").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/dashboard?competitionId=999999")).StatusCode);
    }

    // ---- Generic platform proof ---------------------------------------------------------------

    /// <summary>
    /// Phase 3 "done" scenario through the API the admin UI uses: a brand-new project and competition with
    /// its own categories and form, published, used by a participant, and moderated — with no code, SQL or
    /// seed change. Runs only in this throwaway test database.
    /// </summary>
    [Fact]
    public async Task A_new_project_and_competition_are_built_and_used_without_any_code_change()
    {
        var admin = await AdminAsync();
        var project = await Json(await admin.PostAsJsonAsync("/api/admin/projects", new
        {
            name = "Laçın", shortDescription = "Laçın haqqında yaradıcı müsabiqələr.", accentColor = "#7A3E2B", sortOrder = 2,
        }));
        var projectId = project.GetProperty("id").GetInt32();

        var competition = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Laçın foto müsabiqəsi", shortDescription = "Laçını obyektivlə göstərin.",
            hasCategories = true, categoryLabel = "Janrlar",
        }));
        var competitionId = competition.GetProperty("id").GetInt32();
        Assert.Equal("lacin-foto-musabiqesi", competition.GetProperty("slug").GetString());

        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/categories/import", new
        {
            items = new object[] { new { name = "Mənzərə" }, new { name = "Portret" }, new { name = "Memarlıq" } },
        }));
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields",
            new { label = "Fotonun adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true }));
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields",
            new { label = "Çəkiliş yeri", fieldType = "LongText", isRequired = true, helpText = "Harada çəkilib?" }));
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields",
            new { label = "Foto", fieldType = "File", isRequired = true, optionsJson = """{"maxFiles":1,"accept":["jpeg","png"]}""" }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/status", new { status = "Published" }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/projects/{projectId}/status", new { status = "Published" }));

        // Public side.
        var anonymous = _factory.CreateClient();
        var home = await anonymous.GetFromJsonAsync<JsonElement>("/api/home");
        Assert.Contains(home.GetProperty("projects").EnumerateArray(), p => p.GetProperty("slug").GetString() == "lacin");
        var detail = await anonymous.GetFromJsonAsync<JsonElement>("/api/competitions/lacin-foto-musabiqesi");
        Assert.Equal("Janrlar", detail.GetProperty("categoryLabel").GetString());
        Assert.Equal(["Mənzərə", "Portret", "Memarlıq"], detail.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        var participant = await ParticipantAsync("fotoqraf@example.com");
        var draft = await Json(await participant.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = "lacin-foto-musabiqesi", categorySlug = "portret" }));
        var id = draft.GetProperty("id").GetInt32();
        var keys = draft.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Equal(["fotonun_adi", "cekilis_yeri", "foto"], keys);
        await Json(await participant.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new Dictionary<string, string> { ["fotonun_adi"] = "Qarabağ atları", ["cekilis_yeri"] = "Laçın rayonu" },
        }));
        await Json(await UploadAsync(participant, $"/api/submissions/{id}/files", "file", "foto.png", Png, "image/png", ("fieldKey", "foto")));
        var submitted = await Json(await participant.PostAsync($"/api/submissions/{id}/submit", null));
        Assert.StartsWith("SUB-", submitted.GetProperty("referenceNumber").GetString());

        // Back in admin.
        var list = await Json(await admin.GetAsync($"/api/admin/submissions?projectId={projectId}"));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("Qarabağ atları", row.GetProperty("title").GetString());
        Assert.Equal("Portret", row.GetProperty("category").GetProperty("name").GetString());

        var moderated = await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status", new { status = "Approved" }));
        Assert.Equal("Approved", moderated.GetProperty("status").GetString());
        Assert.Equal("Laçın rayonu", moderated.GetProperty("answers").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "cekilis_yeri").GetProperty("value").GetString());
    }
}
