using Meydan.Api.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// Moderation of submitted work across all projects. Drafts are private to their owner and never appear here.
/// Files are opened through the shared, authorised file endpoint (/api/submissions/{id}/files/{fileId}).
/// </summary>
[Route("api/admin/submissions")]
public sealed class AdminSubmissionsController(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, ResultAdministration results, IFileStorage storage,
    PermanentDeletion deletion, PrintItems printItems, Voting.DocumentPages documentPages)
    : AdminControllerBase
{
    /// <summary>What would go with this entry, counted on the server for the confirmation dialog.</summary>
    [HttpGet("{id:int}/delete-impact")]
    public Task<DeleteImpactDto> DeleteImpact(int id, CancellationToken ct) => deletion.SubmissionImpactAsync(id, ct);

    /// <summary>
    /// Removes one entry for good: its answers, files, notes, history and any win it holds. The participant's
    /// account, their other entries and the competition all stay. There is no undo; the audit entry survives.
    /// </summary>
    [HttpDelete("{id:int}/permanent")]
    public async Task<DeleteReport> DeletePermanently(int id, CancellationToken ct) =>
        await deletion.DeleteSubmissionAsync(id, CurrentUserId, ct);

    /// <summary>Statuses an admin may set in Phase 3. Winner stays reserved for the future jury/results phase.</summary>
    public static readonly SubmissionStatus[] ModerationStatuses =
        [SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.Approved, SubmissionStatus.Rejected];

    [HttpGet]
    // The parameter must not be called "q": the binder would treat the ?q= search term as its prefix.
    public async Task<Paged<AdminSubmissionListItem>> List([FromQuery] AdminSubmissionQuery filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 100);
        var query = Filter(filter);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(ListItem)
            .ToListAsync(ct);
        items = await SubmissionImages.WithPreviewPagesAsync(items, db, documentPages, ct);
        return new Paged<AdminSubmissionListItem>(items, total, page, size);
    }

    /// <summary>Most works one print request returns; a larger selection is narrowed with the list's own filters.</summary>
    public const int PrintLimit = 300;

    /// <summary>
    /// The selection the list shows, as management prints it: one work per page or four. Same filters as the list;
    /// without a status filter only works that can still win are printed (submitted, under review, approved).
    /// Ordered by reference number. Never carries contact or registration data; the author's name only on request —
    /// the admin's print pages ask for it by default (internal documents, owner, 7 October 2026).
    /// </summary>
    /// <para>
    /// <c>finalists=true</c> prints the competition's frozen finalists only — the jury's sheets (D13). Still in
    /// reference order, never in the order of the public vote, and with nothing of the vote on them.
    /// </para>
    [HttpGet("print")]
    public async Task<AdminPrintDto> Print(
        [FromQuery] AdminSubmissionQuery filter, [FromQuery] bool includeAuthor, [FromQuery] bool finalists, CancellationToken ct)
    {
        var query = Filter(filter);
        if (finalists)
        {
            if (filter.CompetitionId is not { } competitionId)
                throw ValidationFailedException.For("competitionId", "Finalçıları çap etmək üçün müsabiqəni seçin.");
            if (!await db.CompetitionFinals.AnyAsync(f => f.CompetitionId == competitionId, ct))
                throw new ConflictException("Bu müsabiqənin finalçıları hələ təsdiqlənməyib.");
            query = query.Where(s => db.CompetitionFinalists.Any(x => x.SubmissionId == s.Id && x.CompetitionId == competitionId));
        }
        else if (filter.Status is null) query = query.Where(s => CompetitionResult.EligibleStatuses.Contains(s.Status));
        var total = await query.CountAsync(ct);

        var ids = await query
            .OrderBy(s => s.ReferenceNumber == null).ThenBy(s => s.ReferenceNumber).ThenBy(s => s.Id)
            .Take(PrintLimit)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var items = await printItems.ForAsync(ids, includeAuthor, ct);

        var competitionIds = await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => s.CompetitionId).Distinct().ToListAsync(ct);
        var competitionTitle = competitionIds.Count == 1
            ? await db.Competitions.AsNoTracking().Where(c => c.Id == competitionIds[0]).Select(c => c.Title).FirstAsync(ct)
            : null;
        return new AdminPrintDto(competitionTitle, total, PrintLimit, includeAuthor, items);
    }

    [HttpGet("{id:int}")]
    public async Task<AdminSubmissionDetail> Get(int id, CancellationToken ct)
    {
        var s = await db.Submissions.AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Competition).ThenInclude(c => c.Project)
            .Include(x => x.Competition).ThenInclude(c => c.Fields)
            .Include(x => x.Category)
            .Include(x => x.Answers)
            .Include(x => x.Files)
            .Include(x => x.Notes).ThenInclude(n => n.Author)
            .Include(x => x.StatusChanges).ThenInclude(h => h.ChangedBy)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id && x.Status != SubmissionStatus.Draft, ct)
            ?? throw new NotFoundException("Təklif tapılmadı.");

        var fields = s.Competition.Fields
            .Where(f => f.IsActive
                        || s.Answers.Any(a => a.CompetitionFieldId == f.Id && a.Value != null)
                        || s.Files.Any(x => x.CompetitionFieldId == f.Id))
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
            .ToList();

        var answers = fields
            .Where(f => f.FieldType != CompetitionFieldType.File)
            .Select(f =>
            {
                var answer = s.Answers.FirstOrDefault(a => a.CompetitionFieldId == f.Id);
                return new AdminAnswerDto(f.Id, f.Key, answer?.FieldLabel ?? f.Label, f.Label, f.FieldType, f.IsActive, answer?.Value);
            })
            .ToList();

        // Each PDF's page count, for the page that stands for it (SubmissionImages) and the admin's page picker.
        var pdfPages = await SubmissionImages.PdfPageCountsAsync(
            s.Files.Select(f => (new ImageCandidate(f.Id, f.ContentType, f.SortOrder, f.PublicAssetStatus, f.ThumbnailStatus,
                f.PublicAssetWidth, f.PublicAssetHeight, f.PreviewPage), f.StoragePath)),
            documentPages, ct);
        var files = s.Files.OrderBy(f => f.SortOrder).Select(f =>
        {
            var field = fields.FirstOrDefault(x => x.Id == f.CompetitionFieldId);
            var isPublic = field?.IsPublicAfterResults == true;
            var count = pdfPages.GetValueOrDefault(f.Id);
            return new AdminFileDto(f.Id, field?.Key, field?.Label, f.OriginalFileName, f.ContentType, f.SizeBytes, f.UploadedAt,
                isPublic, f.PublicAssetStatus, f.PublicAssetError, f.ScanStatus,
                isPublic && f.PublicAssetStatus == PublicAssetStatus.Ready && !f.HiddenFromPublic,
                f.ThumbnailStatus == PublicAssetStatus.Ready, f.HiddenFromPublic,
                count, f.PreviewPage, count > 0 ? SubmissionImages.RepresentativePage(count, f.PreviewPage) : 1,
                count > 0 ? SubmissionImages.RepresentativePage(count, null) : 1);
        }).ToList();
        var candidates = files.Where(f => f.CanBePreview).ToList();
        var effectivePreview = (candidates.FirstOrDefault(f => f.Id == s.PreviewFileId)
                                ?? candidates.FirstOrDefault(f => f.ContentType.StartsWith("image/", StringComparison.Ordinal))
                                ?? candidates.FirstOrDefault())?.Id;

        var u = s.User;
        return new AdminSubmissionDetail(
            s.Id, s.ReferenceNumber, s.Title, s.Status, s.CreatedAt, s.UpdatedAt, s.SubmittedAt,
            new AdminParticipantDto(u.Id, u.FirstName, u.LastName, u.Email, u.PhoneNumber, u.CreatedAt),
            new RefDto(s.Competition.Project.Id, s.Competition.Project.Name),
            new RefDto(s.Competition.Id, s.Competition.Title), s.Competition.Slug, s.Competition.CategoryLabel,
            s.Category is null ? null : new RefDto(s.Category.Id, s.Category.Name),
            answers, files,
            s.Notes.OrderByDescending(n => n.CreatedAt)
                .Select(n => new AdminNoteDto(n.Id, n.Text, $"{n.Author.FirstName} {n.Author.LastName}", n.CreatedAt))
                .ToList(),
            s.StatusChanges.OrderBy(h => h.ChangedAt)
                .Select(h => new AdminStatusChangeDto(h.FromStatus, h.ToStatus,
                    h.ChangedBy is null ? null : $"{h.ChangedBy.FirstName} {h.ChangedBy.LastName}", h.ChangedAt, h.Comment))
                .ToList(),
            ModerationStatuses.Where(x => x != s.Status).ToList(),
            await results.ForSubmissionAsync(s, ct),
            s.PreviewFileId,
            effectivePreview,
            s.IsDemo);
    }

    /// <summary>Moves a submission through moderation. Who and when is recorded in its history and the audit log.</summary>
    [HttpPut("{id:int}/status")]
    public async Task<AdminSubmissionDetail> ChangeStatus(int id, ChangeSubmissionStatusRequest req, CancellationToken ct)
    {
        if (!ModerationStatuses.Contains(req.Status))
            throw ValidationFailedException.For("status", "Bu status admin tərəfindən təyin edilə bilməz.");
        var s = await db.Submissions.FirstOrDefaultAsync(x => x.Id == id && x.Status != SubmissionStatus.Draft, ct)
                ?? throw new NotFoundException("Təklif tapılmadı.");
        if (s.Status == SubmissionStatus.Withdrawn)
            throw new ConflictException("Geri çəkilmiş təklifin statusu dəyişdirilə bilməz.");
        if (!CompetitionResult.IsEligible(req.Status) && await results.IsWinnerAsync(s.Id, ct))
            throw new ConflictException("Bu iş qalibdir. Onu rədd etmək üçün əvvəlcə qalibi dəyişin və ya ləğv edin.");

        if (s.Status != req.Status)
        {
            var now = clock.GetUtcNow();
            db.SubmissionStatusChanges.Add(new SubmissionStatusChange
            {
                SubmissionId = s.Id,
                FromStatus = s.Status,
                ToStatus = req.Status,
                ChangedById = CurrentUserId,
                ChangedAt = now,
                Comment = AdminSlugs.NullIfBlank(req.Comment),
            });
            audit.Record(CurrentUserId, AuditActions.StatusChanged, AuditEntities.Submission, s.Id, $"{s.Status} → {req.Status}");
            s.Status = req.Status;
            s.UpdatedAt = now;
            s.ConcurrencyStamp = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("Təklif eyni anda dəyişdirildi. Səhifəni yeniləyin.");
            }
        }
        return await Get(id, ct);
    }

    /// <summary>
    /// Chooses which file illustrates the work in the public gallery. Only a file of this very submission, in a
    /// public field, with a ready public-safe derivative is accepted — any other id is rejected.
    /// </summary>
    [HttpPut("{id:int}/preview")]
    public async Task<AdminSubmissionDetail> SetPreview(int id, SetPreviewRequest req, CancellationToken ct)
    {
        var s = await db.Submissions.FirstOrDefaultAsync(x => x.Id == id && x.Status != SubmissionStatus.Draft, ct)
                ?? throw new NotFoundException("Təklif tapılmadı.");
        if (req.FileId is { } fileId)
        {
            var file = await db.SubmissionFiles.AsNoTracking()
                .Where(f => f.Id == fileId && f.SubmissionId == id)
                .Select(f => new { f.PublicAssetStatus, f.HiddenFromPublic, IsPublic = f.Field != null && f.Field.IsPublicAfterResults })
                .FirstOrDefaultAsync(ct)
                ?? throw ValidationFailedException.For("fileId", "Bu fayl bu təklifə aid deyil.");
            if (!file.IsPublic)
                throw ValidationFailedException.For("fileId", "Bu faylın sahəsi nəticədən sonra ictimai deyil.");
            if (file.HiddenFromPublic)
                throw ValidationFailedException.For("fileId", "Bu fayl saytda gizlədilib — əvvəlcə onu yenidən göstərin.");
            if (file.PublicAssetStatus != PublicAssetStatus.Ready)
                throw ValidationFailedException.For("fileId", "Bu fayl üçün təhlükəsiz önizləmə hələ hazır deyil.");
        }
        s.PreviewFileId = req.FileId;
        audit.Record(CurrentUserId, AuditActions.PreviewChanged, AuditEntities.Submission, id,
            req.FileId is null ? "automatic" : $"file {req.FileId}");
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Chooses the page of a PDF that stands for the work on every admin screen and printed card — the jury's package,
    /// every print, the Final tab, the jury's rows, the final show, the vote ranking and the lists — or, with page null,
    /// gives it back to the automatic choice (SubmissionImages.RepresentativePage). Only the choice is stored: the PDF
    /// itself is never changed, and every page stays in the full material. Audited.
    /// </summary>
    [HttpPut("{id:int}/files/{fileId:int}/preview-page")]
    public async Task<AdminSubmissionDetail> SetPdfPreviewPage(int id, int fileId, SetPdfPreviewPageRequest req, CancellationToken ct)
    {
        var file = await db.SubmissionFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.SubmissionId == id && f.Submission.Status != SubmissionStatus.Draft, ct)
            ?? throw new NotFoundException("Fayl tapılmadı.");
        if (file.ContentType != "application/pdf")
            throw ValidationFailedException.For("page", "Önizləmə səhifəsi yalnız PDF üçün seçilir.");
        if (file.PublicAssetStatus != PublicAssetStatus.Ready)
            throw ValidationFailedException.For("page", "Bu PDF-in təhlükəsiz nüsxəsi hələ hazır deyil.");
        var count = await documentPages.CountAsync(file.StoragePath, ct);
        if (req.Page is { } chosen && (chosen < 1 || chosen > count))
            throw ValidationFailedException.For("page", $"Səhifə 1 ilə {count} arasında olmalıdır.");
        if (file.PreviewPage != req.Page)
        {
            string Describe(int? p) => p is { } x ? $"page {x}" : $"automatic (page {SubmissionImages.RepresentativePage(count, null)})";
            audit.Record(CurrentUserId, AuditActions.PdfPreviewPageChanged, AuditEntities.Submission, id,
                $"file {fileId}: {Describe(file.PreviewPage)} → {Describe(req.Page)} of {count}");
            file.PreviewPage = req.Page;
            await db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
    }

    /// <summary>
    /// Takes one file off every public page — the ballot, the work's own page, the gallery after results — or puts it
    /// back. The work stays in the competition with its other files, and nothing is deleted. Meant for a picture that
    /// gives its author away during an anonymous vote (D2), or anything else the public should not see. A hidden file
    /// cannot illustrate the work: if it was the chosen preview, the choice goes back to automatic. Audited.
    /// </summary>
    [HttpPut("{id:int}/files/{fileId:int}/visibility")]
    public async Task<AdminSubmissionDetail> SetFileVisibility(int id, int fileId, SetFileVisibilityRequest req, CancellationToken ct)
    {
        var file = await db.SubmissionFiles.Include(f => f.Submission)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.SubmissionId == id && f.Submission.Status != SubmissionStatus.Draft, ct)
            ?? throw new NotFoundException("Fayl tapılmadı.");
        if (file.HiddenFromPublic != req.Hidden)
        {
            file.HiddenFromPublic = req.Hidden;
            var note = "";
            if (req.Hidden && file.Submission.PreviewFileId == fileId)
            {
                file.Submission.PreviewFileId = null;
                note = "; preview back to automatic";
            }
            audit.Record(CurrentUserId, req.Hidden ? AuditActions.FileHidden : AuditActions.FileShown, AuditEntities.Submission, id,
                $"file {fileId}{note}");
            await db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
    }

    /// <summary>The metadata-free derivative exactly as the public will see it (admins only, before or after publication).</summary>
    /// <summary>
    /// One work as the admin's work viewer shows it — the same shape the printed cards use (PrintItems): reference,
    /// title, the author's name, nomination, the full public description, and the whole public material, every picture
    /// and every page of every PDF. Files of public fields only, never one hidden from the public; never a contact or
    /// registration detail, and nothing of the vote.
    /// </summary>
    [HttpGet("{id:int}/material")]
    public async Task<AdminPrintItemDto> Material(int id, CancellationToken ct) =>
        (await printItems.ForAsync([id], includeAuthor: true, ct)).FirstOrDefault()
        ?? throw new NotFoundException("Təklif tapılmadı.");

    /// <summary>
    /// A later page (2, 3, …) of a work's PDF as a metadata-free image, for the admin's printed appendix — rendered as
    /// the ballot renders it (DocumentPages). Page 1 is the file's own safe copy (/public). The PDF is never served.
    /// <c>thumb=true</c> gives the card-size copy, for a list's thumbnail.
    /// </summary>
    [HttpGet("{id:int}/files/{fileId:int}/pages/{page:int}")]
    public async Task<IActionResult> DocumentPage(int id, int fileId, int page, [FromQuery] bool thumb, CancellationToken ct)
    {
        var original = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == id && f.ContentType == "application/pdf")
            .Select(f => f.StoragePath)
            .FirstOrDefaultAsync(ct);
        if (original is null || page < 2 || page > await documentPages.CountAsync(original, ct))
            throw new NotFoundException("Səhifə tapılmadı.");
        Meydan.Infrastructure.Files.PublicImage image;
        try
        {
            image = await documentPages.PageAsync(original, page, thumbnail: thumb, ct);
        }
        catch (Meydan.Infrastructure.Files.DerivativeException)
        {
            throw new NotFoundException("Səhifə tapılmadı.");
        }
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        return File(image.Bytes, FileTypes.Webp.MimeType);
    }

    [HttpGet("{id:int}/files/{fileId:int}/public")]
    public async Task<IActionResult> PublicAsset(int id, int fileId, CancellationToken ct)
    {
        var key = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == id && f.PublicAssetStatus == PublicAssetStatus.Ready)
            .Select(f => f.PublicAssetKey)
            .FirstOrDefaultAsync(ct);
        if (key is null || !StorageAreas.IsIn(key, StorageAreas.Derived))
            throw new NotFoundException("Təhlükəsiz önizləmə hazır deyil.");
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        return File(storage.OpenRead(key), FileTypes.Webp.MimeType);
    }

    /// <summary>Internal note for staff. Participants never see notes.</summary>
    [HttpPost("{id:int}/notes")]
    public async Task<AdminSubmissionDetail> AddNote(int id, AddNoteRequest req, CancellationToken ct)
    {
        if (!await db.Submissions.AnyAsync(x => x.Id == id && x.Status != SubmissionStatus.Draft, ct))
            throw new NotFoundException("Təklif tapılmadı.");
        db.SubmissionNotes.Add(new SubmissionNote
        {
            SubmissionId = id,
            AuthorId = CurrentUserId,
            Text = req.Text.Trim(),
            CreatedAt = clock.GetUtcNow(),
        });
        audit.Record(CurrentUserId, AuditActions.NoteAdded, AuditEntities.Submission, id);
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    internal static System.Linq.Expressions.Expression<Func<Submission, AdminSubmissionListItem>> ListItem =>
        s => new AdminSubmissionListItem(
            s.Id, s.ReferenceNumber, s.Title, s.Status, s.SubmittedAt,
            new ParticipantRefDto(s.UserId, s.User.FirstName + " " + s.User.LastName, s.User.Email),
            new RefDto(s.Competition.ProjectId, s.Competition.Project.Name),
            new RefDto(s.CompetitionId, s.Competition.Title),
            s.Category == null ? null : new RefDto(s.Category.Id, s.Category.Name),
            s.Files.Count,
            s.CategoryWin != null
            || (s.Competition.Result != null && (s.Competition.Result.OverallWinnerSubmissionId == s.Id
                                                 || s.Competition.Result.SecondPlaceSubmissionId == s.Id
                                                 || s.Competition.Result.ThirdPlaceSubmissionId == s.Id)),
            s.Files
                .OrderBy(f => f.Id == s.PreviewFileId ? 0 : f.ContentType.StartsWith("image/") ? 1 : 2)
                .ThenBy(f => f.SortOrder).ThenBy(f => f.Id)
                .Select(f => new AdminPreviewDto(f.Id, f.ThumbnailStatus == PublicAssetStatus.Ready, 1))
                .FirstOrDefault(),
            s.IsDemo);

    private IQueryable<Submission> Filter(AdminSubmissionQuery q)
    {
        var query = db.Submissions.AsNoTracking().Where(s => s.Status != SubmissionStatus.Draft);
        if (q.ProjectId is { } projectId) query = query.Where(s => s.Competition.ProjectId == projectId);
        if (q.CompetitionId is { } competitionId) query = query.Where(s => s.CompetitionId == competitionId);
        if (q.CategoryId is { } categoryId) query = query.Where(s => s.CompetitionCategoryId == categoryId);
        if (q.Status is { } status) query = query.Where(s => s.Status == status);
        if (q.Demo is { } demo) query = query.Where(s => s.IsDemo == demo);
        if (q.Winner is { } winner)
            query = query.Where(s => (s.CategoryWin != null
                                      || (s.Competition.Result != null && (s.Competition.Result.OverallWinnerSubmissionId == s.Id
                                                                           || s.Competition.Result.SecondPlaceSubmissionId == s.Id
                                                                           || s.Competition.Result.ThirdPlaceSubmissionId == s.Id))) == winner);
        if (q.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            query = query.Where(s => s.SubmittedAt >= fromUtc);
        }
        if (q.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            query = query.Where(s => s.SubmittedAt <= toUtc);
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(s =>
                EF.Functions.ILike(s.ReferenceNumber ?? "", pattern)
                || EF.Functions.ILike(s.User.FirstName + " " + s.User.LastName, pattern)
                || EF.Functions.ILike(s.User.Email ?? "", pattern)
                || EF.Functions.ILike(s.Title, pattern));
        }
        return query;
    }
}
