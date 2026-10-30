using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meydan.Api.Features.Demo;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SkiaSharp;

namespace Meydan.Api.Tests;

/// <summary>
/// `seed-demo` / `remove-demo`: demo content in the real database through the real pipelines (accounts, covers, private
/// files with public-safe copies, category winners), marked IsDemo, idempotent, and removed without touching real work.
/// Uses the asset pack committed in ./demo-assets.
/// </summary>
public sealed class DemoDataTests : IDisposable
{
    private const string Chevron = DbSeeder.ChevronCompetitionSlug;
    private static readonly string[] WinningSettlements = ["Balaxanı", "Ramana", "Maştağa", "Mərdəkan", "Şüvəlan", "Buzovna", "Nardaran", "Biləcəri"];

    private readonly MeydanFactory _factory = new();
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var dir in _temp)
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>./demo-assets of the repository.</summary>
    internal static string AssetsFolder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "demo-assets");
                if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
            }
            throw new InvalidOperationException("demo-assets/manifest.json not found above the test folder");
        }
    }

    private async Task<DemoReport> SeedAsync(string? folder = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoData>().SeedAsync(folder ?? AssetsFolder, CancellationToken.None);
    }

    private async Task<DemoRemovalReport> RemoveAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DemoData>().RemoveAsync(CancellationToken.None);
    }

    /// <summary>Stored files (keys), without in-flight temp files and health probes.</summary>
    private List<string> StoredFiles() =>
        Directory.Exists(_factory.StorageRoot)
            ? Directory.EnumerateFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_factory.StorageRoot, f).Replace('\\', '/'))
                .Where(f => !f.EndsWith(".partial", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith(".health-", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Real uploads get their public-safe copies in the background: wait for them so storage is stable.</summary>
    private async Task WaitForPublicCopiesAsync()
    {
        for (var i = 0; i < 150; i++)
        {
            var pending = await _factory.QueryDbAsync(db => db.SubmissionFiles.CountAsync(f =>
                f.PublicAssetStatus == PublicAssetStatus.Pending || f.ThumbnailStatus == PublicAssetStatus.Pending));
            if (pending == 0) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("public-safe copies were not made");
    }

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

    /// <summary>A real participant's final entry, through the public API (registration, draft, upload, submit).</summary>
    private async Task<(int Id, string Email)> RealEntryAsync(string competitionSlug, string? categorySlug, bool withFile)
    {
        var email = $"real-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync(email));
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug, categorySlug }));
        var id = draft.GetProperty("id").GetInt32();
        object answers = competitionSlug == Chevron
            ? new { title = "Real iş", concept = "Real konsept", symbols = "Real simvollar" } // the chevron form asks for symbols too
            : new { title = "Real iş", concept = "Real konsept" };
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers }));
        if (withFile)
        {
            var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
            var file = new ByteArrayContent(TestImages.Png());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "eskiz.png");
            await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        }
        await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return (id, email);
    }

    // ---- Seeding ---------------------------------------------------------------------------------

    [Fact]
    public async Task Seeds_the_supplied_pack_through_the_real_pipelines()
    {
        var report = await SeedAsync();
        Assert.Equal((3, 3, 15, 32, 15, 8, true),
            (report.ProjectsCreated, report.CompetitionsCreated, report.UsersCreated, report.SubmissionsCreated,
                report.FilesImported, report.WinnersCreated, report.ResultCoverSet));
        Assert.Empty(report.Notes);

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AssetsFolder, "manifest.json")))!;
        var settlements = manifest["submissions"]!.AsArray().Select(s => (Name: s!["settlement"]!.GetValue<string>(), File: s["file"]!.GetValue<string>())).ToList();

        await _factory.WithDbAsync(async db =>
        {
            // The real Bakı project, competition and its 59 settlements are neither duplicated nor turned into demo.
            Assert.Equal(1, await db.Projects.CountAsync(p => p.Slug == DbSeeder.BakiProjectSlug && !p.IsDemo));
            var chevron = await db.Competitions.Include(c => c.Categories).SingleAsync(c => c.Slug == Chevron);
            Assert.False(chevron.IsDemo);
            Assert.Equal(59, chevron.Categories.Count);

            // Participants: undeliverable addresses, no password, locked, Participant only, never linked to QRLog.
            var people = await db.Users.Where(u => u.IsDemo).ToListAsync();
            Assert.Equal(15, people.Count);
            Assert.All(people, u =>
            {
                Assert.EndsWith("@example.invalid", u.Email);
                Assert.Null(u.PasswordHash);
                Assert.True(u.LockoutEnabled);
                Assert.True(u.LockoutEnd > DateTimeOffset.UtcNow.AddYears(100));
            });
            var ids = people.Select(u => u.Id).ToList();
            var roles = await db.UserRoles.Where(r => ids.Contains(r.UserId))
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name).Distinct().ToListAsync();
            Assert.Equal(Roles.Participant, Assert.Single(roles));
            Assert.False(await db.UserLogins.AnyAsync(l => ids.Contains(l.UserId)));

            // Projects and competitions: demo-marked, published, covers through the cover pipeline (public covers area).
            var projects = await db.Projects.Where(p => p.IsDemo).ToListAsync();
            Assert.Equal(new[] { "abseron-irsi", "xezer-sahili", "yasil-baki" }, projects.Select(p => p.Slug).Order().ToArray());
            var competitions = await db.Competitions.Where(c => c.IsDemo).ToListAsync();
            Assert.Equal(3, competitions.Count);
            Assert.Contains(competitions, c => c.Status == CompetitionStatus.Published && c.StartDate < DateTimeOffset.UtcNow && c.EndDate > DateTimeOffset.UtcNow);
            Assert.Contains(competitions, c => c.Status == CompetitionStatus.Published && c.StartDate > DateTimeOffset.UtcNow);
            Assert.Contains(competitions, c => c.Status == CompetitionStatus.Closed && c.EndDate < DateTimeOffset.UtcNow);
            Assert.All(competitions, c => Assert.Null(c.Prize)); // no invented business facts
            foreach (var cover in projects.Select(p => p.CoverImage).Concat(competitions.Select(c => c.CoverImage)))
            {
                Assert.StartsWith("/api/media/covers/", cover);
                Assert.True(File.Exists(Path.Combine(_factory.StorageRoot, cover!["/api/media/".Length..])));
            }

            // Every settlement of the manifest: exactly one public entry, carrying that settlement's own chevron.
            foreach (var (name, file) in settlements)
            {
                var entries = await db.Submissions.Include(s => s.Files).Include(s => s.Category)
                    .Where(s => s.IsDemo && s.CompetitionId == chevron.Id && s.Category!.Name == name).ToListAsync();
                var visible = entries.Where(s => CompetitionResult.IsEligible(s.Status)).ToList();
                Assert.True(visible.Count == 1, $"{name}: {visible.Count} public entries");
                var stored = Assert.Single(visible[0].Files);
                var expected = DemoArtwork.Prepare(await File.ReadAllBytesAsync(Path.Combine(AssetsFolder, file)));
                Assert.True(expected.Trimmed, $"{name}: caption not detected");
                Assert.Equal(expected.Png.Length, stored.SizeBytes); // that settlement's file, prepared
                Assert.Equal(Path.GetFileNameWithoutExtension(file) + ".png", stored.OriginalFileName);
                Assert.Equal("image/png", stored.ContentType);
                Assert.Equal((PublicAssetStatus.Ready, PublicAssetStatus.Ready), (stored.PublicAssetStatus, stored.ThumbnailStatus));
                Assert.StartsWith("derived/", stored.PublicAssetKey);
                Assert.False(stored.StoragePath.StartsWith("derived/") || stored.StoragePath.StartsWith("covers/")); // original stays private
                Assert.NotNull(visible[0].ReferenceNumber);
                Assert.All(entries.Where(s => s.Status == SubmissionStatus.Draft), d => Assert.Null(d.ReferenceNumber));
            }

            // Winners: one per listed settlement, each the entry of that very settlement; never an overall winner.
            var winners = await db.CompetitionCategoryWinners.Include(w => w.Category).Include(w => w.Submission).ToListAsync();
            Assert.Equal(WinningSettlements.Order(), winners.Select(w => w.Category.Name).Order());
            Assert.All(winners, w => Assert.True(w.Submission.IsDemo && w.Submission.CompetitionCategoryId == w.CompetitionCategoryId));
            Assert.False(await db.CompetitionResults.AnyAsync(r => r.OverallWinnerSubmissionId != null));

            // The Bakı result gets the supplied banner but is not published by the seed.
            var result = await db.CompetitionResults.SingleAsync(r => r.CompetitionId == chevron.Id);
            Assert.Null(result.PublishedAt);
            Assert.True(result.CoverIsDemo);
            Assert.StartsWith("/api/media/covers/", result.CoverImage);

            // No machine path anywhere in what was stored.
            var paths = (await db.SubmissionFiles.Select(f => new[] { f.StoragePath, f.PublicAssetKey, f.ThumbnailKey, f.OriginalFileName }).ToListAsync())
                .SelectMany(x => x).Concat(projects.Select(p => p.CoverImage)).Concat(competitions.Select(c => c.CoverImage)).Append(result.CoverImage)
                .OfType<string>().ToList();
            Assert.All(paths, p => Assert.DoesNotMatch(@"[A-Za-z]:[\\/]|\\|/Users/|/home/|demo-assets", p));

            Assert.True(await db.SeedHistory.AnyAsync(h => h.Key == DemoData.SeedKey));
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.DemoSeeded));
        });

        // Demo accounts can never be signed into.
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "aysel.mammadova@example.invalid", password = "anything-at-all-1", rememberMe = false });
        Assert.False(login.IsSuccessStatusCode);
        Assert.DoesNotContain("accessToken", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_public_site_shows_demo_content_as_ordinary_content_and_nothing_before_results()
    {
        await SeedAsync();
        var anonymous = _factory.CreateClient();

        var projects = await anonymous.GetStringAsync("/api/projects");
        Assert.Contains("Xəzər sahili", projects);
        Assert.Contains("Abşeron irsi", projects);
        var competition = await Json(await anonymous.GetAsync("/api/competitions/xezerin-nefesi-poster"));
        Assert.StartsWith("/api/media/covers/", competition.GetProperty("coverImage").GetString());
        var home = await anonymous.GetStringAsync("/api/home");

        // Nothing public says "demo" or shows an address.
        foreach (var body in new[] { projects, competition.GetRawText(), home })
        {
            Assert.DoesNotContain("isDemo", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.invalid", body);
        }

        // The Bakı result is not published by the seed: its results and gallery stay closed.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Chevron}/results")).StatusCode);
        Assert.False((await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        await SeedAsync();
        var files = StoredFiles();
        var counter = await _factory.QueryDbAsync(db => db.ReferenceCounters.SumAsync(c => c.Value));

        var again = await SeedAsync();

        Assert.Equal((0, 0, 0, 0, 0, 0, false),
            (again.ProjectsCreated, again.CompetitionsCreated, again.UsersCreated, again.SubmissionsCreated,
                again.FilesImported, again.WinnersCreated, again.ResultCoverSet));
        Assert.Equal(files, StoredFiles());
        Assert.Equal(counter, await _factory.QueryDbAsync(db => db.ReferenceCounters.SumAsync(c => c.Value)));
        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(15, await db.Users.CountAsync(u => u.IsDemo));
            Assert.Equal(32, await db.Submissions.CountAsync(s => s.IsDemo));
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.DemoSeeded)); // a no-op run is not logged
        });
    }

    [Fact]
    public async Task Demo_files_follow_the_same_access_rules_as_real_ones()
    {
        await SeedAsync();
        var admin = await AdminAsync();
        var competitionId = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).SingleAsync());
        // As in production: the file field is public after results; an admin closes entries and publishes.
        var files = (await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}/fields"))).EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == "files");
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/fields/{files.GetProperty("id").GetInt32()}", new
        {
            key = "files", label = files.GetProperty("label").GetString(), fieldType = "File", isRequired = true,
            isSubmissionTitle = false, isActive = true, isPublicAfterResults = true, optionsJson = files.GetProperty("optionsJson").GetString(),
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/status", new { status = "Closed" }));
        await Json(await admin.PostAsync($"/api/admin/competitions/{competitionId}/result/publish", null));

        var (winner, rejected) = await _factory.QueryDbAsync(async db =>
        {
            var rows = await db.Submissions.Include(s => s.Files).Include(s => s.Category)
                .Where(s => s.IsDemo && s.Category!.Name == "Balaxanı" && s.Status != SubmissionStatus.Draft).ToListAsync();
            return (rows.Single(s => s.Status == SubmissionStatus.Approved), rows.Single(s => s.Status == SubmissionStatus.Rejected));
        });
        var file = winner.Files.Single();
        var anonymous = _factory.CreateClient();

        // The private original: never public, not even through the media route.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/submissions/{winner.Id}/files/{file.Id}")).StatusCode);
        Assert.False((await anonymous.GetAsync($"/api/media/{file.StoragePath}")).IsSuccessStatusCode);
        // The public-safe copy and its thumbnail, now that the result is published.
        var copy = await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{winner.ReferenceNumber}/files/{file.Id}");
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.Equal("image/webp", copy.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{winner.ReferenceNumber}/files/{file.Id}/thumb")).StatusCode);
        // A rejected entry stays out of public view.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/competitions/{Chevron}/gallery/{rejected.ReferenceNumber}")).StatusCode);
        // Admins preview both.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/admin/submissions/{winner.Id}/files/{file.Id}/public")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/submissions/{winner.Id}/files/{file.Id}")).StatusCode);

        // Winners are named; nobody else is; no address, storage key or private answer is public.
        var gallery = await anonymous.GetStringAsync($"/api/competitions/{Chevron}/gallery");
        var results = await anonymous.GetStringAsync($"/api/competitions/{Chevron}/results");
        Assert.Contains("Aysel Məmmədova", gallery);
        Assert.Contains("Balaxanının yaddaşı", results);
        foreach (var body in new[] { gallery, results })
        {
            Assert.DoesNotContain("Samir Məmmədli", body); // entries, never a winner
            Assert.DoesNotContain("example.invalid", body);
            Assert.DoesNotContain(file.StoragePath, body);
            Assert.DoesNotContain("Neft buruğu sənaye tarixini", body); // "symbols" is not a public field
        }
    }

    // ---- Removal -------------------------------------------------------------------------------------

    [Fact]
    public async Task Removes_exactly_the_demo_data_and_keeps_real_work()
    {
        var (realId, realEmail) = await RealEntryAsync(Chevron, "ramana", withFile: true);
        await WaitForPublicCopiesAsync();
        var before = StoredFiles();
        await SeedAsync();

        var report = await RemoveAsync();

        Assert.Equal((3, 3, 15, 32, 8, true),
            (report.ProjectsRemoved, report.CompetitionsRemoved, report.UsersRemoved, report.SubmissionsRemoved,
                report.WinnersRemoved, report.ResultCoverRemoved));
        Assert.Empty(report.Kept);
        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.IsDemo));
            Assert.False(await db.Projects.AnyAsync(p => p.IsDemo));
            Assert.False(await db.Competitions.AnyAsync(c => c.IsDemo));
            Assert.False(await db.Submissions.AnyAsync(s => s.IsDemo));
            Assert.False(await db.CompetitionCategoryWinners.AnyAsync());
            Assert.False(await db.CompetitionResults.AnyAsync()); // the row the seed created, unused otherwise
            Assert.False(await db.SeedHistory.AnyAsync(h => h.Key == DemoData.SeedKey));
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.DemoRemoved));

            // Real work untouched: the Bakı set-up, the participant, the entry and its file.
            Assert.Equal(59, await db.CompetitionCategories.CountAsync(k => k.Competition.Slug == Chevron));
            Assert.True(await db.Users.AnyAsync(u => u.Email == realEmail));
            var real = await db.Submissions.Include(s => s.Files).SingleAsync(s => s.Id == realId);
            Assert.Equal(SubmissionStatus.Submitted, real.Status);
            var file = Assert.Single(real.Files);
            Assert.True(File.Exists(Path.Combine(_factory.StorageRoot, file.StoragePath)));
        });
        // Storage is exactly as it was before the seed: every demo cover, original and public copy is gone.
        Assert.Equal(before, StoredFiles());
    }

    [Fact]
    public async Task Keeps_a_demo_account_that_voted_and_its_vote()
    {
        var (realId, _) = await RealEntryAsync(Chevron, "ramana", withFile: true);
        await WaitForPublicCopiesAsync();
        await SeedAsync();
        // A demo account votes for real work: removing the demo must not take the vote with the account.
        var voter = await _factory.QueryDbAsync(async db =>
        {
            var u = await db.Users.Where(x => x.IsDemo).OrderBy(x => x.Email).FirstAsync();
            var category = await db.Submissions.Where(s => s.Id == realId).Select(s => s.CompetitionCategoryId).FirstAsync();
            db.Votes.Add(new Vote { UserId = u.Id, SubmissionId = realId, CompetitionCategoryId = category!.Value, CreatedAt = _factory.Clock.Now });
            await db.SaveChangesAsync();
            return (u.Id, u.Email);
        });

        var report = await RemoveAsync();

        Assert.Equal(14, report.UsersRemoved);
        Assert.Equal([$"{voter.Email} has votes — kept"], report.Kept);
        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Id == voter.Id));
            Assert.Equal(1, await db.Votes.CountAsync(v => v.UserId == voter.Id && v.SubmissionId == realId));
            Assert.Equal(1, await db.Users.CountAsync(u => u.IsDemo));
        });
    }

    [Fact]
    public async Task Keeps_a_demo_competition_that_received_real_work()
    {
        await SeedAsync();
        var (realId, _) = await RealEntryAsync("xezerin-nefesi-poster", null, withFile: false);

        var report = await RemoveAsync();

        Assert.Contains(report.Kept, k => k.Contains("Xəzərin nəfəsi"));
        Assert.Contains(report.Kept, k => k.Contains("Xəzər sahili"));
        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Competitions.AnyAsync(c => c.Slug == "xezerin-nefesi-poster"));
            Assert.True(await db.Projects.AnyAsync(p => p.Slug == "xezer-sahili"));
            Assert.True(await db.Submissions.AnyAsync(s => s.Id == realId));
            Assert.False(await db.Submissions.AnyAsync(s => s.IsDemo));
            Assert.False(await db.Competitions.AnyAsync(c => c.Slug == "das-yaddasi-foto" || c.Slug == "yasil-seher-vizual-kimlik"));
        });
    }

    // ---- Real results are never overridden or announced ------------------------------------------------

    [Fact]
    public async Task Never_replaces_a_real_winner()
    {
        var (realId, _) = await RealEntryAsync(Chevron, "balaxani", withFile: true);
        var admin = await AdminAsync();
        var competitionId = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).SingleAsync());
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/result/winners", new { submissionId = realId, confirmPublished = false }));

        var report = await SeedAsync();

        Assert.Equal(7, report.WinnersCreated);
        Assert.Contains(report.Notes, n => n.StartsWith("Balaxanı: a real winner already exists"));
        await _factory.WithDbAsync(async db =>
        {
            var balaxani = await db.CompetitionCategoryWinners.SingleAsync(w => w.Category.Name == "Balaxanı");
            Assert.Equal(realId, balaxani.SubmissionId);
            // Choosing a winner does not create a result row; the seed made one (banner and demo announcement).
            var seeded = await db.CompetitionResults.SingleAsync(r => r.CompetitionId == competitionId);
            Assert.True(seeded.IsDemo);
            Assert.Equal(DemoContent.ResultAnnouncement, seeded.Announcement);
        });

        var removal = await RemoveAsync();

        Assert.Contains(removal.Kept, k => k.StartsWith("result of competition"));
        await _factory.WithDbAsync(async db =>
        {
            var left = await db.CompetitionCategoryWinners.SingleAsync();
            Assert.Equal(realId, left.SubmissionId);
            // The row stays for the real winner, but nothing of the demo stays on it.
            var result = await db.CompetitionResults.SingleAsync(r => r.CompetitionId == competitionId);
            Assert.False(result.IsDemo);
            Assert.Null(result.CoverImage);
            Assert.Null(result.Announcement);
            Assert.Null(result.PublishedAt);
        });
    }

    [Fact]
    public async Task Does_not_announce_demo_winners_in_a_published_result()
    {
        await _factory.WithDbAsync(async db =>
        {
            var id = await db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).SingleAsync();
            db.CompetitionResults.Add(new CompetitionResult { CompetitionId = id, PublishedAt = DateTimeOffset.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        });

        var report = await SeedAsync();

        Assert.Equal(0, report.WinnersCreated);
        Assert.False(report.ResultCoverSet);
        Assert.Equal(32, report.SubmissionsCreated);
        Assert.Contains(report.Notes, n => n.Contains("published"));
        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.CompetitionCategoryWinners.AnyAsync());
            var result = await db.CompetitionResults.SingleAsync();
            Assert.Null(result.CoverImage);
            Assert.NotNull(result.PublishedAt); // publication state untouched
        });
    }

    // ---- Asset validation ---------------------------------------------------------------------------------

    public static TheoryData<string> BrokenPacks => new() { "missing file", "not an image", "wrong size", "outside the folder", "settlement missing" };

    [Theory]
    [MemberData(nameof(BrokenPacks))]
    public async Task Rejects_a_broken_asset_pack_before_writing_anything(string problem)
    {
        var folder = CopyPack();
        var manifestPath = Path.Combine(folder, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        var first = manifest["submissions"]![0]!;
        switch (problem)
        {
            case "missing file":
                first["file"] = "submissions/yoxdur.webp";
                break;
            case "not an image":
                await File.WriteAllTextAsync(Path.Combine(folder, first["file"]!.GetValue<string>()), "bu şəkil deyil");
                break;
            case "wrong size":
                first["size"] = new JsonArray(10, 10);
                break;
            case "outside the folder":
                first["file"] = "../outside.webp";
                break;
            case "settlement missing":
                manifest["submissions"]!.AsArray().RemoveAt(0);
                break;
        }
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var before = StoredFiles();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(folder));

        Assert.Equal(before, StoredFiles());
        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.IsDemo));
            Assert.False(await db.Projects.AnyAsync(p => p.IsDemo));
            Assert.False(await db.Submissions.AnyAsync());
        });
    }

    private string CopyPack()
    {
        var target = Path.Combine(Path.GetTempPath(), "meydan-demo-pack-" + Guid.NewGuid().ToString("N"));
        _temp.Add(target);
        foreach (var file in Directory.EnumerateFiles(AssetsFolder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(AssetsFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return target;
    }

    // ---- Admin ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_lists_tell_demo_from_real()
    {
        var (realId, realEmail) = await RealEntryAsync(Chevron, "mastaga", withFile: true);
        await SeedAsync();
        var admin = await AdminAsync();

        var demo = await Json(await admin.GetAsync("/api/admin/submissions?demo=true&pageSize=100"));
        Assert.Equal(26, demo.GetProperty("total").GetInt32()); // final entries only; drafts are private to their author
        Assert.All(demo.GetProperty("items").EnumerateArray(), s => Assert.True(s.GetProperty("isDemo").GetBoolean()));
        var real = await Json(await admin.GetAsync("/api/admin/submissions?demo=false"));
        Assert.Equal(realId, Assert.Single(real.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());

        var people = await Json(await admin.GetAsync("/api/admin/participants?demo=true&pageSize=100"));
        Assert.Equal(15, people.GetProperty("total").GetInt32());
        var realPeople = await Json(await admin.GetAsync("/api/admin/participants?demo=false&pageSize=100"));
        Assert.Equal(new[] { MeydanFactory.AdminEmail, realEmail }.Order(StringComparer.Ordinal).ToArray(),
            realPeople.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("email").GetString() ?? "").Order(StringComparer.Ordinal).ToArray());

        var projects = await Json(await admin.GetAsync("/api/admin/projects"));
        Assert.Equal(new[] { "abseron-irsi", "xezer-sahili", "yasil-baki" },
            projects.EnumerateArray().Where(p => p.GetProperty("isDemo").GetBoolean()).Select(p => p.GetProperty("slug").GetString() ?? "").Order().ToArray());
        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{demo.GetProperty("items")[0].GetProperty("id").GetInt32()}"));
        Assert.True(detail.GetProperty("isDemo").GetBoolean());
    }

    [Fact]
    public async Task Admin_can_give_a_competition_a_poster_and_take_it_away()
    {
        var admin = await AdminAsync();
        var id = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).SingleAsync());
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestImages.Png(180, 234));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "cover", "poster.png");

        var withPoster = await Json(await admin.PostAsync($"/api/admin/competitions/{id}/cover", form));
        var url = withPoster.GetProperty("coverImage").GetString()!;
        Assert.StartsWith("/api/media/covers/", url);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync(url)).StatusCode);

        var without = await Json(await admin.DeleteAsync($"/api/admin/competitions/{id}/cover"));
        Assert.Equal(JsonValueKind.Null, without.GetProperty("coverImage").ValueKind);
        Assert.False(File.Exists(Path.Combine(_factory.StorageRoot, url["/api/media/".Length..])));
        await _factory.WithDbAsync(async db =>
            Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.CoverChanged && a.EntityId == id.ToString())));

        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.DeleteAsync($"/api/admin/competitions/{id}/cover")).StatusCode);
    }

    // ---- Command line -------------------------------------------------------------------------------------

    private sealed class HostEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Meydan.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Refuses_to_run_outside_development_without_the_explicit_flag(string environment)
    {
        var code = await DemoCommands.RunAsync(["seed-demo", "--assets", AssetsFolder], _factory.Services, new HostEnv(environment));

        Assert.Equal(2, code);
        await _factory.WithDbAsync(async db => Assert.False(await db.Users.AnyAsync(u => u.IsDemo)));
    }

    [Fact]
    public async Task Runs_with_the_explicit_flag_and_reports()
    {
        var code = await DemoCommands.RunAsync(
            ["seed-demo", "--assets", AssetsFolder, DemoCommands.AllowProduction], _factory.Services, new HostEnv("Production"));

        Assert.Equal(0, code);
        await _factory.WithDbAsync(async db => Assert.Equal(15, await db.Users.CountAsync(u => u.IsDemo)));
        Assert.Equal(0, await DemoCommands.RunAsync(["remove-demo", DemoCommands.AllowProduction], _factory.Services, new HostEnv("Production")));
        await _factory.WithDbAsync(async db => Assert.False(await db.Users.AnyAsync(u => u.IsDemo)));
    }

    [Fact]
    public async Task Asks_for_exactly_one_command()
    {
        Assert.Equal(2, await DemoCommands.RunAsync(["seed-demo", "remove-demo"], _factory.Services, new HostEnv("Development")));
    }
}

