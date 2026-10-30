using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The print view's data: the works of a selection as management reviews them on paper. Admins only, and never a
/// contact or registration detail; the author's name only when the admin asks for it.
/// </summary>
public sealed class AdminPrintTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Work(int Id, string Reference, HttpClient Owner, string Email);

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

    private async Task<(HttpClient Client, string Email)> ParticipantAsync(string first, string last)
    {
        var client = _factory.CreateClient();
        var email = $"p-{Guid.NewGuid():N}@example.com";
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = last, phone = TestPhones.Next(), email,
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await Json(res)).GetProperty("accessToken").GetString()!);
        return (client, email);
    }

    private async Task<Work> SubmitAsync(string settlement, string title, string concept, string first, string last, bool submit = true)
    {
        var (client, email) = await ParticipantAsync(first, last);
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title, concept, symbols = "Gizli simvollar", notes = "Şəxsi qeyd: ünvanım Bakı" },
        }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", $"{first}_{last}.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        if (!submit) return new Work(id, "", client, email);
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return new Work(id, submitted.GetProperty("referenceNumber").GetString()!, client, email);
    }

    private async Task<int> ChevronIdAsync(HttpClient admin) =>
        (await Json(await admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
        .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();

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

    private static Task<HttpResponseMessage> PrintAsync(HttpClient client, string query) =>
        client.GetAsync($"/api/admin/submissions/print{query}");

    private static JsonElement Item(JsonElement print, string reference) =>
        print.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("reference").GetString() == reference);

    [Fact]
    public async Task Only_an_admin_may_read_it()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PrintAsync(anonymous, "")).StatusCode);

        var (participant, _) = await ParticipantAsync("Elçin", "Nəbiyev");
        Assert.Equal(HttpStatusCode.Forbidden, (await PrintAsync(participant, "")).StatusCode);
    }

    [Fact]
    public async Task A_page_carries_the_work_and_never_who_made_it_unless_asked()
    {
        var b = await SubmitAsync("balaxani", "Balaxanı şevronu", "Konsept: dalğa və neft", "Leyla", "Quliyeva");
        var r = await SubmitAsync("ramana", "Ramana qalası", "Konsept: qala divarı", "Nigar", "Həsənova");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await SetFieldPublicAsync(admin, competition, "concept", true);
        await SetFieldPublicAsync(admin, competition, "files", true);
        await WaitForDerivativesAsync(b.Id, r.Id);

        var res = await PrintAsync(admin, $"?competitionId={competition}");
        var body = await res.Content.ReadAsStringAsync();
        var print = await Json(res);

        Assert.Equal(2, print.GetProperty("total").GetInt32());
        Assert.False(print.GetProperty("authorIncluded").GetBoolean());
        Assert.Contains("şevron", print.GetProperty("competitionTitle").GetString());
        // By reference number, the order the pages are discussed in.
        var references = print.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()).ToList();
        Assert.Equal(references.OrderBy(x => x, StringComparer.Ordinal).ToList(), references);

        var page = Item(print, b.Reference);
        Assert.Equal("Balaxanı şevronu", page.GetProperty("title").GetString());
        Assert.Equal("Konsept: dalğa və neft", page.GetProperty("concept").GetString());
        Assert.Equal("Konseptin izahı", page.GetProperty("conceptLabel").GetString());
        Assert.Equal("Balaxanı", page.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("author").ValueKind);
        var image = page.GetProperty("image");
        Assert.True(image.GetProperty("large").GetBoolean());
        // The picture is the work's own file, and the admin endpoint serves its metadata-free copy.
        var fileId = image.GetProperty("fileId").GetInt32();
        var served = await admin.GetAsync($"/api/admin/submissions/{b.Id}/files/{fileId}/public");
        Assert.Equal("image/webp", served.Content.Headers.ContentType!.MediaType);

        // Nothing private: no contact or registration data, no private answers, no names.
        foreach (var secret in new[] { b.Email, r.Email, "@example.com", "+994", "Leyla", "Quliyeva", "Nigar", "Həsənova",
                     "Gizli simvollar", "Şəxsi qeyd", "email", "phone", "registeredAt", "userId" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);

        // The name only on request — and still nothing else about the person.
        var named = await PrintAsync(admin, $"?competitionId={competition}&includeAuthor=true");
        var namedBody = await named.Content.ReadAsStringAsync();
        var withAuthor = await Json(named);
        Assert.True(withAuthor.GetProperty("authorIncluded").GetBoolean());
        Assert.Equal("Leyla Quliyeva", Item(withAuthor, b.Reference).GetProperty("author").GetString());
        foreach (var secret in new[] { b.Email, "@example.com", "+994", "Gizli simvollar", "Şəxsi qeyd", "email", "phone" })
            Assert.DoesNotContain(secret, namedBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A submitted work whose only file is a PDF of <paramref name="pages"/> pages.</summary>
    private async Task<(int Id, string Reference, HttpClient Owner)> SubmitPdfAsync(string settlement, string title, int pages, string first)
    {
        var (client, _) = await ParticipantAsync(first, "Babayev");
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title, concept = "PDF", symbols = "Simvollar" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var pdf = new ByteArrayContent(TestImages.PdfWithPages(pages));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "brendbuk.pdf");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;
        return (id, reference, client);
    }

    [Fact]
    public async Task A_pdf_card_shows_its_content_page_and_the_whole_pdf_stays_listed_for_admins_only()
    {
        // PDFs of one, two and four pages, and a work with a picture.
        var one = await SubmitPdfAsync("balaxani", "Bir səhifə", 1, "Kamran");
        var two = await SubmitPdfAsync("bileceri", "İki səhifə", 2, "Tural");
        var four = await SubmitPdfAsync("ramana", "Dörd səhifə", 4, "Orxan");
        var picture = await SubmitAsync("ramana", "Ramana qalası", "Konsept", "Nigar", "Həsənova");

        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await SetFieldPublicAsync(admin, competition, "concept", true);
        await SetFieldPublicAsync(admin, competition, "files", true);
        await WaitForDerivativesAsync(one.Id, two.Id, four.Id, picture.Id);

        var print = await Json(await PrintAsync(admin, $"?competitionId={competition}&includeAuthor=true"));
        (int Page, int PdfPages, int Material) Card(string reference)
        {
            var item = Item(print, reference);
            return (item.GetProperty("image").GetProperty("page").GetInt32(), item.GetProperty("pdfPages").GetInt32(),
                item.GetProperty("material").GetArrayLength());
        }
        // A one-page PDF shows its only page; two or more pages, the second — the badge still counts every page.
        Assert.Equal((1, 1, 1), Card(one.Reference));
        Assert.Equal((2, 2, 2), Card(two.Reference));
        Assert.Equal((2, 4, 4), Card(four.Reference));
        Assert.Equal((1, 0, 1), Card(picture.Reference));
        Assert.Equal("Orxan Babayev", Item(print, four.Reference).GetProperty("author").GetString());
        var material = Item(print, four.Reference).GetProperty("material").EnumerateArray()
            .Select(m => (m.GetProperty("kind").GetString()!, m.GetProperty("page").GetInt32(), m.GetProperty("pages").GetInt32()))
            .ToList();
        Assert.Equal(new[] { ("pdf", 1, 4), ("pdf", 2, 4), ("pdf", 3, 4), ("pdf", 4, 4) }, material);

        // The card's page 2 and every later page come from the admin's own endpoint as metadata-free images; never the
        // PDF, never past the end, never to anybody but an admin.
        var fileId = Item(print, four.Reference).GetProperty("image").GetProperty("fileId").GetInt32();
        var page2 = await admin.GetAsync($"/api/admin/submissions/{four.Id}/files/{fileId}/pages/2");
        Assert.Equal(HttpStatusCode.OK, page2.StatusCode);
        Assert.Equal("image/webp", page2.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/submissions/{four.Id}/files/{fileId}/pages/4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/submissions/{four.Id}/files/{fileId}/pages/5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/submissions/{four.Id}/files/{fileId}/pages/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await four.Owner.GetAsync($"/api/admin/submissions/{four.Id}/files/{fileId}/pages/2")).StatusCode);

        // Nothing private travels with it.
        var body = print.GetRawText();
        foreach (var secret in new[] { "@example.com", "+994", "Gizli simvollar", "Şəxsi qeyd", "storagePath", "brendbuk.pdf" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_public_material_is_printed_and_only_works_that_can_still_win()
    {
        var shown = await SubmitAsync("balaxani", "Göstərilən iş", "Açıq konsept", "Səbinə", "Rzayeva");
        var rejected = await SubmitAsync("ramana", "Rədd olunan iş", "Rədd konsepti", "Kamran", "Babayev");
        var draft = await SubmitAsync("nardaran", "Qaralama iş", "Qaralama konsepti", "Rəşad", "İsmayılov", submit: false);
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{rejected.Id}/status", new { status = "Rejected" }));

        // Private fields stay private on paper too: no concept, no picture.
        var before = Item(await Json(await PrintAsync(admin, $"?competitionId={competition}")), shown.Reference);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("concept").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("image").ValueKind);

        await SetFieldPublicAsync(admin, competition, "concept", true);
        await SetFieldPublicAsync(admin, competition, "files", true);
        await WaitForDerivativesAsync(shown.Id, rejected.Id);

        // Without a status filter: the works that can still win; never a rejected one, never a draft.
        var print = await Json(await PrintAsync(admin, $"?competitionId={competition}"));
        var refs = print.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()).ToList();
        Assert.Equal([shown.Reference], refs);
        Assert.DoesNotContain("Qaralama", print.ToString());
        _ = draft;

        // Asked for explicitly, the rejected ones are printed.
        var rejectedOnly = await Json(await PrintAsync(admin, $"?competitionId={competition}&status=Rejected"));
        Assert.Equal(rejected.Reference, Assert.Single(rejectedOnly.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());

        // A file taken off public pages is not printed either.
        var fileId = Item(print, shown.Reference).GetProperty("image").GetProperty("fileId").GetInt32();
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{shown.Id}/files/{fileId}/visibility", new { hidden = true }));
        var hidden = Item(await Json(await PrintAsync(admin, $"?competitionId={competition}")), shown.Reference);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("image").ValueKind);
    }

    [Fact]
    public async Task A_public_choice_such_as_the_nomination_is_printed_and_a_private_one_is_not()
    {
        var work = await SubmitAsync("balaxani", "Nominasiyalı iş", "Konsept", "Aysel", "Məmmədova");
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await _factory.WithDbAsync(async db =>
        {
            var nomination = new CompetitionField
            {
                CompetitionId = competition, Key = "nomination", Label = "Nominasiya", FieldType = CompetitionFieldType.Select,
                OptionsJson = "[\"Simvol\",\"Promo\"]", IsPublicAfterResults = true, SortOrder = 0, IsActive = true,
            };
            var secret = new CompetitionField
            {
                CompetitionId = competition, Key = "region", Label = "Yaşadığı rayon", FieldType = CompetitionFieldType.Select,
                OptionsJson = "[\"Bakı\",\"Sumqayıt\"]", IsPublicAfterResults = false, SortOrder = 0, IsActive = true,
            };
            db.CompetitionFields.AddRange(nomination, secret);
            await db.SaveChangesAsync();
            db.SubmissionAnswers.AddRange(
                new SubmissionAnswer { SubmissionId = work.Id, CompetitionFieldId = nomination.Id, Value = "Simvol", FieldLabel = "Nominasiya" },
                new SubmissionAnswer { SubmissionId = work.Id, CompetitionFieldId = secret.Id, Value = "Sumqayıt", FieldLabel = "Yaşadığı rayon" });
            await db.SaveChangesAsync();
        });

        var res = await PrintAsync(admin, $"?competitionId={competition}");
        var body = await res.Content.ReadAsStringAsync();
        var page = Item(await Json(res), work.Reference);
        var fact = Assert.Single(page.GetProperty("facts").EnumerateArray());
        Assert.Equal("Nominasiya", fact.GetProperty("label").GetString());
        Assert.Equal("Simvol", fact.GetProperty("value").GetString());
        Assert.DoesNotContain("Sumqayıt", body);
        Assert.DoesNotContain("Yaşadığı rayon", body);
    }
}
