using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Demo;

public sealed record DemoReport(
    int ProjectsCreated, int CompetitionsCreated, int UsersCreated, int SubmissionsCreated, int FilesImported,
    int WinnersCreated, bool ResultCoverSet, IReadOnlyList<string> Notes);

public sealed record DemoRemovalReport(
    int ProjectsRemoved, int CompetitionsRemoved, int UsersRemoved, int SubmissionsRemoved, int StoredFilesDeleted,
    int WinnersRemoved, bool ResultCoverRemoved, IReadOnlyList<string> Kept);

/// <summary>
/// Demo content in the real database, through the real pipelines (`seed-demo` / `remove-demo`): accounts through
/// Identity, covers through the cover pipeline, submission files stored privately and given public-safe derivatives and
/// thumbnails by the same processor that serves uploads, winners in the category-winner model. Everything created is
/// marked IsDemo (a demo cover on a real result: CoverIsDemo), so removal takes exactly that and nothing else.
/// <para>
/// Idempotent: users are found by e-mail, projects and competitions by slug (a real row with that slug stops the seed),
/// submissions by (demo participant, competition, category), winners by category, the result cover only where none
/// exists. Running it twice creates nothing the second time. It never publishes or unpublishes a result and never
/// changes the status of a competition it did not create.
/// </para>
/// </summary>
public sealed class DemoData(
    MeydanDbContext db,
    UserManager<ApplicationUser> users,
    IFileStorage storage,
    PublicDerivatives derivatives,
    PublicAssetProcessor processor,
    IFileScanner scanner,
    ReferenceNumberGenerator references,
    AuditTrail audit,
    IOptions<UploadOptions> uploads,
    TimeProvider clock,
    ILogger<DemoData> logger)
{
    public const string SeedKey = "demo-data-v1";

    public async Task<DemoReport> SeedAsync(string assetsFolder, CancellationToken ct)
    {
        var manifest = await DemoManifest.LoadAsync(assetsFolder, derivatives, ct);
        var chevron = await db.Competitions.AsNoTracking().Include(c => c.Categories).AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Slug == DbSeeder.ChevronCompetitionSlug, ct)
            ?? throw new InvalidOperationException("The Bakı chevron competition does not exist yet — run `migrate` first.");
        foreach (var s in DemoContent.Chevrons)
        {
            if (!manifest.Chevrons.ContainsKey(s.Settlement))
                throw new InvalidOperationException($"manifest.json has no chevron for {s.Settlement}");
            if (chevron.Categories.All(k => k.Name != s.Settlement))
                throw new InvalidOperationException($"{s.Settlement} is not one of the competition's categories");
        }

        DemoReport? report = null;
        var newFiles = new List<int>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var stored = new List<string>(); // written during this attempt: removed again if it fails
            db.ChangeTracker.Clear();
            newFiles.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                report = await SeedInsideAsync(manifest, chevron.Id, stored, newFiles, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                foreach (var key in stored) TryDelete(key);
                throw;
            }
        });

        // After the commit, exactly like a real final submission: public-safe derivative + thumbnail per file.
        foreach (var id in newFiles)
            await processor.ProcessAsync(id, ct);
        var failed = await db.SubmissionFiles.AsNoTracking()
            .CountAsync(f => newFiles.Contains(f.Id) && f.PublicAssetStatus != PublicAssetStatus.Ready, ct);
        if (failed > 0)
            report = report! with { Notes = [.. report.Notes, $"{failed} imported file(s) did not get a public-safe copy"] };
        logger.LogInformation(
            "Demo data seeded: {Projects} projects, {Competitions} competitions, {Users} participants, {Submissions} submissions, {Files} files, {Winners} winners",
            report!.ProjectsCreated, report.CompetitionsCreated, report.UsersCreated, report.SubmissionsCreated, report.FilesImported, report.WinnersCreated);
        return report!;
    }

    private async Task<DemoReport> SeedInsideAsync(
        DemoManifest manifest, int chevronId, List<string> stored, List<int> newFiles, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var notes = new List<string>();
        int projects = 0, competitions = 0, created = 0, submissions = 0, files = 0, winners = 0;

        // ---- People -----------------------------------------------------------------------------------------
        var people = new Dictionary<string, ApplicationUser>();
        foreach (var p in DemoContent.People)
        {
            var user = await users.FindByEmailAsync(p.Email);
            if (user is not null && !user.IsDemo)
                throw new InvalidOperationException($"{p.Email} belongs to a real account — the demo seed will not use it");
            if (user is null)
            {
                var joined = today.AddDays(-p.JoinedDaysAgo).AddHours(9 + p.JoinedDaysAgo % 8);
                user = new ApplicationUser
                {
                    UserName = p.Email,
                    Email = p.Email,
                    FirstName = p.FirstName,
                    LastName = p.LastName,
                    TermsAcceptedAt = joined,
                    CreatedAt = joined,
                    UpdatedAt = joined,
                    IsDemo = true,
                    // No password and locked: a demo account can never be signed into.
                    LockoutEnabled = true,
                    LockoutEnd = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
                };
                var result = await users.CreateAsync(user);
                if (!result.Succeeded)
                    throw new InvalidOperationException($"demo user {p.Email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
                await users.AddToRoleAsync(user, Roles.Participant);
                created++;
            }
            people[p.Key] = user;
        }

        // ---- Projects and competitions --------------------------------------------------------------------------
        var projectIds = new Dictionary<string, int>();
        foreach (var def in DemoContent.Projects)
        {
            var project = await db.Projects.FirstOrDefaultAsync(x => x.Slug == def.Slug, ct);
            if (project is not null && !project.IsDemo)
                throw new InvalidOperationException($"a real project already uses the slug \"{def.Slug}\"");
            if (project is null)
            {
                var cover = manifest.ProjectCovers.GetValueOrDefault(def.Name)
                            ?? throw new InvalidOperationException($"manifest.json has no cover for the project {def.Name}");
                project = new Project
                {
                    Name = def.Name,
                    Slug = def.Slug,
                    ShortDescription = def.ShortDescription,
                    Description = def.Description,
                    AccentColor = def.AccentColor,
                    Status = ProjectStatus.Published,
                    SortOrder = def.SortOrder,
                    IsDemo = true,
                    CoverImage = def.CoverTextFreeFrom > 0
                        ? await SaveCoverAsync(DemoArtwork.CropCover(cover.Bytes, def.CoverTextFreeFrom),
                            Path.ChangeExtension(Path.GetFileName(cover.File), ".png"), stored, ct)
                        : await SaveCoverAsync(cover.Bytes, Path.GetFileName(cover.File), stored, ct),
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync(ct);
                projects++;
            }
            projectIds[def.Slug] = project.Id;
        }

        var competitionIds = new Dictionary<string, int>();
        foreach (var def in DemoContent.Competitions)
        {
            var competition = await db.Competitions.FirstOrDefaultAsync(x => x.Slug == def.Slug, ct);
            if (competition is not null && !competition.IsDemo)
                throw new InvalidOperationException($"a real competition already uses the slug \"{def.Slug}\"");
            if (competition is null)
            {
                var cover = manifest.CompetitionCover(def.ManifestTitlePrefix)
                            ?? throw new InvalidOperationException($"manifest.json has no cover for {def.Title}");
                competition = new Competition
                {
                    ProjectId = projectIds[def.ProjectSlug],
                    Title = def.Title,
                    Slug = def.Slug,
                    ShortDescription = def.ShortDescription,
                    Description = def.Description,
                    Requirements = def.Requirements,
                    StartDate = today.AddDays(def.StartDay).AddHours(-4),        // 00:00 in Baku
                    EndDate = today.AddDays(def.EndDay).AddHours(19).AddMinutes(59), // 23:59 in Baku
                    Status = def.Phase == DemoContent.Phase.Completed ? CompetitionStatus.Closed : CompetitionStatus.Published,
                    HasCategories = false,
                    SortOrder = 1,
                    IsDemo = true,
                    CoverImage = await SaveCoverAsync(cover.Bytes, Path.GetFileName(cover.File), stored, ct),
                };
                AddFields(competition, def.FileFieldLabel);
                db.Competitions.Add(competition);
                await db.SaveChangesAsync(ct);
                competitions++;
            }
            competitionIds[def.Slug] = competition.Id;
        }

        // ---- Bakı chevron entries --------------------------------------------------------------------------------
        var chevronFields = await db.CompetitionFields.Where(f => f.CompetitionId == chevronId).ToListAsync(ct);
        var categories = await db.CompetitionCategories.Where(k => k.CompetitionId == chevronId).ToDictionaryAsync(k => k.Name, ct);
        foreach (var settlement in DemoContent.Chevrons)
        {
            var category = categories[settlement.Settlement];
            var artwork = DemoArtwork.Prepare(manifest.Chevrons[settlement.Settlement].Bytes);
            var fileName = Path.GetFileNameWithoutExtension(manifest.Chevrons[settlement.Settlement].File) + ".png";
            foreach (var entry in settlement.Entries)
            {
                var (sub, fileId) = await EnsureEntryAsync(people[entry.Person], chevronId, category.Id, chevronFields, entry,
                    entry.Status == SubmissionStatus.Draft ? null : (artwork.Png, fileName), today, stored, ct);
                if (sub is null) continue;
                submissions++;
                if (fileId is { } id) { newFiles.Add(id); files++; }
            }
        }

        // ---- Other demo competitions: text-only entries (no artwork was supplied for them) ---------------------
        foreach (var (slug, entries) in DemoContent.OtherEntries)
        {
            var competitionId = competitionIds[slug];
            var fields = await db.CompetitionFields.Where(f => f.CompetitionId == competitionId).ToListAsync(ct);
            foreach (var entry in entries)
            {
                var (sub, _) = await EnsureEntryAsync(people[entry.Person], competitionId, null, fields, entry, null, today, stored, ct);
                if (sub is not null) submissions++;
            }
        }

        // ---- Winners and the results banner: never into a result that is public right now ----------------------
        // Adding them to a published result would announce demo winners publicly at once. Entries (anonymous in the
        // gallery until they win) are fine; winners wait for an admin: unpublish, seed again, review, publish.
        var resultPublished = await db.CompetitionResults.AnyAsync(r => r.CompetitionId == chevronId && r.PublishedAt != null, ct);
        if (resultPublished)
            notes.Add("The Bakı result is published: demo winners and the results banner were not added (they would be public at once). "
                      + "To include them: unpublish the result, run seed-demo again, review, publish.");

        // One per settlement, never replacing a winner that already exists.
        foreach (var settlement in resultPublished ? Array.Empty<DemoContent.SettlementDef>() : DemoContent.Chevrons)
        {
            var winner = settlement.Entries.FirstOrDefault(e => e.Winner);
            if (winner is null) continue;
            var category = categories[settlement.Settlement];
            if (await db.CompetitionCategoryWinners.AnyAsync(w => w.CompetitionCategoryId == category.Id, ct))
            {
                var isDemo = await db.CompetitionCategoryWinners.AnyAsync(w => w.CompetitionCategoryId == category.Id && w.Submission.IsDemo, ct);
                if (!isDemo) notes.Add($"{settlement.Settlement}: a real winner already exists — left as it is");
                continue;
            }
            var userId = people[winner.Person].Id;
            var submissionId = await db.Submissions
                .Where(s => s.IsDemo && s.UserId == userId && s.CompetitionId == chevronId && s.CompetitionCategoryId == category.Id)
                .Select(s => s.Id).FirstAsync(ct);
            db.CompetitionCategoryWinners.Add(new CompetitionCategoryWinner
            {
                CompetitionId = chevronId,
                CompetitionCategoryId = category.Id,
                SubmissionId = submissionId,
                SelectedAt = today.AddDays(-1).AddHours(8),
            });
            winners++;
        }
        await db.SaveChangesAsync(ct);

        // The Bakı results banner: only where there is none.
        var resultCoverSet = false;
        if (manifest.ResultCover is { } resultCover && !resultPublished)
        {
            var result = await db.CompetitionResults.FirstOrDefaultAsync(r => r.CompetitionId == chevronId, ct);
            if (result is null)
            {
                result = new CompetitionResult { CompetitionId = chevronId, IsDemo = true, Announcement = DemoContent.ResultAnnouncement };
                db.CompetitionResults.Add(result);
            }
            if (string.IsNullOrWhiteSpace(result.CoverImage))
            {
                result.CoverImage = await SaveCoverAsync(resultCover.Bytes, Path.GetFileName(resultCover.File), stored, ct);
                result.CoverIsDemo = true;
                resultCoverSet = true;
            }
            else if (!result.CoverIsDemo)
            {
                notes.Add("The Bakı result already has an admin's cover — left as it is");
            }
            await db.SaveChangesAsync(ct);
        }

        var report = new DemoReport(projects, competitions, created, submissions, files, winners, resultCoverSet, notes);
        if (projects + competitions + created + submissions + winners > 0 || resultCoverSet)
            audit.Record(null, AuditActions.DemoSeeded, AuditEntities.Platform, SeedKey,
                $"projects {projects}, competitions {competitions}, users {created}, submissions {submissions}, files {files}, winners {winners}");
        if (!await db.SeedHistory.AnyAsync(h => h.Key == SeedKey, ct))
            db.SeedHistory.Add(new SeedHistory { Key = SeedKey, AppliedAt = now });
        await db.SaveChangesAsync(ct);
        return report;
    }

    /// <summary>
    /// A demo entry created the way a participant's is: answers with the question labels frozen at submission, the
    /// file stored privately after the same content checks as an upload, a reference number from the same counter,
    /// and a status history. Returns (null, null) when the entry already exists.
    /// </summary>
    private async Task<(Submission? Submission, int? FileId)> EnsureEntryAsync(
        ApplicationUser user, int competitionId, int? categoryId, List<CompetitionField> fields, DemoContent.EntryDef entry,
        (byte[] Bytes, string Name)? file, DateTimeOffset today, List<string> stored, CancellationToken ct)
    {
        if (await db.Submissions.AnyAsync(s => s.IsDemo && s.UserId == user.Id && s.CompetitionId == competitionId
                                               && s.CompetitionCategoryId == categoryId, ct))
            return (null, null);

        var submittedAt = today.AddDays(-entry.SubmittedDaysAgo).AddHours(7 + entry.SubmittedDaysAgo % 9).AddMinutes(entry.Title.Length % 50);
        var createdAt = submittedAt.AddHours(-26);
        var final = entry.Status != SubmissionStatus.Draft;
        var s = new Submission
        {
            UserId = user.Id,
            CompetitionId = competitionId,
            CompetitionCategoryId = categoryId,
            Title = entry.Title,
            Status = entry.Status,
            ReferenceNumber = final ? await references.NextAsync(ReferenceNumberGenerator.SubmissionPrefix, ct) : null,
            SubmittedAt = final ? submittedAt : null,
            CreatedAt = createdAt,
            UpdatedAt = final ? submittedAt : createdAt.AddHours(2),
            IsDemo = true,
        };

        string? Answer(string key) => key switch
        {
            "title" => entry.Title,
            "concept" => entry.Concept,
            "symbols" => entry.Symbols,
            "notes" => entry.Notes,
            _ => null,
        };
        foreach (var f in fields.Where(f => f.FieldType != CompetitionFieldType.File))
        {
            var value = Answer(f.Key);
            if (value is null) continue;
            s.Answers.Add(new SubmissionAnswer { CompetitionFieldId = f.Id, Value = value, FieldLabel = final ? f.Label : null });
        }

        if (file is { } upload)
        {
            var field = fields.First(f => f.FieldType == CompetitionFieldType.File);
            var type = FileTypes.DetectBySignature(upload.Bytes.AsSpan(0, Math.Min(upload.Bytes.Length, FileTypes.SignatureLength)))
                       ?? throw new InvalidOperationException($"{upload.Name}: unknown content");
            derivatives.Inspect(upload.Bytes);
            var scan = await scanner.ScanAsync(new MemoryStream(upload.Bytes, writable: false), type, ct);
            if (scan.Status == FileScanStatus.Infected)
                throw new InvalidOperationException($"{upload.Name} was rejected by the file scanner");
            var key = await storage.SaveAsync(new MemoryStream(upload.Bytes, writable: false), type.CanonicalExtension, ct);
            stored.Add(key);
            s.Files.Add(new SubmissionFile
            {
                CompetitionFieldId = field.Id,
                OriginalFileName = FileNames.Sanitize(upload.Name),
                StoragePath = key,
                ContentType = type.MimeType,
                SizeBytes = upload.Bytes.Length,
                SortOrder = 1,
                UploadedAt = createdAt.AddMinutes(30),
                ScanStatus = scan.Status,
                PublicAssetStatus = PublicAssetStatus.Pending,
                ThumbnailStatus = PublicAssetStatus.Pending,
            });
        }

        if (final)
        {
            s.StatusChanges.Add(new SubmissionStatusChange
            {
                FromStatus = SubmissionStatus.Draft, ToStatus = SubmissionStatus.Submitted, ChangedById = user.Id, ChangedAt = submittedAt,
            });
            if (entry.Status is not SubmissionStatus.Submitted)
                s.StatusChanges.Add(new SubmissionStatusChange
                {
                    FromStatus = SubmissionStatus.Submitted, ToStatus = entry.Status, ChangedAt = submittedAt.AddDays(2),
                    Comment = entry.ModerationComment,
                });
        }

        db.Submissions.Add(s);
        await db.SaveChangesAsync(ct);
        return (s, s.Files.FirstOrDefault()?.Id);
    }

    private static void AddFields(Competition c, string fileLabel)
    {
        var fileRules = JsonSerializer.Serialize(new { maxFiles = 3, maxFileSizeMb = 10, accept = new[] { "png", "jpeg", "webp", "pdf" } });
        c.Fields.Add(new CompetitionField
        {
            Key = "title", Label = "İşin adı", FieldType = CompetitionFieldType.ShortText, IsRequired = true,
            IsSubmissionTitle = true, IsPublicAfterResults = true, SortOrder = 1, OptionsJson = """{"maxLength":150}""",
        });
        c.Fields.Add(new CompetitionField
        {
            Key = "concept", Label = "İdeyanın izahı", FieldType = CompetitionFieldType.LongText, IsRequired = true,
            IsPublicAfterResults = true, SortOrder = 2,
        });
        c.Fields.Add(new CompetitionField
        {
            Key = "files", Label = fileLabel, FieldType = CompetitionFieldType.File, IsRequired = false,
            IsPublicAfterResults = true, SortOrder = 3, OptionsJson = fileRules,
        });
    }

    /// <summary>Through the admin cover pipeline: content checks, re-encoded metadata-free WEBP in the public covers area.</summary>
    private async Task<string> SaveCoverAsync(byte[] bytes, string fileName, List<string> stored, CancellationToken ct)
    {
        var url = await CoverImages.SaveAsync(bytes, fileName, uploads.Value, storage, derivatives, ct);
        stored.Add(url[CoverImages.UrlPrefix.Length..]);
        return url;
    }

    // ---- Removal --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Removes exactly what the demo seed created (IsDemo rows, a demo cover on a real result) and the files they own.
    /// A demo competition or project that meanwhile received real entries is kept and reported — real work is never
    /// deleted. Files are deleted from storage only after the database change has committed.
    /// </summary>
    public async Task<DemoRemovalReport> RemoveAsync(CancellationToken ct)
    {
        DemoRemovalReport? report = null;
        var toDelete = new List<string>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            toDelete.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            report = await RemoveInsideAsync(toDelete, ct);
            await tx.CommitAsync(ct);
        });
        var deleted = 0;
        foreach (var key in toDelete.Distinct())
            if (TryDelete(key)) deleted++;
        report = report! with { StoredFilesDeleted = deleted };
        logger.LogInformation(
            "Demo data removed: {Projects} projects, {Competitions} competitions, {Users} participants, {Submissions} submissions, {Files} stored files",
            report.ProjectsRemoved, report.CompetitionsRemoved, report.UsersRemoved, report.SubmissionsRemoved, report.StoredFilesDeleted);
        return report;
    }

    private async Task<DemoRemovalReport> RemoveInsideAsync(List<string> toDelete, CancellationToken ct)
    {
        var kept = new List<string>();

        // Demo entries (wherever they are) with their answers, files, notes and history; their wins first.
        var demoSubmissions = await db.Submissions.Where(s => s.IsDemo).Include(s => s.Files).ToListAsync(ct);
        var ids = demoSubmissions.Select(s => s.Id).ToList();
        var winners = await db.CompetitionCategoryWinners.Where(w => ids.Contains(w.SubmissionId)).ToListAsync(ct);
        db.CompetitionCategoryWinners.RemoveRange(winners);
        foreach (var r in await db.CompetitionResults
                     .Where(r => (r.OverallWinnerSubmissionId != null && ids.Contains(r.OverallWinnerSubmissionId.Value))
                                 || (r.SecondPlaceSubmissionId != null && ids.Contains(r.SecondPlaceSubmissionId.Value))
                                 || (r.ThirdPlaceSubmissionId != null && ids.Contains(r.ThirdPlaceSubmissionId.Value)))
                     .ToListAsync(ct))
        {
            for (var place = 1; place <= CompetitionResult.PlaceCount; place++)
                if (r.HolderOf(place) is { } held && ids.Contains(held))
                    r.SetPlace(place, null, null, null);
        }
        foreach (var f in demoSubmissions.SelectMany(s => s.Files))
            toDelete.AddRange(new[] { f.StoragePath, f.PublicAssetKey, f.ThumbnailKey }.OfType<string>());
        db.Submissions.RemoveRange(demoSubmissions);
        await db.SaveChangesAsync(ct);

        // Demo competitions, unless something real now lives in them.
        var competitions = 0;
        foreach (var c in await db.Competitions.Where(c => c.IsDemo).Include(c => c.Result).ToListAsync(ct))
        {
            if (await db.Submissions.AnyAsync(s => s.CompetitionId == c.Id, ct))
            {
                kept.Add($"competition \"{c.Title}\" has real entries — kept");
                continue;
            }
            Cover(c.CoverImage);
            Cover(c.Result?.CoverImage);
            db.Competitions.Remove(c);
            competitions++;
        }
        await db.SaveChangesAsync(ct);

        var projects = 0;
        foreach (var p in await db.Projects.Where(p => p.IsDemo).ToListAsync(ct))
        {
            if (await db.Competitions.AnyAsync(c => c.ProjectId == p.Id, ct))
            {
                kept.Add($"project \"{p.Name}\" still has competitions — kept");
                continue;
            }
            Cover(p.CoverImage);
            db.Projects.Remove(p);
            projects++;
        }

        // A demo cover on a real result goes; a result row the demo created goes if nothing real uses it.
        var resultCoverRemoved = false;
        foreach (var r in await db.CompetitionResults.Where(r => r.CoverIsDemo || r.IsDemo).ToListAsync(ct))
        {
            if (r.CoverIsDemo)
            {
                Cover(r.CoverImage);
                r.CoverImage = null;
                r.CoverIsDemo = false;
                resultCoverRemoved = true;
            }
            if (r.IsDemo)
            {
                var demoText = r.Announcement == DemoContent.ResultAnnouncement;
                var used = r.IsPublished || r.OverallWinnerSubmissionId is not null || r.CoverImage is not null
                           || (r.Announcement is not null && !demoText)
                           || await db.CompetitionCategoryWinners.AnyAsync(w => w.CompetitionId == r.CompetitionId, ct);
                if (used)
                {
                    // Real winners or an admin's publication keep the row; the demo's own text goes with the demo.
                    r.IsDemo = false;
                    if (demoText) r.Announcement = null;
                    kept.Add($"result of competition {r.CompetitionId} is in real use — kept");
                }
                else
                {
                    db.CompetitionResults.Remove(r);
                }
            }
        }
        await db.SaveChangesAsync(ct);

        var usersRemoved = 0;
        foreach (var u in await db.Users.Where(u => u.IsDemo).ToListAsync(ct))
        {
            if (await db.Submissions.AnyAsync(s => s.UserId == u.Id, ct))
            {
                kept.Add($"{u.Email} has real entries — kept");
                continue;
            }
            // A vote is never deleted, and the database would take it with the account (Votes.UserId cascades until
            // the D1 migration, Phase22): an account that voted stays, as in the admin's own deletion.
            if (await db.Votes.IgnoreQueryFilters().AnyAsync(v => v.UserId == u.Id, ct))
            {
                kept.Add($"{u.Email} has votes — kept");
                continue;
            }
            var result = await users.DeleteAsync(u);
            if (!result.Succeeded)
                throw new InvalidOperationException($"demo user {u.Email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            usersRemoved++;
        }

        db.SeedHistory.RemoveRange(await db.SeedHistory.Where(h => h.Key == SeedKey).ToListAsync(ct));
        audit.Record(null, AuditActions.DemoRemoved, AuditEntities.Platform, SeedKey,
            $"projects {projects}, competitions {competitions}, users {usersRemoved}, submissions {demoSubmissions.Count}, winners {winners.Count}");
        await db.SaveChangesAsync(ct);
        return new DemoRemovalReport(projects, competitions, usersRemoved, demoSubmissions.Count, 0, winners.Count, resultCoverRemoved, kept);

        void Cover(string? url)
        {
            if (url is not null && url.StartsWith(CoverImages.UrlPrefix, StringComparison.Ordinal))
                toDelete.Add(url[CoverImages.UrlPrefix.Length..]);
        }
    }

    private bool TryDelete(string key)
    {
        try
        {
            storage.Delete(key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning("Demo file {Key} could not be deleted: {Error}", key, ex.GetType().Name);
            return false;
        }
    }
}