/// <summary>The chevron and cover preparation on the supplied files, without a database.</summary>
public sealed class DemoArtworkTests
{
    public static TheoryData<string> Chevrons
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(DemoDataTests.AssetsFolder, "submissions"), "*.webp").Order())
                data.Add(Path.GetFileName(file));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Chevrons))]
    public void Cuts_each_supplied_chevron_out_of_its_contact_sheet(string file)
    {
        var prepared = DemoArtwork.Prepare(File.ReadAllBytes(Path.Combine(DemoDataTests.AssetsFolder, "submissions", file)));

        Assert.True(prepared.Trimmed);
        using var image = SKBitmap.Decode(prepared.Png);
        Assert.Equal((prepared.Width, prepared.Height), (image.Width, image.Height));
        Assert.InRange(image.Width, 220, 300);
        Assert.InRange(image.Height, 290, 340);
        // Transparent around the shield: the corners and the whole last row (the caption lay below it).
        Assert.Equal(0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(0, image.GetPixel(image.Width - 1, 0).Alpha);
        Assert.All(Enumerable.Range(0, image.Width), x => Assert.Equal(0, image.GetPixel(x, image.Height - 1).Alpha));
        // Opaque artwork in the middle.
        Assert.Equal(255, image.GetPixel(image.Width / 2, image.Height / 2).Alpha);
    }

    [Fact]
    public void Leaves_any_other_image_as_it_is()
    {
        var prepared = DemoArtwork.Prepare(TestImages.Png(64, 48));

        Assert.False(prepared.Trimmed);
        Assert.Equal((64, 48), (prepared.Width, prepared.Height));
    }

    [Theory]
    [InlineData("xezer-sahili-cover.webp")]
    [InlineData("yasil-baki-cover.webp")]
    [InlineData("abseron-irsi-cover.webp")]
    public void Crops_a_supplied_project_cover_to_its_text_free_part(string file)
    {
        var source = File.ReadAllBytes(Path.Combine(DemoDataTests.AssetsFolder, "projects", file));
        using var original = SKBitmap.Decode(source);

        using var cropped = SKBitmap.Decode(DemoArtwork.CropCover(source, 0.48));

        Assert.Equal(original.Width - (int)Math.Ceiling(original.Width * 0.48) - 2, cropped.Width);
        Assert.Equal(original.Height - 4, cropped.Height);
    }

    [Fact]
    public void Refuses_a_crop_that_would_leave_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoArtwork.CropCover(TestImages.Png(), 0.95));
    }
}
