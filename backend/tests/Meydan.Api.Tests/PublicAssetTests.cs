using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// Phase 5: the public gallery only ever serves server-made derivatives — re-encoded images and PDF page previews
/// without any metadata — while the participant's original stays private and byte-for-byte unchanged.
/// </summary>
public sealed class PublicAssetTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---- helpers ----------------------------------------------------------------------------------

    private sealed record Upload(int FileId, string Name, byte[] Bytes);
    private sealed record Entry(int Id, string Reference, List<Upload> Files, HttpClient Owner);

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> Body(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"expected {(int)expected}, got {(int)res.StatusCode}: {body}");
        return body;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, string name, byte[] bytes, string type)
    {
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return client.PostAsync($"/api/submissions/{id}/files", form);
    }

    private async Task<Entry> SubmitAsync(string settlement, params (string Name, byte[] Bytes, string Type)[] files)
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { title = $"İş {settlement}", concept = "Konsept", symbols = "Simvollar", notes = "Şəxsi qeyd" },
        }));
        var uploads = new List<Upload>();
        foreach (var (name, bytes, type) in files)
        {
            var f = await Json(await UploadAsync(client, id, name, bytes, type));
            uploads.Add(new Upload(f.GetProperty("id").GetInt32(), name, bytes));
        }
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return new Entry(id, submitted.GetProperty("referenceNumber").GetString()!, uploads, client);
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

    private async Task<(HttpClient Admin, int Competition)> PublishAsync(Entry winner)
    {
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        await SetFieldPublicAsync(admin, competition, "files", true);
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competition}/result/winners", new { submissionId = winner.Id }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competition}/status", new { status = "Closed" }));
        await Json(await admin.PostAsync($"/api/admin/competitions/{competition}/result/publish", null));
        return (admin, competition);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- tests ------------------------------------------------------------------------------------

    [Fact]
    public async Task Public_images_are_metadata_free_derivatives_and_the_original_stays_private_and_unchanged()
    {
        var jpeg = TestImages.JpegWithExif(120, 60, orientation: 6);
        var png = TestImages.PngWithMetadata();
        var pdf = TestImages.PdfWithMetadata();
        Assert.NotEmpty(TestImages.LeakedMarkers(jpeg)); // the fixtures really do carry the secrets
        Assert.NotEmpty(TestImages.LeakedMarkers(png));

        var entry = await SubmitAsync("mastaga", ("telefon.jpg", jpeg, "image/jpeg"), ("eskiz.png", png, "image/png"), ("izah.pdf", pdf, "application/pdf"));
        await WaitForDerivativesAsync(entry.Id);
        await PublishAsync(entry);

        var anonymous = _factory.CreateClient();
        var work = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{entry.Reference}"));
        var files = work.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["image", "image", "document-preview"], files.Select(f => f.GetProperty("kind").GetString()).ToList());
        Assert.DoesNotContain("izah.pdf", work.GetRawText());
        Assert.DoesNotContain("telefon", work.GetRawText());

        foreach (var (file, i) in files.Select((f, i) => (f, i)))
        {
            var res = await anonymous.GetAsync(file.GetProperty("url").GetString());
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/webp", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
            Assert.Equal($"{entry.Reference}-{i + 1}.webp", res.Content.Headers.ContentDisposition?.FileNameStar);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Empty(TestImages.LeakedMarkers(bytes));
            Assert.DoesNotContain("%PDF", System.Text.Encoding.Latin1.GetString(bytes));
            Assert.All(entry.Files, u => Assert.NotEqual(Sha(u.Bytes), Sha(bytes)));

            // Revalidation keeps the gate on every request but avoids re-sending the bytes.
            var again = new HttpRequestMessage(HttpMethod.Get, file.GetProperty("url").GetString());
            again.Headers.IfNoneMatch.Add(res.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(again)).StatusCode);
        }

        // EXIF orientation 6 was applied to the pixels: stored 120×60, published upright as 60×120.
        var first = await (await anonymous.GetAsync(files[0].GetProperty("url").GetString())).Content.ReadAsByteArrayAsync();
        Assert.Equal((60, 120), TestImages.Dimensions(first));

        // Originals: unchanged on disk, reachable only by their owner (and admins), never publicly.
        var stored = _factory.WithDb(db => db.SubmissionFiles.Where(f => f.SubmissionId == entry.Id).OrderBy(f => f.SortOrder)
            .Select(f => new { f.Id, f.StoragePath, f.PublicAssetKey }).ToList());
        foreach (var (s, u) in stored.Zip(entry.Files))
        {
            Assert.Equal(Sha(u.Bytes), Sha(await File.ReadAllBytesAsync(Path.Combine(_factory.StorageRoot, s.StoragePath))));
            Assert.NotEqual(s.StoragePath, s.PublicAssetKey);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/submissions/{entry.Id}/files/{s.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/media/{s.StoragePath}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/media/{s.PublicAssetKey}")).StatusCode);
            var owner = await entry.Owner.GetAsync($"/api/submissions/{entry.Id}/files/{s.Id}");
            Assert.Equal(Sha(u.Bytes), Sha(await owner.Content.ReadAsByteArrayAsync()));
        }
    }

    [Fact]
    public async Task A_public_asset_url_works_only_while_the_result_is_published_and_the_file_is_public()
    {
        var entry = await SubmitAsync("mastaga", ("a.png", TestImages.Png(), "image/png"));
        var other = await SubmitAsync("suvelan", ("b.png", TestImages.Png(), "image/png"));
        await WaitForDerivativesAsync(entry.Id, other.Id);
        var anonymous = _factory.CreateClient();
        string Url(Entry e) => $"/api/competitions/{Chevron}/gallery/{e.Reference}/files/{e.Files[0].FileId}";

        await Body(await anonymous.GetAsync(Url(entry)), HttpStatusCode.NotFound); // prepared, not published
        var (admin, competition) = await PublishAsync(entry);
        var ok = await anonymous.GetAsync(Url(entry));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var etag = ok.Headers.ETag!;
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(Url(other))).StatusCode);

        // A rejected entry leaves the gallery at once.
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{other.Id}/status", new { status = "Rejected" }));
        await Body(await anonymous.GetAsync(Url(other)), HttpStatusCode.NotFound);

        // Making the field private again closes its files.
        await SetFieldPublicAsync(admin, competition, "files", false);
        await Body(await anonymous.GetAsync(Url(entry)), HttpStatusCode.NotFound);
        await SetFieldPublicAsync(admin, competition, "files", true);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(Url(entry))).StatusCode);

        // Unpublishing closes the very same URL — also for a client that revalidates with an old ETag.
        await Json(await admin.PostAsync($"/api/admin/competitions/{competition}/result/unpublish", null));
        var stale = new HttpRequestMessage(HttpMethod.Get, Url(entry));
        stale.Headers.IfNoneMatch.Add(etag);
        await Body(await anonymous.SendAsync(stale), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Decompression_bombs_and_corrupt_images_are_refused_at_upload()
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync("bomb@example.com"));
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = Chevron, categorySlug = "mastaga" })))
            .GetProperty("id").GetInt32();

        var bomb = await Body(await UploadAsync(client, id, "bomba.png", TestImages.PngBomb(), "image/png"), HttpStatusCode.BadRequest);
        Assert.Contains("çox böyükdür", bomb);

        byte[] corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. RandomNumberGenerator.GetBytes(2000)];
        var broken = await Body(await UploadAsync(client, id, "zeduli.png", corrupt, "image/png"), HttpStatusCode.BadRequest);
        Assert.Contains("oxunmadı", broken);
        Assert.Equal(0, _factory.WithDb(db => db.SubmissionFiles.Count()));
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_gets_a_placeholder_never_the_original()
    {
        // Passes the signature check ("%PDF-") but has no renderable page.
        var broken = "%PDF-1.4\nthis is not a real document\n%%EOF"u8.ToArray();
        var entry = await SubmitAsync("mastaga", ("sened.pdf", broken, "application/pdf"), ("ok.png", TestImages.Png(), "image/png"));
        await WaitForDerivativesAsync(entry.Id);
        var (admin, competition) = await PublishAsync(entry);

        var status = _factory.WithDb(db => db.SubmissionFiles.Single(f => f.Id == entry.Files[0].FileId));
        Assert.Equal(PublicAssetStatus.Failed, status.PublicAssetStatus);
        Assert.Null(status.PublicAssetKey);

        var anonymous = _factory.CreateClient();
        var work = await Json(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{entry.Reference}"));
        Assert.Single(work.GetProperty("files").EnumerateArray()); // only the PNG derivative
        await Body(await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{entry.Reference}/files/{entry.Files[0].FileId}"), HttpStatusCode.NotFound);

        var result = await Json(await admin.GetAsync($"/api/admin/competitions/{competition}/result"));
        var failure = Assert.Single(result.GetProperty("assetFailures").EnumerateArray());
        Assert.Equal(entry.Reference, failure.GetProperty("reference").GetString());
        Assert.Equal("pdf-unreadable", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admins_choose_the_gallery_preview_only_among_the_submissions_own_public_safe_files()
    {
        var entry = await SubmitAsync("mastaga", ("bir.png", TestImages.Png(), "image/png"), ("iki.jpg", TestImages.Jpeg(), "image/jpeg"));
        var stranger = await SubmitAsync("suvelan", ("yad.png", TestImages.Png(), "image/png"));
        await WaitForDerivativesAsync(entry.Id, stranger.Id);
        var admin = await AdminAsync();
        var competition = await ChevronIdAsync(admin);
        var url = $"/api/admin/submissions/{entry.Id}/preview";

        // Not public yet → refused; another submission's file → refused (IDOR); participants/anonymous → denied.
        Assert.Contains("ictimai deyil", await Body(await admin.PutAsJsonAsync(url, new { fileId = entry.Files[1].FileId }), HttpStatusCode.BadRequest));
        await SetFieldPublicAsync(admin, competition, "files", true);
        Assert.Contains("aid deyil", await Body(await admin.PutAsJsonAsync(url, new { fileId = stranger.Files[0].FileId }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await entry.Owner.PutAsJsonAsync(url, new { fileId = entry.Files[1].FileId })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PutAsJsonAsync(url, new { fileId = entry.Files[1].FileId })).StatusCode);

        var detail = await Json(await admin.PutAsJsonAsync(url, new { fileId = entry.Files[1].FileId }));
        Assert.Equal(entry.Files[1].FileId, detail.GetProperty("effectivePreviewFileId").GetInt32());
        Assert.Equal(entry.Files[1].FileId, detail.GetProperty("previewFileId").GetInt32());

        // Admins can see the exact public-safe image before publication.
        var safe = await admin.GetAsync($"/api/admin/submissions/{entry.Id}/files/{entry.Files[1].FileId}/public");
        Assert.Equal("image/webp", safe.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await entry.Owner.GetAsync($"/api/admin/submissions/{entry.Id}/files/{entry.Files[1].FileId}/public")).StatusCode);

        await PublishAsync(entry);
        var gallery = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/gallery"));
        var card = gallery.GetProperty("items").EnumerateArray().First(i => i.GetProperty("reference").GetString() == entry.Reference);
        Assert.EndsWith($"/files/{entry.Files[1].FileId}", card.GetProperty("preview").GetProperty("url").GetString());

        // Clearing the choice falls back to the first image.
        var cleared = await Json(await admin.PutAsJsonAsync(url, new { fileId = (int?)null }));
        Assert.Equal(entry.Files[0].FileId, cleared.GetProperty("effectivePreviewFileId").GetInt32());
        var audit = await Json(await admin.GetAsync($"/api/admin/audit?entityType=Submission&entityId={entry.Id}"));
        Assert.Equal(2, audit.EnumerateArray().Count(a => a.GetProperty("action").GetString() == "PreviewChanged"));
    }

    [Fact]
    public async Task Small_previews_are_card_size_copies_under_the_same_access_rules_as_the_original()
    {
        var big = TestImages.NoisePng(3_000_000); // ~870×870 noise: a heavy original that compresses badly
        var entry = await SubmitAsync("mastaga", ("boyuk.png", big, "image/png"));
        await WaitForDerivativesAsync(entry.Id);
        await WaitForThumbnailsAsync(entry.Id);
        var admin = await AdminAsync();
        var fileId = entry.Files[0].FileId;
        var thumb = $"/api/submissions/{entry.Id}/files/{fileId}/thumb";

        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{entry.Id}"));
        Assert.True(detail.GetProperty("files")[0].GetProperty("thumbnailReady").GetBoolean());

        var res = await admin.GetAsync(thumb);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/webp", res.Content.Headers.ContentType?.MediaType);
        Assert.True(res.Headers.CacheControl is { Private: true, NoCache: true, NoStore: false }, res.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var (w, h) = TestImages.Dimensions(bytes);
        Assert.True(Math.Max(w, h) <= 640, $"thumbnail is {w}×{h}");
        Assert.True(bytes.Length * 10 < big.Length, $"thumbnail {bytes.Length} B vs original {big.Length} B");
        // (Metadata: the copy is made from the derivative, which the fixture test above proves metadata-free; random
        // noise could contain a marker like "GPS" by chance, so it is not scanned here.)

        // Revalidated on every use (access re-checked) without re-sending the bytes.
        var again = new HttpRequestMessage(HttpMethod.Get, thumb);
        again.Headers.IfNoneMatch.Add(res.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await admin.SendAsync(again)).StatusCode);

        // The owner may; a stranger and the anonymous public may not (the gallery has its own gated route).
        Assert.Equal(HttpStatusCode.OK, (await entry.Owner.GetAsync(thumb)).StatusCode);
        var stranger = _factory.CreateClient();
        stranger.UseToken(await stranger.RegisterAsync($"s-{Guid.NewGuid():N}@example.com"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(thumb)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(thumb)).StatusCode);

        // Public: only through the result gate, and the card carries both sizes.
        await Body(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/gallery/{entry.Reference}/files/{fileId}/thumb"), HttpStatusCode.NotFound);
        await PublishAsync(entry);
        var gallery = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/gallery"));
        var card = gallery.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("reference").GetString() == entry.Reference);
        Assert.Equal("Ready", card.GetProperty("previewState").GetString());
        var preview = card.GetProperty("preview");
        Assert.Equal(preview.GetProperty("url").GetString() + "/thumb", preview.GetProperty("thumbUrl").GetString());
        Assert.True(preview.GetProperty("width").GetInt32() > 640);
        var publicThumb = await _factory.CreateClient().GetAsync(preview.GetProperty("thumbUrl").GetString());
        Assert.Equal("no-cache", publicThumb.Headers.CacheControl?.ToString());
        var publicBytes = await publicThumb.Content.ReadAsByteArrayAsync();
        Assert.True(Math.Max(TestImages.Dimensions(publicBytes).Item1, TestImages.Dimensions(publicBytes).Item2) <= 640);
    }

    [Fact]
    public async Task Cards_say_when_a_preview_is_still_being_made_or_could_not_be_made()
    {
        var pending = await SubmitAsync("mastaga", ("a.png", TestImages.Png(), "image/png"));
        var broken = await SubmitAsync("suvelan", ("sened.pdf", "%PDF-1.4\nnot a document\n%%EOF"u8.ToArray(), "application/pdf"));
        await WaitForDerivativesAsync(pending.Id, broken.Id);
        // As if the worker had not reached it yet (the next sweep is minutes away in tests).
        await _factory.WithDbAsync(async db =>
        {
            var f = await db.SubmissionFiles.SingleAsync(x => x.SubmissionId == pending.Id);
            f.PublicAssetStatus = PublicAssetStatus.Pending;
            await db.SaveChangesAsync();
        });
        await PublishAsync(pending);

        var items = (await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Chevron}/gallery"))).GetProperty("items")
            .EnumerateArray().ToDictionary(i => i.GetProperty("reference").GetString()!, i => i);
        Assert.Equal("Processing", items[pending.Reference].GetProperty("previewState").GetString());
        Assert.Equal(JsonValueKind.Null, items[pending.Reference].GetProperty("preview").ValueKind);
        Assert.Equal("Unavailable", items[broken.Reference].GetProperty("previewState").GetString());
        Assert.Equal(JsonValueKind.Null, items[broken.Reference].GetProperty("preview").ValueKind);
    }

    private async Task WaitForThumbnailsAsync(params int[] submissionIds)
    {
        for (var i = 0; i < 200; i++)
        {
            var pending = _factory.WithDb(db => db.SubmissionFiles.Count(f =>
                submissionIds.Contains(f.SubmissionId) && f.ThumbnailStatus == PublicAssetStatus.Pending));
            if (pending == 0) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("thumbnails were not produced");
    }

    [Fact]
    public async Task Uploads_record_that_no_antivirus_scanned_them()
    {
        var entry = await SubmitAsync("mastaga", ("a.png", TestImages.Png(), "image/png"));
        var admin = await AdminAsync();
        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{entry.Id}"));
        Assert.Equal("NotScanned", detail.GetProperty("files")[0].GetProperty("scanStatus").GetString());
    }
}
