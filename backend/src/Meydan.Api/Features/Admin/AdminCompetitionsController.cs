using System.Globalization;
using Meydan.Api.Common;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Admin;

[Route("api/admin/competitions")]
public sealed class AdminCompetitionsController(
    MeydanDbContext db,
    AuditTrail audit,
    TimeProvider clock,
    IFileStorage storage,
    PublicDerivatives derivatives,
    PermanentDeletion deletion,
    IOptions<UploadOptions> uploads) : AdminControllerBase
{
    private static readonly CultureInfo Az = new("az");

    [HttpGet]
    public async Task<IReadOnlyList<AdminCompetitionDto>> List([FromQuery] int? projectId, CancellationToken ct)
    {
        var query = db.Competitions.AsNoTracking();
        if (projectId is not null)
            query = query.Where(c => c.ProjectId == projectId);
        var rows = await Project(query.OrderBy(c => c.Project.SortOrder).ThenBy(c => c.SortOrder).ThenBy(c => c.Title))
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<AdminCompetitionDto> Get(int id, CancellationToken ct)
    {
        var row = await Project(db.Competitions.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        return ToDto(row);
    }

    /// <summary>New competitions always start as drafts.</summary>
    [HttpPost]
    public async Task<ActionResult<AdminCompetitionDto>> Create(CompetitionUpsertRequest req, CancellationToken ct)
    {
        var competition = new Competition { Status = CompetitionStatus.Draft };
        await ApplyAsync(competition, req, ct);
        db.Competitions.Add(competition);
        await db.SaveChangesAsync(ct);
        audit.Record(CurrentUserId, AuditActions.Created, AuditEntities.Competition, competition.Id, competition.Slug);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = competition.Id }, await Get(competition.Id, ct));
    }

    [HttpPut("{id:int}")]
    public async Task<AdminCompetitionDto> Update(int id, CompetitionUpsertRequest req, CancellationToken ct)
    {
        var competition = await FindAsync(id, ct);
        await ApplyAsync(competition, req, ct);
        audit.Record(CurrentUserId, AuditActions.Updated, AuditEntities.Competition, id, competition.Slug);
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Draft → Published → Closed → Archived (and back). Publishing checks the competition can actually take
    /// part: an active form, and categories when categories are switched on.
    /// </summary>
    [HttpPut("{id:int}/status")]
    public async Task<AdminCompetitionDto> ChangeStatus(int id, StatusRequest<CompetitionStatus> req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Status))
            throw ValidationFailedException.For("status", "Status düzgün deyil.");
        var competition = await db.Competitions
            .Include(c => c.Fields)
            .Include(c => c.Categories)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Müsabiqə tapılmadı.");

        if (req.Status == CompetitionStatus.Published)
        {
            var problems = new List<string>();
            if (!competition.Fields.Any(f => f.IsActive))
                problems.Add("Təqdimat formasında ən azı bir aktiv sahə olmalıdır.");
            if (competition.HasCategories && !competition.Categories.Any(c => c.IsActive))
                problems.Add("Kateqoriyalar açıqdır, amma aktiv kateqoriya yoxdur.");
            if (problems.Count > 0)
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["status"] = problems.ToArray() });
        }

        if (competition.Status != req.Status)
        {
            var action = req.Status switch
            {
                CompetitionStatus.Published => AuditActions.Published,
                CompetitionStatus.Closed => AuditActions.Closed,
                CompetitionStatus.Archived => AuditActions.Archived,
                _ => AuditActions.Unpublished,
            };
            audit.Record(CurrentUserId, action, AuditEntities.Competition, id, $"{competition.Status} → {req.Status}");
            competition.Status = req.Status;
            await db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
    }

    /// <summary>Only before anyone has started a submission (drafts included); otherwise archive it.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var competition = await FindAsync(id, ct);
        if (await db.Submissions.AnyAsync(s => s.CompetitionId == id, ct))
            throw new ConflictException("Bu müsabiqəyə artıq təklif başlanıb — silmək olmaz. Onu arxivləşdirin.");
        db.Competitions.Remove(competition);
        audit.Record(CurrentUserId, AuditActions.Deleted, AuditEntities.Competition, id, competition.Slug);
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(competition.CoverImage, storage);
        return NoContent();
    }

    /// <summary>What would go with this competition, counted on the server for the confirmation dialog.</summary>
    [HttpGet("{id:int}/delete-impact")]
    public Task<DeleteImpactDto> DeleteImpact(int id, CancellationToken ct) => deletion.CompetitionImpactAsync(id, ct);

    /// <summary>
    /// Removes the competition with its entries, categories, form fields and results. The project it belongs to
    /// stays, as do the participants' accounts. There is no undo; the audit entry survives.
    /// </summary>
    [HttpDelete("{id:int}/permanent")]
    public async Task<DeleteReport> DeletePermanently(int id, CancellationToken ct) =>
        await deletion.DeleteCompetitionAsync(id, CurrentUserId, ct);

    /// <summary>The competition's own cover (a poster), stored like project covers: re-encoded, metadata-free WEBP.</summary>
    [HttpPost("{id:int}/cover")]
    [UploadBodyLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<AdminCompetitionDto> UploadCover(int id, CancellationToken ct)
    {
        var (_, file) = await UploadForms.ReadSingleFileAsync(Request, "cover", uploads.Value, ct);
        var competition = await FindAsync(id, ct);
        var url = await CoverImages.SaveAsync(file, uploads.Value, storage, derivatives, ct);
        var previous = competition.CoverImage;
        competition.CoverImage = url;
        audit.Record(CurrentUserId, AuditActions.CoverChanged, AuditEntities.Competition, id);
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return await Get(id, ct);
    }

    [HttpDelete("{id:int}/cover")]
    public async Task<AdminCompetitionDto> RemoveCover(int id, CancellationToken ct)
    {
        var competition = await FindAsync(id, ct);
        var previous = competition.CoverImage;
        competition.CoverImage = null;
        audit.Record(CurrentUserId, AuditActions.CoverChanged, AuditEntities.Competition, id, "removed");
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return await Get(id, ct);
    }

    // ---- Categories -------------------------------------------------------------------------

    [HttpGet("{id:int}/categories")]
    public async Task<IReadOnlyList<AdminCategoryDto>> Categories(int id, CancellationToken ct)
    {
        await EnsureCompetitionAsync(id, ct);
        return await db.CompetitionCategories.AsNoTracking()
            .Where(c => c.CompetitionId == id)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new AdminCategoryDto(c.Id, c.CompetitionId, c.Name, c.Slug, c.Description, c.SortOrder,
                c.IsActive, c.Submissions.Count(s => s.Status != SubmissionStatus.Draft)))
            .ToListAsync(ct);
    }

    [HttpPost("{id:int}/categories")]
    public async Task<ActionResult<AdminCategoryDto>> CreateCategory(int id, CategoryUpsertRequest req, CancellationToken ct)
    {
        await EnsureCompetitionAsync(id, ct);
        var nextOrder = await db.CompetitionCategories.Where(c => c.CompetitionId == id).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
        var category = new CompetitionCategory { CompetitionId = id, SortOrder = nextOrder + 1 };
        await ApplyCategoryAsync(category, req, ct);
        db.CompetitionCategories.Add(category);
        await db.SaveChangesAsync(ct);
        audit.Record(CurrentUserId, AuditActions.Created, AuditEntities.Category, category.Id, $"competition {id}");
        await db.SaveChangesAsync(ct);
        return Created($"/api/admin/competitions/{id}/categories/{category.Id}", ToDto(category, 0));
    }

    /// <summary>Rename, describe, or switch a category on/off. Deactivating hides it from new submissions only.</summary>
    [HttpPut("{id:int}/categories/{categoryId:int}")]
    public async Task<AdminCategoryDto> UpdateCategory(int id, int categoryId, CategoryUpsertRequest req, CancellationToken ct)
    {
        var category = await db.CompetitionCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.CompetitionId == id, ct)
                       ?? throw new NotFoundException("Kateqoriya tapılmadı.");
        await ApplyCategoryAsync(category, req, ct);
        audit.Record(CurrentUserId, AuditActions.Updated, AuditEntities.Category, categoryId, $"competition {id}");
        await db.SaveChangesAsync(ct);
        var used = await db.Submissions.CountAsync(s => s.CompetitionCategoryId == categoryId && s.Status != SubmissionStatus.Draft, ct);
        return ToDto(category, used);
    }

    /// <summary>Only categories nobody has chosen yet (drafts included) can be deleted; others are deactivated.</summary>
    [HttpDelete("{id:int}/categories/{categoryId:int}")]
    public async Task<IActionResult> DeleteCategory(int id, int categoryId, CancellationToken ct)
    {
        var category = await db.CompetitionCategories.FirstOrDefaultAsync(c => c.Id == categoryId && c.CompetitionId == id, ct)
                       ?? throw new NotFoundException("Kateqoriya tapılmadı.");
        if (await db.Submissions.AnyAsync(s => s.CompetitionCategoryId == categoryId, ct))
            throw new ConflictException("Bu kateqoriya üzrə təkliflər var — silmək olmaz. Onu deaktiv edin.");
        db.CompetitionCategories.Remove(category);
        audit.Record(CurrentUserId, AuditActions.Deleted, AuditEntities.Category, categoryId, $"competition {id}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sets the display order: the ids of this competition's categories, first to last.</summary>
    [HttpPut("{id:int}/categories/order")]
    public async Task<IReadOnlyList<AdminCategoryDto>> ReorderCategories(int id, ReorderRequest req, CancellationToken ct)
    {
        var categories = await db.CompetitionCategories.Where(c => c.CompetitionId == id).ToListAsync(ct);
        if (categories.Count == 0)
            await EnsureCompetitionAsync(id, ct);
        if (req.Ids.Count != categories.Count || req.Ids.Distinct().Count() != categories.Count
            || req.Ids.Any(i => categories.All(c => c.Id != i)))
            throw ValidationFailedException.For("ids", "Sıralama bu müsabiqənin bütün kateqoriyalarını bir dəfə əhatə etməlidir.");
        for (var i = 0; i < req.Ids.Count; i++)
            categories.First(c => c.Id == req.Ids[i]).SortOrder = i + 1;
        audit.Record(CurrentUserId, AuditActions.CategoriesReordered, AuditEntities.Competition, id);
        await db.SaveChangesAsync(ct);
        return await Categories(id, ct);
    }

    /// <summary>
    /// Imports a list (e.g. 59 settlements) in one go. Every line is checked first — empty names, duplicate
    /// names or slugs within the list or against existing categories — and nothing is saved if any line fails.
    /// </summary>
    [HttpPost("{id:int}/categories/import")]
    public async Task<IReadOnlyList<AdminCategoryDto>> ImportCategories(int id, ImportCategoriesRequest req, CancellationToken ct)
    {
        await EnsureCompetitionAsync(id, ct);
        var existing = await db.CompetitionCategories.Where(c => c.CompetitionId == id).ToListAsync(ct);
        var names = existing.ToDictionary(c => c.Name.ToLower(Az), c => c.Name);
        var slugs = existing.Select(c => c.Slug).ToHashSet();
        var order = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder);

        var errors = new Dictionary<string, string[]>();
        var toAdd = new List<CompetitionCategory>();
        for (var i = 0; i < req.Items.Count; i++)
        {
            var item = req.Items[i];
            var line = $"items[{i}]";
            var name = item.Name?.Trim() ?? "";
            if (name.Length == 0)
            {
                errors[line] = [$"{i + 1}-ci sətir: ad boşdur."];
                continue;
            }
            if (name.Length > 160)
            {
                errors[line] = [$"{i + 1}-ci sətir: ad 160 simvoldan uzundur."];
                continue;
            }
            string slug;
            try { slug = AdminSlugs.Resolve(item.Slug, name); }
            catch (ValidationFailedException) { errors[line] = [$"{i + 1}-ci sətir: “{name}” üçün ünvan yaratmaq olmur."]; continue; }

            if (names.TryGetValue(name.ToLower(Az), out var clash))
                errors[line] = [$"{i + 1}-ci sətir: “{clash}” artıq siyahıdadır."];
            else if (!slugs.Add(slug))
                errors[line] = [$"{i + 1}-ci sətir: “{slug}” ünvanı artıq istifadə olunur."];
            else
            {
                names[name.ToLower(Az)] = name;
                toAdd.Add(new CompetitionCategory
                {
                    CompetitionId = id, Name = name, Slug = slug, SortOrder = ++order, IsActive = true,
                    Description = AdminSlugs.NullIfBlank(item.Description),
                });
            }
        }
        if (errors.Count > 0)
            throw new ValidationFailedException(errors);

        db.CompetitionCategories.AddRange(toAdd);
        audit.Record(CurrentUserId, AuditActions.CategoriesImported, AuditEntities.Competition, id, $"{toAdd.Count} kateqoriya");
        await db.SaveChangesAsync(ct);
        return await Categories(id, ct);
    }

    /// <summary>Legacy: names only, existing slugs skipped silently.</summary>
    [HttpPost("{id:int}/categories/bulk")]
    public async Task<IReadOnlyList<AdminCategoryDto>> BulkCategories(int id, BulkCategoriesRequest req, CancellationToken ct)
    {
        await EnsureCompetitionAsync(id, ct);
        var existing = await db.CompetitionCategories.Where(c => c.CompetitionId == id).ToListAsync(ct);
        var slugs = existing.Select(c => c.Slug).ToHashSet();
        var order = existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder);
        var added = 0;
        foreach (var raw in req.Names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var slug = AdminSlugs.Resolve(null, name);
            if (!slugs.Add(slug)) continue;
            db.CompetitionCategories.Add(new CompetitionCategory
            {
                CompetitionId = id, Name = name, Slug = slug, SortOrder = ++order, IsActive = true,
            });
            added++;
        }
        audit.Record(CurrentUserId, AuditActions.CategoriesImported, AuditEntities.Competition, id, $"{added} kateqoriya");
        await db.SaveChangesAsync(ct);
        return await Categories(id, ct);
    }

    // ---- Legacy per-competition submissions list ----------------------------------------------

    /// <summary>Submitted work only — drafts are private to their owner until final submission.</summary>
    [HttpGet("{id:int}/submissions")]
    public async Task<IReadOnlyList<AdminSubmissionDto>> Submissions(int id, CancellationToken ct)
    {
        await EnsureCompetitionAsync(id, ct);
        return await db.Submissions.AsNoTracking()
            .Where(s => s.CompetitionId == id && s.Status != SubmissionStatus.Draft)
            .OrderByDescending(s => s.SubmittedAt)
            .Select(s => new AdminSubmissionDto(s.Id, s.ReferenceNumber, s.Title, s.Status, s.SubmittedAt,
                s.Category != null ? s.Category.Name : null, s.UserId,
                s.User.FirstName + " " + s.User.LastName, s.User.Email, s.Files.Count))
            .ToListAsync(ct);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private sealed record Row(Competition C, string ProjectName, ProjectStatus ProjectStatus, int Categories,
        int ActiveCategories, int Fields, int Submissions, int Drafts, bool HasWinner, DateTimeOffset? ResultPublishedAt);

    private static IQueryable<Row> Project(IQueryable<Competition> query) =>
        query.Select(c => new Row(c, c.Project.Name, c.Project.Status,
            c.Categories.Count, c.Categories.Count(x => x.IsActive), c.Fields.Count(f => f.IsActive),
            c.Submissions.Count(s => s.Status != SubmissionStatus.Draft),
            c.Submissions.Count(s => s.Status == SubmissionStatus.Draft),
            c.CategoryWinners.Any() || (c.Result != null && c.Result.OverallWinnerSubmissionId != null),
            c.Result != null ? c.Result.PublishedAt : null));

    private AdminCompetitionDto ToDto(Row r)
    {
        var c = r.C;
        var now = clock.GetUtcNow();
        return new AdminCompetitionDto(c.Id, c.ProjectId, r.ProjectName, r.ProjectStatus, c.Title, c.Slug,
            c.ShortDescription, c.Description, c.Rules, c.Requirements, c.Nominations,
            c.JudgingCriteria, c.UsageTerms, c.ContactNote, c.CoverImage,
            c.StartDate, c.EndDate, c.ResultsAnnouncedOn, c.ExhibitionOn, c.Prize,
            c.Status, CompetitionPhases.Of(c, now), c.IsAcceptingSubmissions(now), c.HasCategories, c.CategoryLabel,
            c.MaxSubmissionsPerUser, c.SortOrder, r.Categories, r.ActiveCategories, r.Fields, r.Submissions, r.Drafts,
            c.CreatedAt, c.UpdatedAt,
            r.ResultPublishedAt is not null ? ResultState.Published : r.HasWinner ? ResultState.Prepared : ResultState.NoWinner,
            c.IsDemo, c.VotingStartsAt, c.VotingEndsAt, c.VotingStateAt(now));
    }

    private async Task<Competition> FindAsync(int id, CancellationToken ct) =>
        await db.Competitions.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Müsabiqə tapılmadı.");

    private async Task EnsureCompetitionAsync(int id, CancellationToken ct)
    {
        if (!await db.Competitions.AnyAsync(c => c.Id == id, ct))
            throw new NotFoundException("Müsabiqə tapılmadı.");
    }

    private async Task ApplyAsync(Competition c, CompetitionUpsertRequest req, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == req.ProjectId, ct))
            throw ValidationFailedException.For("projectId", "Belə layihə yoxdur.");
        if (req.StartDate is not null && req.EndDate is not null && req.EndDate < req.StartDate)
            throw ValidationFailedException.For("endDate", "Bitmə tarixi başlama tarixindən əvvəl ola bilməz.");
        // A half-set window is not a window: the public side would have to guess which half to believe, so it is
        // refused here rather than silently ignored.
        if (req.VotingStartsAt is null != req.VotingEndsAt is null)
            throw ValidationFailedException.For(req.VotingStartsAt is null ? "votingStartsAt" : "votingEndsAt",
                "Səsvermənin həm başlama, həm də bitmə tarixini yazın (və ya hər ikisini boş buraxın).");
        if (req.VotingStartsAt is { } from && req.VotingEndsAt is { } to && to <= from)
            throw ValidationFailedException.For("votingEndsAt", "Səsvermənin bitmə tarixi başlama tarixindən sonra olmalıdır.");

        var slug = AdminSlugs.Resolve(req.Slug, req.Title);
        if (await db.Competitions.AnyAsync(x => x.Slug == slug && x.Id != c.Id, ct))
            throw ValidationFailedException.For("slug", $"“{slug}” ünvanı artıq başqa müsabiqədə istifadə olunur.");

        c.ProjectId = req.ProjectId;
        c.Title = req.Title.Trim();
        c.Slug = slug;
        c.ShortDescription = req.ShortDescription.Trim();
        c.Description = AdminSlugs.NullIfBlank(req.Description);
        c.Rules = AdminSlugs.NullIfBlank(req.Rules);
        c.Requirements = AdminSlugs.NullIfBlank(req.Requirements);
        c.Nominations = AdminSlugs.NullIfBlank(req.Nominations);
        c.JudgingCriteria = AdminSlugs.NullIfBlank(req.JudgingCriteria);
        c.UsageTerms = AdminSlugs.NullIfBlank(req.UsageTerms);
        c.ContactNote = AdminSlugs.NullIfBlank(req.ContactNote);
        c.StartDate = req.StartDate;
        c.EndDate = req.EndDate;
        c.ResultsAnnouncedOn = req.ResultsAnnouncedOn;
        c.ExhibitionOn = req.ExhibitionOn;
        c.Prize = AdminSlugs.NullIfBlank(req.Prize);
        c.HasCategories = req.HasCategories;
        c.CategoryLabel = AdminSlugs.NullIfBlank(req.CategoryLabel);
        c.MaxSubmissionsPerUser = req.MaxSubmissionsPerUser;
        c.SortOrder = req.SortOrder;

        // The window gets a line of its own in the audit. Under D3 the vote decides who receives a prize, so
        // "who moved the dates, and when" is not a detail of an ordinary edit.
        if (c.VotingStartsAt != req.VotingStartsAt || c.VotingEndsAt != req.VotingEndsAt)
        {
            var was = Window(c.VotingStartsAt, c.VotingEndsAt);
            c.VotingStartsAt = req.VotingStartsAt;
            c.VotingEndsAt = req.VotingEndsAt;
            if (c.Id != 0)
                audit.Record(CurrentUserId, AuditActions.VotingWindowChanged, AuditEntities.Competition, c.Id,
                    $"{was} → {Window(c.VotingStartsAt, c.VotingEndsAt)}");
        }
    }

    private static string Window(DateTimeOffset? from, DateTimeOffset? to) =>
        from is null || to is null ? "yoxdur" : $"{from:yyyy-MM-dd HH:mm}–{to:yyyy-MM-dd HH:mm}";

    private async Task ApplyCategoryAsync(CompetitionCategory c, CategoryUpsertRequest req, CancellationToken ct)
    {
        var name = req.Name.Trim();
        var slug = AdminSlugs.Resolve(req.Slug, name);
        var lower = name.ToLower(Az);
        var siblings = await db.CompetitionCategories
            .Where(x => x.CompetitionId == c.CompetitionId && x.Id != c.Id)
            .Select(x => new { x.Name, x.Slug })
            .ToListAsync(ct);
        if (siblings.Any(x => x.Name.ToLower(Az) == lower))
            throw ValidationFailedException.For("name", $"“{name}” adlı kateqoriya artıq var.");
        if (siblings.Any(x => x.Slug == slug))
            throw ValidationFailedException.For("slug", $"“{slug}” ünvanlı kateqoriya artıq var.");

        c.Name = name;
        c.Slug = slug;
        c.Description = AdminSlugs.NullIfBlank(req.Description);
        c.IsActive = req.IsActive;
    }

    private static AdminCategoryDto ToDto(CompetitionCategory c, int used) =>
        new(c.Id, c.CompetitionId, c.Name, c.Slug, c.Description, c.SortOrder, c.IsActive, used);
}
