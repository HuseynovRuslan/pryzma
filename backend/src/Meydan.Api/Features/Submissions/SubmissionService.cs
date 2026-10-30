using Meydan.Api.Common;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Api.Features.Auth;
using Meydan.Api.Features.Email;
using Meydan.Infrastructure.Email;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Submissions;

/// <summary>
/// Participant side of submissions: draft → answers and files → final submit. Every rule (ownership,
/// deadline, category, required fields, file checks, limits) is enforced here; the SPA only mirrors
/// them for a better experience.
/// </summary>
public sealed class SubmissionService(
    MeydanDbContext db,
    IFileStorage storage,
    ReferenceNumberGenerator references,
    IOptions<UploadOptions> uploadOptions,
    PublicDerivatives derivatives,
    IFileScanner scanner,
    IPublicAssetQueue publicAssets,
    IEmailOutbox outbox,
    TimeProvider clock,
    ILogger<SubmissionService> logger)
{
    private UploadOptions Uploads => uploadOptions.Value;

    // ---- Draft --------------------------------------------------------------------------------

    /// <summary>
    /// Resumes the participant's open draft for this competition + category, or starts one. Idempotent, so
    /// pressing "Təklif ver" twice (or coming back after login) never piles up empty drafts.
    /// </summary>
    public async Task<(SubmissionDto Dto, bool Created)> StartAsync(Guid userId, StartSubmissionRequest req, CancellationToken ct)
    {
        var competition = await LoadPublicCompetitionAsync(req.CompetitionSlug, ct);
        EnsureAccepting(competition);
        var category = ResolveCategory(competition, req.CategorySlug);

        var categoryId = category?.Id;
        var existing = await db.Submissions
            .Where(s => s.UserId == userId && s.CompetitionId == competition.Id
                        && s.CompetitionCategoryId == categoryId
                        && s.Status == SubmissionStatus.Draft)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return (await GetAsync(userId, existing.Value, ct), false);

        await EnsureBelowLimitAsync(userId, competition, ct);

        var now = clock.GetUtcNow();
        var submission = new Submission
        {
            UserId = userId,
            CompetitionId = competition.Id,
            CompetitionCategoryId = category?.Id,
            Status = SubmissionStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(userId, submission.Id, ct), true);
    }

    public async Task<SubmissionDto> GetAsync(Guid userId, int id, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: false, ct);
        return ToDto(s);
    }

    public async Task<SubmissionDto> SaveAnswersAsync(Guid userId, int id, SaveAnswersRequest req, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: true, ct);
        EnsureEditable(s);
        EnsureAccepting(s.Competition);

        var schemas = Schemas(s.Competition).ToDictionary(f => f.Key);
        var errors = new Dictionary<string, string[]>();
        foreach (var (key, raw) in req.Answers ?? [])
        {
            if (!schemas.TryGetValue(key, out var schema))
            {
                errors[key] = ["Bu sahə formada yoxdur."];
                continue;
            }
            if (schema.Type == CompetitionFieldType.File)
            {
                errors[key] = ["Fayllar ayrıca yüklənir."];
                continue;
            }
            var (value, error) = schema.Normalize(raw);
            if (error is not null)
            {
                errors[key] = [error];
                continue;
            }

            var answer = s.Answers.FirstOrDefault(a => a.CompetitionFieldId == schema.Field.Id);
            if (answer is null)
                s.Answers.Add(new SubmissionAnswer { CompetitionFieldId = schema.Field.Id, Value = value });
            else
                answer.Value = value;

            if (schema.Field.IsSubmissionTitle)
                s.Title = value ?? "";
        }
        if (errors.Count > 0)
            throw new ValidationFailedException(errors);

        Touch(s);
        await SaveAsync(ct);
        return ToDto(s);
    }

    // ---- Files --------------------------------------------------------------------------------

    public async Task<SubmissionFileDto> UploadAsync(Guid userId, int id, string? fieldKey, IFormFile? file, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: false, ct);
        EnsureEditable(s);
        EnsureAccepting(s.Competition);

        var schema = Schemas(s.Competition).FirstOrDefault(f => f.Key == fieldKey && f.Type == CompetitionFieldType.File)
                     ?? throw ValidationFailedException.For(fieldKey ?? "", "Bu sahəyə fayl yüklənmir.");
        var rules = schema.Files!;
        var key = schema.Key;

        if (file is null || file.Length == 0)
            throw ValidationFailedException.For(key, "Fayl boşdur və ya seçilməyib.");
        var name = FileNames.Sanitize(file.FileName);
        if (file.Length > rules.MaxFileSizeBytes)
            throw ValidationFailedException.For(key,
                $"“{name}” çox böyükdür. Hər fayl ən çox {rules.MaxFileSizeBytes / 1024 / 1024} MB ola bilər.");
        EnsureRoomForFile(s, schema, rules, key);

        var accepted = string.Join(", ", rules.Accept.Select(t => t.Label));
        var byExtension = FileTypes.ByExtension(Path.GetExtension(name));
        if (byExtension is null || !rules.Accept.Contains(byExtension))
            throw ValidationFailedException.For(key, $"“{name}” qəbul olunmur. İcazə verilən formatlar: {accepted}.");

        // The name and the browser's declared type are hints; the bytes decide.
        await using var stream = file.OpenReadStream();
        var header = new byte[FileTypes.SignatureLength];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var detected = FileTypes.DetectBySignature(header.AsSpan(0, read));
        if (detected != byExtension)
            throw ValidationFailedException.For(key, $"“{name}” faylının məzmunu {byExtension.Label} formatına uyğun deyil.");

        var declared = (file.ContentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (declared is not ("" or "application/octet-stream") && !detected.AcceptedDeclaredMimeTypes.Contains(declared))
            throw ValidationFailedException.For(key, $"“{name}” faylının növü ({declared}) formatı ilə uyğun gəlmir.");

        // ≤ MaxFileSizeMb, already bounded by the request limit: read once, check, store.
        stream.Position = 0;
        using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        // Images must decode as what they claim and stay within the pixel budget (decompression bombs are refused
        // from the header, before any decoding).
        if (PublicDerivatives.RasterTypes.Contains(detected))
        {
            try { derivatives.Inspect(bytes); }
            catch (DerivativeException ex) { throw ValidationFailedException.For(key, $"“{name}”: {ex.Message}"); }
        }

        var scan = await scanner.ScanAsync(new MemoryStream(bytes, writable: false), detected, ct);
        if (scan.Status == FileScanStatus.Infected)
        {
            logger.LogWarning("Upload rejected by the file scanner for submission {SubmissionId}", id);
            throw ValidationFailedException.For(key, $"“{name}” təhlükəsizlik yoxlamasından keçmədi.");
        }

        var storageKey = await storage.SaveAsync(new MemoryStream(bytes, writable: false), detected.CanonicalExtension, ct);
        try
        {
            // Several files are usually uploaded in parallel. Each attempt re-reads the submission, re-checks
            // the limits and inserts; a concurrent writer only causes a retry, never a lost or extra file.
            for (var attempt = 1; ; attempt++)
            {
                db.ChangeTracker.Clear();
                var current = await LoadOwnedAsync(userId, id, tracked: true, ct);
                EnsureEditable(current);
                EnsureRoomForFile(current, schema, rules, key);

                var entity = new SubmissionFile
                {
                    CompetitionFieldId = schema.Field.Id,
                    OriginalFileName = name,
                    StoragePath = storageKey,
                    ContentType = detected.MimeType,
                    SizeBytes = file.Length,
                    ScanStatus = scan.Status,
                    SortOrder = current.Files.Count == 0 ? 1 : current.Files.Max(f => f.SortOrder) + 1,
                    UploadedAt = clock.GetUtcNow(),
                };
                current.Files.Add(entity);
                Touch(current);
                try
                {
                    await db.SaveChangesAsync(ct);
                    return ToFileDto(entity, key);
                }
                catch (DbUpdateConcurrencyException) when (attempt < 5)
                {
                    await Task.Delay(Random.Shared.Next(10, 60), ct);
                }
            }
        }
        catch
        {
            storage.Delete(storageKey); // no orphan on disk if the row was not written
            throw;
        }
    }

    private void EnsureRoomForFile(Submission s, FieldSchema schema, FileRules rules, string key)
    {
        if (s.Files.Count(f => f.CompetitionFieldId == schema.Field.Id) >= rules.MaxFiles)
            throw ValidationFailedException.For(key, $"Bu sahəyə ən çox {rules.MaxFiles} fayl əlavə etmək olar.");
        if (s.Files.Count >= Uploads.MaxFilesPerSubmission)
            throw ValidationFailedException.For(key, $"Bir təklifə ən çox {Uploads.MaxFilesPerSubmission} fayl əlavə etmək olar.");
    }

    public async Task DeleteFileAsync(Guid userId, int id, int fileId, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: true, ct);
        EnsureEditable(s);
        var file = s.Files.FirstOrDefault(f => f.Id == fileId) ?? throw new NotFoundException("Fayl tapılmadı.");
        s.Files.Remove(file);
        db.SubmissionFiles.Remove(file);
        Touch(s);
        await SaveAsync(ct);
        try
        {
            storage.Delete(file.StoragePath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Stored file {FileId} could not be deleted from disk", file.Id);
        }
    }

    /// <summary>Owner of the submission, or an Admin for anything already submitted.</summary>
    public async Task<(Stream Content, SubmissionFile File)> OpenFileAsync(Guid userId, bool isAdmin, int id, int fileId, CancellationToken ct)
    {
        var file = await db.SubmissionFiles.AsNoTracking()
            .Include(f => f.Submission)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.SubmissionId == id, ct)
            ?? throw new NotFoundException("Fayl tapılmadı.");
        var allowed = file.Submission.UserId == userId
                      || (isAdmin && file.Submission.Status != SubmissionStatus.Draft);
        if (!allowed)
            throw new NotFoundException("Fayl tapılmadı.");
        return (storage.OpenRead(file.StoragePath), file);
    }

    /// <summary>The card-size copy of a file, under the same access rule as <see cref="OpenFileAsync"/>.</summary>
    public async Task<(Stream Content, string ETag)> OpenThumbnailAsync(Guid userId, bool isAdmin, int id, int fileId, CancellationToken ct)
    {
        var file = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == id)
            .Select(f => new { f.Submission.UserId, f.Submission.Status, f.ThumbnailKey, f.ThumbnailStatus })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Fayl tapılmadı.");
        var allowed = file.UserId == userId || (isAdmin && file.Status != SubmissionStatus.Draft);
        if (!allowed || file.ThumbnailStatus != PublicAssetStatus.Ready || file.ThumbnailKey is not { } key
            || !StorageAreas.IsIn(key, StorageAreas.Derived))
            throw new NotFoundException("Fayl tapılmadı.");
        try
        {
            return (storage.OpenRead(key), Results.PublicResults.ETagOf(key));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new NotFoundException("Fayl tapılmadı.");
        }
    }

    // ---- Final submission ---------------------------------------------------------------------

    /// <summary>Runs every final-submission rule without submitting; used by the preview step.</summary>
    public async Task<SubmissionDto> CheckAsync(Guid userId, int id, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: false, ct);
        EnsureEditable(s);
        EnsureAccepting(s.Competition);
        ValidateComplete(s);
        return ToDto(s);
    }

    public async Task<SubmissionDto> SubmitAsync(Guid userId, int id, CancellationToken ct)
    {
        var s = await LoadOwnedAsync(userId, id, tracked: true, ct);
        EnsureEditable(s);
        EnsureAccepting(s.Competition);
        ResolveCategory(s.Competition, s.Category?.Slug);
        ValidateComplete(s);
        LinkNomination(s);
        await EnsureBelowLimitAsync(userId, s.Competition, ct);

        // Minted before the submission row changes, so the counter save carries nothing else.
        var reference = await references.NextAsync(ReferenceNumberGenerator.SubmissionPrefix, ct);
        var now = clock.GetUtcNow();

        // Freeze the questions as they were asked: later renames by an admin never rewrite submitted answers.
        foreach (var answer in s.Answers)
            answer.FieldLabel = s.Competition.Fields.FirstOrDefault(f => f.Id == answer.CompetitionFieldId)?.Label;

        s.StatusChanges.Add(new SubmissionStatusChange
        {
            FromStatus = SubmissionStatus.Draft,
            ToStatus = SubmissionStatus.Submitted,
            ChangedById = userId,
            ChangedAt = now,
        });
        s.Status = SubmissionStatus.Submitted;
        s.ReferenceNumber = reference;
        s.SubmittedAt = now;
        Touch(s);
        await SaveAsync(ct);
        logger.LogInformation("Submission {SubmissionId} submitted as {Reference}", s.Id, reference);

        // Everything below is after the commit: the work is saved whatever happens to derivatives or e-mail.
        foreach (var f in s.Files) publicAssets.Enqueue(f.Id);
        await QueueReceiptAsync(s, reference);
        return ToDto(s);
    }

    /// <summary>
    /// "Təklifiniz qəbul edildi" e-mail as a durable outbox row, written after the submission has been committed —
    /// if queuing fails the submission stands and only a log line records it.
    /// </summary>
    private async Task QueueReceiptAsync(Submission s, string reference)
    {
        try
        {
            await outbox.EnqueueAsync(EmailTemplates.SubmissionReceivedKind, s.UserId, s.Id, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Receipt e-mail for {Reference} not queued: {ErrorType}", reference, ex.GetType().Name);
        }
    }

    // ---- Rules --------------------------------------------------------------------------------

    private void EnsureAccepting(Competition competition)
    {
        if (!competition.IsAcceptingSubmissions(clock.GetUtcNow()))
            throw new ConflictException("Bu müsabiqəyə təklif qəbulu bağlıdır.");
    }

    private static void EnsureEditable(Submission s)
    {
        if (!s.IsEditableByParticipant)
            throw new ConflictException("Bu təklif artıq təqdim olunub və dəyişdirilə bilməz.");
    }

    private static CompetitionCategory? ResolveCategory(Competition competition, string? slug)
    {
        var active = competition.Categories.Where(c => c.IsActive).ToList();
        if (!competition.HasCategories || active.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(slug))
                throw ValidationFailedException.For("category", "Bu müsabiqədə kateqoriya seçimi yoxdur.");
            return null;
        }
        var label = (competition.CategoryLabel ?? "Kateqoriyalar").ToLower(new System.Globalization.CultureInfo("az"));
        if (string.IsNullOrWhiteSpace(slug))
            throw ValidationFailedException.For("category", $"İştirak üçün {label} siyahısından birini seçin.");
        return active.FirstOrDefault(c => c.Slug == slug)
               ?? throw ValidationFailedException.For("category", $"Seçilən dəyər {label} siyahısında yoxdur.");
    }

    /// <summary>
    /// Places the work in its nomination when the entry form asks for one as an ordinary question rather than
    /// through the category picker — which is how the settlement competitions ask it, and why
    /// <c>HasCategories</c> stays false there: participants are not made to answer the same question twice.
    ///
    /// <para>
    /// The rule is one exact comparison: a Select answer whose text equals the name of exactly one switched-on
    /// category of this competition. Nothing is guessed. An answer that matches nothing, or matches two different
    /// categories, leaves the work unplaced — it keeps every answer it gave and simply does not appear on a
    /// ballot, which is the honest outcome and one an admin can correct by hand.
    /// </para>
    /// <para>The participant is asked nothing new, and a work already placed by the category picker is left alone.</para>
    /// </summary>
    private static void LinkNomination(Submission s)
    {
        if (s.CompetitionCategoryId is not null) return;
        var categories = s.Competition.Categories.Where(c => c.IsActive).ToList();
        if (categories.Count == 0) return;

        var matched = s.Competition.Fields
            .Where(f => f.FieldType == CompetitionFieldType.Select)
            .Select(f => s.Answers.FirstOrDefault(a => a.CompetitionFieldId == f.Id)?.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .SelectMany(value => categories.Where(c => string.Equals(c.Name, value, StringComparison.Ordinal)))
            .Distinct()
            .ToList();
        if (matched.Count == 1) s.CompetitionCategoryId = matched[0].Id;
    }

    private async Task EnsureBelowLimitAsync(Guid userId, Competition competition, CancellationToken ct)
    {
        if (competition.MaxSubmissionsPerUser is not { } max)
            return;
        var used = await db.Submissions.CountAsync(x => x.UserId == userId && x.CompetitionId == competition.Id
            && x.Status != SubmissionStatus.Draft && x.Status != SubmissionStatus.Withdrawn, ct);
        if (used >= max)
            throw new ConflictException($"Bu müsabiqəyə bir iştirakçı ən çox {max} təklif göndərə bilər.");
    }

    private void ValidateComplete(Submission s)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var schema in Schemas(s.Competition))
        {
            var value = s.Answers.FirstOrDefault(a => a.CompetitionFieldId == schema.Field.Id)?.Value;
            var fileCount = s.Files.Count(f => f.CompetitionFieldId == schema.Field.Id);
            if (schema.RequiredError(value, fileCount) is { } required)
            {
                errors[schema.Key] = [required];
                continue;
            }
            // Re-check stored values against the current field definition (an admin may have changed it).
            if (schema.Type != CompetitionFieldType.File && value is not null && schema.Normalize(value).Error is { } invalid)
                errors[schema.Key] = [invalid];
        }
        if (errors.Count > 0)
            throw new ValidationFailedException(errors);
    }

    // ---- Loading & mapping --------------------------------------------------------------------

    private async Task<Competition> LoadPublicCompetitionAsync(string slug, CancellationToken ct)
    {
        var competition = await db.Competitions
            .Include(c => c.Project)
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (competition is null
            || !Competition.IsPubliclyVisible(competition.Status)
            || !Project.IsPubliclyVisible(competition.Project.Status))
            throw new NotFoundException("Belə müsabiqə tapılmadı.");
        return competition;
    }

    /// <summary>Someone else's submission is reported as not found — its existence is not revealed.</summary>
    private async Task<Submission> LoadOwnedAsync(Guid userId, int id, bool tracked, CancellationToken ct)
    {
        var query = db.Submissions
            .Include(s => s.Competition).ThenInclude(c => c.Project)
            .Include(s => s.Competition).ThenInclude(c => c.Categories)
            .Include(s => s.Competition).ThenInclude(c => c.Fields)
            .Include(s => s.Category)
            .Include(s => s.Answers)
            .Include(s => s.Files)
            .AsSplitQuery();
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct)
               ?? throw new NotFoundException("Təklif tapılmadı.");
    }

    /// <summary>The live form: active fields only (disabled ones vanish from new and draft work).</summary>
    private IEnumerable<FieldSchema> Schemas(Competition c) =>
        c.Fields.Where(f => f.IsActive).OrderBy(f => f.SortOrder).ThenBy(f => f.Id).Select(f => FieldSchema.From(f, Uploads));

    /// <summary>
    /// What to show for a submission: drafts see the live form; submitted work keeps every field it answered,
    /// including fields disabled since.
    /// </summary>
    private IEnumerable<FieldSchema> SchemasFor(Submission s) =>
        s.IsEditableByParticipant
            ? Schemas(s.Competition)
            : s.Competition.Fields
                .Where(f => f.IsActive
                            || s.Answers.Any(a => a.CompetitionFieldId == f.Id && a.Value != null)
                            || s.Files.Any(x => x.CompetitionFieldId == f.Id))
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                .Select(f => FieldSchema.From(f, Uploads));

    private void Touch(Submission s)
    {
        s.UpdatedAt = clock.GetUtcNow();
        s.ConcurrencyStamp = Guid.NewGuid();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Təklif eyni anda başqa pəncərədə dəyişdirildi. Səhifəni yeniləyin.");
        }
    }

    private SubmissionDto ToDto(Submission s)
    {
        var schemas = SchemasFor(s).ToList();
        var frozenLabels = s.Answers
            .Where(a => a.FieldLabel != null)
            .ToDictionary(a => a.CompetitionFieldId, a => a.FieldLabel!);
        var fieldKeys = schemas.ToDictionary(f => f.Field.Id, f => f.Key);
        var c = s.Competition;
        return new SubmissionDto(
            s.Id, s.Status, s.ReferenceNumber, s.Title, s.IsEditableByParticipant,
            s.CreatedAt, s.UpdatedAt, s.SubmittedAt,
            new ProjectRefDto(c.Project.Slug, c.Project.Name, c.Project.AccentColor),
            new SubmissionCompetitionDto(c.Slug, c.Title, CompetitionPhases.Of(c, clock.GetUtcNow()),
                c.IsAcceptingSubmissions(clock.GetUtcNow()), c.HasCategories, c.CategoryLabel),
            s.Category is null ? null : new SubmissionCategoryDto(s.Category.Slug, s.Category.Name),
            schemas.Select(f => frozenLabels.TryGetValue(f.Field.Id, out var label)
                ? ToFieldDto(f) with { Label = label }
                : ToFieldDto(f)).ToList(),
            schemas.Where(f => f.Type != CompetitionFieldType.File)
                .ToDictionary(f => f.Key, f => s.Answers.FirstOrDefault(a => a.CompetitionFieldId == f.Field.Id)?.Value),
            s.Files.OrderBy(f => f.SortOrder)
                .Select(f => ToFileDto(f, f.CompetitionFieldId is { } fid && fieldKeys.TryGetValue(fid, out var k) ? k : ""))
                .ToList());
    }

    public static FieldDto ToFieldDto(FieldSchema f) => new(
        f.Field.Id, f.Key, f.Label, f.Type, f.Field.Placeholder, f.Field.HelpText, f.Field.IsRequired, f.Field.SortOrder,
        f.Type == CompetitionFieldType.Select ? f.Options : null,
        f.Type is CompetitionFieldType.ShortText or CompetitionFieldType.LongText ? f.MaxLength : null,
        f.Files is null
            ? null
            : new FileRulesDto(f.Files.MaxFiles, f.Files.MaxFileSizeBytes,
                f.Files.Accept.Select(t => new AcceptedFileTypeDto(t.Key, t.Label, t.MimeType, t.Extensions)).ToList()));

    private static SubmissionFileDto ToFileDto(SubmissionFile f, string fieldKey) =>
        new(f.Id, fieldKey, f.OriginalFileName, f.ContentType, f.SizeBytes, f.UploadedAt);
}
