using Meydan.Api.Common;
using Meydan.Api.Features.Catalog;
using Meydan.Domain;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Admin;

/// <summary>Dashboard numbers, participants, the audit log and the global upload policy.</summary>
[Route("api/admin")]
public sealed class AdminOverviewController(
    MeydanDbContext db, TimeProvider clock, PermanentDeletion deletion, IOptions<UploadOptions> uploads,
    Voting.DocumentPages pages)
    : AdminControllerBase
{
    /// <summary>
    /// The overview, for the whole platform or — with <paramref name="competitionId"/> — for one competition: its
    /// submissions by status and its latest entries. The list of competitions to choose from comes with it.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<DashboardDto> Dashboard([FromQuery] int? competitionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await db.Competitions.AsNoTracking().Include(c => c.Project)
            .Where(c => !c.IsDemo)
            .ToListAsync(ct);
        if (competitionId is { } chosen && all.All(c => c.Id != chosen))
            throw new NotFoundException("Müsabiqə tapılmadı.");
        var competitions = all
            .Where(c => c.Status == CompetitionStatus.Published && c.Project.Status == ProjectStatus.Published)
            .ToList();
        var submissions = db.Submissions.AsNoTracking()
            .Where(s => competitionId == null || s.CompetitionId == competitionId);
        var counts = await submissions
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Count(SubmissionStatus s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

        var recent = await submissions
            .Where(s => s.Status != SubmissionStatus.Draft)
            .OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id)
            .Take(8)
            .Select(AdminSubmissionsController.ListItem)
            .ToListAsync(ct);
        recent = await SubmissionImages.WithPreviewPagesAsync(recent, db, pages, ct);

        return new DashboardDto(
            await db.Projects.CountAsync(p => p.Status == ProjectStatus.Published, ct),
            competitions.Count(c => CompetitionPhases.Of(c, now) == CompetitionPhase.Open),
            counts.Where(c => c.Status != SubmissionStatus.Draft).Sum(c => c.Count),
            Count(SubmissionStatus.Draft),
            Count(SubmissionStatus.Submitted),
            Count(SubmissionStatus.UnderReview),
            recent,
            competitionId,
            Count(SubmissionStatus.Approved),
            Count(SubmissionStatus.Rejected),
            // Open ones first (the one being run), then the rest, newest first.
            all.Select(c => new { c.Id, c.Title, c.Status, c.StartDate, Phase = CompetitionPhases.Of(c, now) })
                .Select(c => new { c.Id, c.Title, c.Status, c.StartDate, c.Phase,
                    IsOpen = c.Status == CompetitionStatus.Published && c.Phase == CompetitionPhase.Open })
                .OrderByDescending(c => c.IsOpen).ThenByDescending(c => c.StartDate).ThenByDescending(c => c.Id)
                .Select(c => new DashboardCompetitionDto(c.Id, c.Title, c.Status, c.Phase, c.IsOpen))
                .ToList());
    }

    [HttpGet("participants")]
    public async Task<Paged<AdminParticipantListItem>> Participants([FromQuery] string? q, [FromQuery] bool? demo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstOrDefaultAsync(ct);
        var users = db.Users.AsNoTracking();
        if (demo is { } onlyDemo) users = users.Where(u => u.IsDemo == onlyDemo);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            users = users.Where(u => EF.Functions.ILike(u.FirstName + " " + u.LastName, pattern)
                                     || EF.Functions.ILike(u.Email ?? "", pattern)
                                     || EF.Functions.ILike(u.PhoneNumber ?? "", pattern));
        }
        var total = await users.CountAsync(ct);
        var items = await users
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new AdminParticipantListItem(
                u.Id, u.FirstName + " " + u.LastName, u.Email, u.PhoneNumber, u.CreatedAt,
                u.Submissions.Count(s => s.Status != SubmissionStatus.Draft),
                db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId),
                u.IsDemo,
                // Every vote, set-aside ones too (the query filter hides those): PermanentDeletion's own rule.
                db.Votes.IgnoreQueryFilters().Any(v => v.UserId == u.Id),
                !u.Submissions.Any()
                && !db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId)
                && !db.Votes.IgnoreQueryFilters().Any(v => v.UserId == u.Id)))
            .ToListAsync(ct);
        return new Paged<AdminParticipantListItem>(items, total, page, pageSize);
    }

    /// <summary>What removing this account would take with it — nothing, or the request is refused.</summary>
    [HttpGet("participants/{id:guid}/delete-impact")]
    public Task<DeleteImpactDto> ParticipantDeleteImpact(Guid id, CancellationToken ct) =>
        deletion.ParticipantImpactAsync(id, CurrentUserId, ct);

    /// <summary>
    /// Removes an account that holds no work, with its sign-ins, sessions and pending mail. An account with
    /// entries, an admin's account and the admin's own are all refused; the audit entry survives the account.
    /// </summary>
    [HttpDelete("participants/{id:guid}/permanent")]
    public async Task<DeleteReport> DeleteParticipant(Guid id, CancellationToken ct) =>
        await deletion.DeleteParticipantAsync(id, CurrentUserId, ct);

    /// <summary>Recent administrative actions, optionally for one entity.</summary>
    [HttpGet("audit")]
    public async Task<IReadOnlyList<AuditEntryDto>> Audit([FromQuery] string? entityType, [FromQuery] string? entityId,
        [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrWhiteSpace(entityId)) query = query.Where(a => a.EntityId == entityId);
        return await query
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Select(a => new AuditEntryDto(a.Id, a.Action, a.EntityType, a.EntityId, a.Summary,
                db.Users.Where(u => u.Id == a.UserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault(),
                a.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>The server-wide ceiling a competition's upload settings must stay within.</summary>
    [HttpGet("upload-policy")]
    public UploadPolicyDto UploadPolicy() =>
        new(uploads.Value.MaxFileSizeMb, uploads.Value.MaxFilesPerSubmission,
            uploads.Value.AllowedFileTypes.Select(t => new UploadTypeDto(t.Key, t.Label, t.Extensions)).ToList());
}
