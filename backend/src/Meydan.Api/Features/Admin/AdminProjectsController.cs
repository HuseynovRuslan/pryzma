using Meydan.Api.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Admin;

[Route("api/admin/projects")]
public sealed class AdminProjectsController(
    MeydanDbContext db,
    AuditTrail audit,
    IFileStorage storage,
    PublicDerivatives derivatives,
    PermanentDeletion deletion,
    IOptions<UploadOptions> uploads) : AdminControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<AdminProjectDto>> List(CancellationToken ct) =>
        await Query(db.Projects.OrderBy(p => p.SortOrder).ThenBy(p => p.Name)).ToListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<AdminProjectDto> Get(int id, CancellationToken ct) =>
        await Query(db.Projects.Where(p => p.Id == id)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Layihə tapılmadı.");

    /// <summary>New projects always start as drafts; publishing is a separate, audited step.</summary>
    [HttpPost]
    public async Task<ActionResult<AdminProjectDto>> Create(ProjectUpsertRequest req, CancellationToken ct)
    {
        var project = new Project { Status = ProjectStatus.Draft };
        await ApplyAsync(project, req, ct);
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        audit.Record(CurrentUserId, AuditActions.Created, AuditEntities.Project, project.Id, project.Slug);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, await Get(project.Id, ct));
    }

    [HttpPut("{id:int}")]
    public async Task<AdminProjectDto> Update(int id, ProjectUpsertRequest req, CancellationToken ct)
    {
        var project = await FindAsync(id, ct);
        await ApplyAsync(project, req, ct);
        audit.Record(CurrentUserId, AuditActions.Updated, AuditEntities.Project, project.Id, project.Slug);
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Publish, unpublish (back to draft) or archive.</summary>
    [HttpPut("{id:int}/status")]
    public async Task<AdminProjectDto> ChangeStatus(int id, StatusRequest<ProjectStatus> req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Status))
            throw ValidationFailedException.For("status", "Status düzgün deyil.");
        var project = await FindAsync(id, ct);
        if (project.Status != req.Status)
        {
            var action = req.Status switch
            {
                ProjectStatus.Published => AuditActions.Published,
                ProjectStatus.Archived => AuditActions.Archived,
                _ => AuditActions.Unpublished,
            };
            audit.Record(CurrentUserId, action, AuditEntities.Project, id, $"{project.Status} → {req.Status}");
            project.Status = req.Status;
            await db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
    }

    [HttpPost("{id:int}/cover")]
    [UploadBodyLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<AdminProjectDto> UploadCover(int id, CancellationToken ct)
    {
        var (_, file) = await UploadForms.ReadSingleFileAsync(Request, "cover", uploads.Value, ct);
        var project = await FindAsync(id, ct);
        var url = await CoverImages.SaveAsync(file, uploads.Value, storage, derivatives, ct);
        var previous = project.CoverImage;
        project.CoverImage = url;
        audit.Record(CurrentUserId, AuditActions.CoverChanged, AuditEntities.Project, id);
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return await Get(id, ct);
    }

    /// <summary>Removes the cover; the generated pattern takes its place.</summary>
    [HttpDelete("{id:int}/cover")]
    public async Task<AdminProjectDto> RemoveCover(int id, CancellationToken ct)
    {
        var project = await FindAsync(id, ct);
        var previous = project.CoverImage;
        project.CoverImage = null;
        audit.Record(CurrentUserId, AuditActions.CoverChanged, AuditEntities.Project, id, "removed");
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(previous, storage);
        return await Get(id, ct);
    }

    /// <summary>Hard delete of an empty project only. Anything with competitions must be archived.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var project = await FindAsync(id, ct);
        if (await db.Competitions.AnyAsync(c => c.ProjectId == id, ct))
            throw new ConflictException("Layihənin müsabiqələri var — silmək olmaz. Onu arxivləşdirin.");
        db.Projects.Remove(project);
        audit.Record(CurrentUserId, AuditActions.Deleted, AuditEntities.Project, id, project.Slug);
        await db.SaveChangesAsync(ct);
        CoverImages.DeleteIfOwned(project.CoverImage, storage);
        return NoContent();
    }

    /// <summary>What would go with this project, counted on the server for the confirmation dialog.</summary>
    [HttpGet("{id:int}/delete-impact")]
    public Task<DeleteImpactDto> DeleteImpact(int id, CancellationToken ct) => deletion.ProjectImpactAsync(id, ct);

    /// <summary>
    /// Removes the project and everything under it — competitions, their entries, answers, files, results — and
    /// nothing else. Participants keep their accounts. There is no undo; the audit entry survives.
    /// </summary>
    [HttpDelete("{id:int}/permanent")]
    public async Task<DeleteReport> DeletePermanently(int id, CancellationToken ct) =>
        await deletion.DeleteProjectAsync(id, CurrentUserId, ct);

    /// <summary>Filter and order the entities first; the DTO projection must come last to stay translatable.</summary>
    private static IQueryable<AdminProjectDto> Query(IQueryable<Project> source) =>
        source.AsNoTracking().Select(p => new AdminProjectDto(
            p.Id, p.Name, p.Slug, p.ShortDescription, p.Description, p.CoverImage, p.AccentColor, p.Status,
            p.SortOrder, p.Competitions.Count,
            p.Competitions.SelectMany(c => c.Submissions).Count(s => s.Status != SubmissionStatus.Draft),
            p.CreatedAt, p.UpdatedAt, p.IsDemo));

    private async Task<Project> FindAsync(int id, CancellationToken ct) =>
        await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Layihə tapılmadı.");

    private async Task ApplyAsync(Project p, ProjectUpsertRequest req, CancellationToken ct)
    {
        var slug = AdminSlugs.Resolve(req.Slug, req.Name);
        if (await db.Projects.AnyAsync(x => x.Slug == slug && x.Id != p.Id, ct))
            throw ValidationFailedException.For("slug", $"“{slug}” ünvanı artıq başqa layihədə istifadə olunur.");

        p.Name = req.Name.Trim();
        p.Slug = slug;
        p.ShortDescription = req.ShortDescription.Trim();
        p.Description = AdminSlugs.NullIfBlank(req.Description);
        p.AccentColor = AdminSlugs.NullIfBlank(req.AccentColor)?.ToUpperInvariant();
        p.SortOrder = req.SortOrder;
    }
}
