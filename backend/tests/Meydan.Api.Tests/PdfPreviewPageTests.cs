using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The page that stands for a PDF (owner, 7 October 2026): automatic — page 2 of two or more pages, else page 1 — until
/// an admin chooses one, and then that page on every admin screen and print alike: the jury's package, the review of
/// all works, the public vote's two prints, the Final tab and its jury rows, the final show, the vote ranking and the
/// admin list. Only the choice is stored: the PDF is never changed, the full material keeps every page, and each
/// change is audited.
/// </summary>
public sealed class PdfPreviewPageTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

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

    private async Task<HttpClient> ParticipantAsync(string first)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = first, lastName = "Babayev", phone = TestPhones.Next(), email = $"p-{Guid.NewGuid():N}@example.com",
            password = "secret123", confirmPassword = "secret123", acceptTerms = true,
        });
        client.UseToken((await Json(res)).GetProperty("accessToken").GetString()!);
        return client;
    }

    /// <summary>A submitted work whose only file is <paramref name="bytes"/>.</summary>
    private async Task<(int Id, string Reference)> SubmitAsync(string settlement, string title, byte[] bytes, string contentType, string name)
    {
        var client = await ParticipantAsync("Orxan");
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement })))
            .GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers = new { title, concept = "Konsept", symbols = "Simvollar" } }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var reference = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null))).GetProperty("referenceNumber").GetString()!;
        return (id, reference);
    }

    private static async Task SetFieldPublicAsync(HttpClient admin, int competitionId, string key)
    {
        var f = (await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}/fields"))).EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == key);
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/fields/{f.GetProperty("id").GetInt32()}", new
        {
            key, label = f.GetProperty("label").GetString(), fieldType = f.GetProperty("fieldType").GetString(),
            isRequired = f.GetProperty("isRequired").GetBoolean(), isSubmissionTitle = f.GetProperty("isSubmissionTitle").GetBoolean(),
            isActive = true, isPublicAfterResults = true,
            optionsJson = f.GetProperty("optionsJson").ValueKind == JsonValueKind.Null ? null : f.GetProperty("optionsJson").GetString(),
        }));
    }

    private async Task WaitForDerivativesAsync(params int[] submissionIds)
    {
        for (var i = 0; i < 200; i++)
        {
            if (_factory.WithDb(db => db.SubmissionFiles.Count(f =>
                    submissionIds.Contains(f.SubmissionId) && f.PublicAssetStatus == PublicAssetStatus.Pending)) == 0)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("public derivatives were not produced");
    }

    private sealed record Setup(HttpClient Admin, int Competition, (int Id, string Reference) One, (int Id, string Reference) Four,
        (int Id, string Reference) Picture, int FourFile, int PictureFile);

    /// <summary>
    /// A one-page PDF, a four-page PDF and a picture in the chevron competition, its vote closed, and the three standing
    /// as frozen finalists — written straight in, as the freeze itself is FinalTests' subject; here only the pictures
    /// of the works matter.
    /// </summary>
    private async Task<Setup> SetupAsync()
    {
        var one = await SubmitAsync("balaxani", "Bir səhifə", TestImages.PdfWithPages(1), "application/pdf", "bir.pdf");
        var four = await SubmitAsync("ramana", "Dörd səhifə", TestImages.PdfWithPages(4), "application/pdf", "dord.pdf");
        var picture = await SubmitAsync("bileceri", "Şəkil", TestImages.Png(), "image/png", "sekil.png");
        var admin = await AdminAsync();
        var competition = (await Json(await admin.GetAsync("/api/admin/competitions"))).EnumerateArray()
            .First(c => c.GetProperty("slug").GetString() == Chevron).GetProperty("id").GetInt32();
        await SetFieldPublicAsync(admin, competition, "concept");
        await SetFieldPublicAsync(admin, competition, "files");
        await WaitForDerivativesAsync(one.Id, four.Id, picture.Id);

        var now = _factory.Clock.Now;
        var (fourFile, pictureFile) = await _factory.QueryDbAsync(async db =>
        {
            var c = await db.Competitions.FirstAsync(x => x.Id == competition);
            c.VotingStartsAt = now.AddDays(-3);
            c.VotingEndsAt = now.AddHours(-1);
            // Marked and locked, so the show opens too.
            var final = new CompetitionFinal
            {
                CompetitionId = competition, Stage = FinalStage.JuryLocked, FrozenAt = now, FrozenVotesFingerprint = "test",
                FirstPrizeAzn = 250, SecondPrizeAzn = 150, ThirdPrizeAzn = 100, JuryLockedAt = now,
            };
            var juror = new FinalJuror { Label = "Münsif 1", SortOrder = 1, CreatedAt = now };
            final.Jurors.Add(juror);
            var rank = 0;
            foreach (var id in new[] { four.Id, one.Id, picture.Id })
            {
                rank++;
                var finalist = new CompetitionFinalist
                {
                    CompetitionId = competition, SubmissionId = id, ValidVotes = 0, PublicRank = rank, PublicPoints = 11 - rank,
                    SnapshotAt = now,
                };
                finalist.Scores.Add(new JuryScore { Juror = juror, Idea = 10 - rank, Execution = 9, Relevance = 9, UpdatedAt = now });
                final.Finalists.Add(finalist);
            }
            db.CompetitionFinals.Add(final);
            await db.SaveChangesAsync();
            return (await db.SubmissionFiles.Where(f => f.SubmissionId == four.Id).Select(f => f.Id).SingleAsync(),
                await db.SubmissionFiles.Where(f => f.SubmissionId == picture.Id).Select(f => f.Id).SingleAsync());
        });
        return new Setup(admin, competition, one, four, picture, fourFile, pictureFile);
    }

    /// <summary>The page every admin screen and print shows for one work, by where it is shown.</summary>
    private static async Task<Dictionary<string, int>> PagesAsync(HttpClient admin, int competition, int submissionId, string reference)
    {
        static int PageOf(JsonElement image) => image.GetProperty("page").GetInt32();
        JsonElement ByReference(JsonElement items) =>
            items.EnumerateArray().Single(i => i.GetProperty("reference").GetString() == reference);
        JsonElement BySubmission(JsonElement items) =>
            items.EnumerateArray().Single(i => i.GetProperty("submissionId").GetInt32() == submissionId);
        JsonElement Row(JsonElement print) =>
            print.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("item").GetProperty("reference").GetString() == reference)
                .GetProperty("item");

        var pages = new Dictionary<string, int>
        {
            ["Münsif paketi"] = PageOf(ByReference((await Json(await admin.GetAsync(
                $"/api/admin/submissions/print?competitionId={competition}&finalists=true&includeAuthor=true"))).GetProperty("items")).GetProperty("image")),
            ["Bütün işləri çap et"] = PageOf(ByReference((await Json(await admin.GetAsync(
                $"/api/admin/submissions/print?competitionId={competition}&includeAuthor=true"))).GetProperty("items")).GetProperty("image")),
            ["Top 10 xalq nəticəsi"] = PageOf(Row(await Json(await admin.GetAsync(
                $"/api/admin/competitions/{competition}/final/print/top10"))).GetProperty("image")),
            ["Tam səs sıralaması"] = PageOf(Row(await Json(await admin.GetAsync(
                $"/api/admin/competitions/{competition}/final/print/full"))).GetProperty("image")),
            ["Final tab / münsif sətirləri"] = PageOf(BySubmission((await Json(await admin.GetAsync(
                $"/api/admin/competitions/{competition}/final"))).GetProperty("finalists")).GetProperty("image")),
            ["Final Show"] = PageOf(BySubmission((await Json(await admin.GetAsync(
                $"/api/admin/competitions/{competition}/final/show"))).GetProperty("works")).GetProperty("image")),
            ["Səs sıralaması"] = PageOf((await Json(await admin.GetAsync($"/api/admin/competitions/{competition}/voting")))
                .GetProperty("nominations").EnumerateArray().SelectMany(n => n.GetProperty("works").EnumerateArray())
                .Single(w => w.GetProperty("submissionId").GetInt32() == submissionId).GetProperty("image")),
            ["Admin siyahısı"] = (await Json(await admin.GetAsync($"/api/admin/submissions?competitionId={competition}&pageSize=100")))
                .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == submissionId)
                .GetProperty("preview").GetProperty("page").GetInt32(),
        };
        return pages;
    }

    private static void AllAre(int page, Dictionary<string, int> pages)
    {
        foreach (var (where, shown) in pages)
            Assert.True(shown == page, $"{where}: page {shown}, expected {page}");
    }

    private static Task<HttpResponseMessage> ChooseAsync(HttpClient admin, int submissionId, int fileId, int? page) =>
        admin.PutAsJsonAsync($"/api/admin/submissions/{submissionId}/files/{fileId}/preview-page", new { page });

    private string Sha(string storagePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_factory.StorageRoot, storagePath))));

    [Fact]
    public async Task The_chosen_page_stands_for_the_pdf_on_every_screen_and_print_and_automatic_comes_back()
    {
        var s = await SetupAsync();

        // Automatic: a one-page PDF by its only page, a four-page PDF by its second; the badge's count is the same.
        AllAre(1, await PagesAsync(s.Admin, s.Competition, s.One.Id, s.One.Reference));
        AllAre(2, await PagesAsync(s.Admin, s.Competition, s.Four.Id, s.Four.Reference));
        var file = (await Json(await s.Admin.GetAsync($"/api/admin/submissions/{s.Four.Id}"))).GetProperty("files")[0];
        Assert.Equal(4, file.GetProperty("pdfPages").GetInt32());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("previewPage").ValueKind);
        Assert.Equal(2, file.GetProperty("representativePage").GetInt32());
        Assert.Equal(2, file.GetProperty("automaticPage").GetInt32());

        var (path, size, assetKey) = _factory.WithDb(db => db.SubmissionFiles.Where(f => f.Id == s.FourFile)
            .Select(f => new { f.StoragePath, f.SizeBytes, f.PublicAssetKey }).AsEnumerable()
            .Select(f => (f.StoragePath, f.SizeBytes, f.PublicAssetKey)).Single());
        var original = Sha(path);

        // The admin picks page 1: every screen and print follows, with no other change.
        var chosen = await Json(await ChooseAsync(s.Admin, s.Four.Id, s.FourFile, 1));
        var after = chosen.GetProperty("files")[0];
        Assert.Equal(1, after.GetProperty("previewPage").GetInt32());
        Assert.Equal(1, after.GetProperty("representativePage").GetInt32());
        AllAre(1, await PagesAsync(s.Admin, s.Competition, s.Four.Id, s.Four.Reference));
        var image = (await Json(await s.Admin.GetAsync($"/api/admin/competitions/{s.Competition}/final/show")))
            .GetProperty("works").EnumerateArray().Single(w => w.GetProperty("submissionId").GetInt32() == s.Four.Id).GetProperty("image");
        Assert.Equal(4, image.GetProperty("pages").GetInt32());
        // Any page of the document may be chosen, the last too.
        await Json(await ChooseAsync(s.Admin, s.Four.Id, s.FourFile, 4));
        AllAre(4, await PagesAsync(s.Admin, s.Competition, s.Four.Id, s.Four.Reference));

        // The full material is untouched: every page, in order, whatever the choice.
        var material = (await Json(await s.Admin.GetAsync($"/api/admin/submissions/{s.Four.Id}/material"))).GetProperty("material")
            .EnumerateArray().Select(m => m.GetProperty("page").GetInt32()).ToList();
        Assert.Equal([1, 2, 3, 4], material);
        var appendix = (await Json(await s.Admin.GetAsync($"/api/admin/submissions/print?competitionId={s.Competition}&finalists=true")))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("reference").GetString() == s.Four.Reference)
            .GetProperty("material").GetArrayLength();
        Assert.Equal(4, appendix);

        // Back to automatic: page 2 again, everywhere.
        var reset = await Json(await ChooseAsync(s.Admin, s.Four.Id, s.FourFile, null));
        Assert.Equal(JsonValueKind.Null, reset.GetProperty("files")[0].GetProperty("previewPage").ValueKind);
        AllAre(2, await PagesAsync(s.Admin, s.Competition, s.Four.Id, s.Four.Reference));
        // The other works never moved.
        AllAre(1, await PagesAsync(s.Admin, s.Competition, s.One.Id, s.One.Reference));

        // The PDF itself is the same file, byte for byte, with its safe copy; only the choice was ever stored.
        Assert.Equal(original, Sha(path));
        _factory.WithDb(db =>
        {
            var f = db.SubmissionFiles.Single(x => x.Id == s.FourFile);
            Assert.Equal((path, size, assetKey), (f.StoragePath, f.SizeBytes, f.PublicAssetKey));
            return 0;
        });

        // Each change is in the audit log: from what, to what, of how many pages.
        var audit = _factory.WithDb(db => db.AuditLogs.Where(a => a.Action == AuditActions.PdfPreviewPageChanged)
            .OrderBy(a => a.Id).Select(a => a.Summary).ToList());
        Assert.Equal(
            [
                $"file {s.FourFile}: automatic (page 2) → page 1 of 4",
                $"file {s.FourFile}: page 1 → page 4 of 4",
                $"file {s.FourFile}: page 4 → automatic (page 2) of 4",
            ],
            audit);
    }

    [Fact]
    public async Task A_page_outside_the_pdf_or_a_file_that_is_not_a_pdf_is_refused_and_nothing_is_stored()
    {
        var s = await SetupAsync();

        foreach (var page in new[] { 0, -1, 5 })
            Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(s.Admin, s.Four.Id, s.FourFile, page)).StatusCode);
        // A picture has no pages to choose from.
        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(s.Admin, s.Picture.Id, s.PictureFile, 1)).StatusCode);
        // A file of another work is not this work's.
        Assert.Equal(HttpStatusCode.NotFound, (await ChooseAsync(s.Admin, s.One.Id, s.FourFile, 1)).StatusCode);
        // Admins only.
        var participant = await ParticipantAsync("Kamran");
        Assert.Equal(HttpStatusCode.Forbidden, (await ChooseAsync(participant, s.Four.Id, s.FourFile, 1)).StatusCode);

        Assert.Null(_factory.WithDb(db => db.SubmissionFiles.Single(f => f.Id == s.FourFile).PreviewPage));
        Assert.Equal(0, _factory.WithDb(db => db.AuditLogs.Count(a => a.Action == AuditActions.PdfPreviewPageChanged)));
        AllAre(2, await PagesAsync(s.Admin, s.Competition, s.Four.Id, s.Four.Reference));

        // The database keeps a chosen page at 1 or more even if a writer forgot to check.
        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.WithDbAsync(async db =>
        {
            (await db.SubmissionFiles.SingleAsync(f => f.Id == s.FourFile)).PreviewPage = 0;
            await db.SaveChangesAsync();
        }));
    }
}
