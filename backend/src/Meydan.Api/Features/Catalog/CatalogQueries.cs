using Meydan.Api.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Catalog;

/// <summary>
/// Read side for public pages. Every query is filtered to publicly visible rows here, in one place,
/// so a Draft project or competition cannot leak through a new endpoint by accident.
/// </summary>
public sealed class CatalogQueries(MeydanDbContext db, TimeProvider clock)
{
    private static readonly ProjectStatus[] VisibleProjectStatuses = [ProjectStatus.Published];

    private static readonly CompetitionStatus[] VisibleCompetitionStatuses =
        [CompetitionStatus.Published, CompetitionStatus.Closed];

    private IQueryable<Project> VisibleProjects =>
        db.Projects.AsNoTracking().Where(p => VisibleProjectStatuses.Contains(p.Status));

    private IQueryable<Competition> VisibleCompetitions =>
        db.Competitions.AsNoTracking()
            .Where(c => VisibleCompetitionStatuses.Contains(c.Status)
                        && VisibleProjectStatuses.Contains(c.Project.Status));

    public async Task<IReadOnlyList<ProjectCardDto>> GetProjectsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var projects = await VisibleProjects
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
        var competitions = await VisibleCompetitions
            .Select(c => new { c.ProjectId, c.Status, c.StartDate, c.EndDate })
            .ToListAsync(ct);

        return projects.Select(p =>
        {
            var own = competitions.Where(c => c.ProjectId == p.Id).ToList();
            var open = own.Count(c => CompetitionPhases.Of(
                new Competition { Status = c.Status, StartDate = c.StartDate, EndDate = c.EndDate }, now)
                == CompetitionPhase.Open);
            return new ProjectCardDto(p.Id, p.Slug, p.Name, p.ShortDescription, p.CoverImage, p.AccentColor,
                p.Status, p.SortOrder, open, own.Count);
        }).ToList();
    }

    public async Task<ProjectDetailDto> GetProjectAsync(string slug, CancellationToken ct)
    {
        var project = await VisibleProjects.FirstOrDefaultAsync(p => p.Slug == slug, ct)
                      ?? throw new NotFoundException("Belə layihə tapılmadı.");
        var competitions = await GetCompetitionsAsync(project.Slug, ct);
        return new ProjectDetailDto(project.Id, project.Slug, project.Name, project.ShortDescription,
            project.Description, project.CoverImage, project.AccentColor, project.Status, project.SortOrder,
            competitions);
    }

    public async Task<IReadOnlyList<CompetitionSummaryDto>> GetCompetitionsAsync(string? projectSlug, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = VisibleCompetitions;
        if (!string.IsNullOrWhiteSpace(projectSlug))
            query = query.Where(c => c.Project.Slug == projectSlug);

        var rows = await query
            .OrderBy(c => c.Project.SortOrder).ThenBy(c => c.SortOrder).ThenBy(c => c.Title)
            .Select(c => new
            {
                Competition = c,
                ProjectRef = new ProjectRefDto(c.Project.Slug, c.Project.Name, c.Project.AccentColor),
                CategoryCount = c.Categories.Count(x => x.IsActive),
                ResultsPublishedAt = c.Result != null ? c.Result.PublishedAt : null,
            })
            .ToListAsync(ct);

        return rows
            .Select(r => ToSummary(r.Competition, r.ProjectRef, r.CategoryCount, r.ResultsPublishedAt, now))
            // Open first, then upcoming, then the finished ones — what a visitor can act on comes first.
            .OrderBy(s => s.Phase switch
            {
                CompetitionPhase.Open => 0,
                CompetitionPhase.Upcoming => 1,
                _ => 2,
            })
            .ToList();
    }

    public async Task<CompetitionDetailDto> GetCompetitionAsync(string slug, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var c = await VisibleCompetitions
            .Include(x => x.Project)
            .Include(x => x.Result)
            .Include(x => x.Categories.Where(cat => cat.IsActive).OrderBy(cat => cat.SortOrder).ThenBy(cat => cat.Name))
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Slug == slug, ct)
                ?? throw new NotFoundException("Belə müsabiqə tapılmadı.");

        return new CompetitionDetailDto(
            c.Id, c.Slug, c.Title, c.ShortDescription, c.Description, c.Rules, c.Requirements, c.Nominations, c.CoverImage,
            c.Status, CompetitionPhases.Of(c, now), c.IsAcceptingSubmissions(now),
            c.StartDate, c.EndDate, c.ResultsAnnouncedOn, c.ExhibitionOn, c.JudgingCriteria, c.UsageTerms, c.ContactNote,
            c.Prize, c.HasCategories, c.CategoryLabel, c.MaxSubmissionsPerUser,
            c.HasCategories
                ? c.Categories.Select(x => new CategoryDto(x.Id, x.Slug, x.Name, x.Description, x.Image)).ToList()
                : [],
            new ProjectRefDto(c.Project.Slug, c.Project.Name, c.Project.AccentColor),
            CompetitionPhases.AreResultsPublic(c, c.Result?.PublishedAt, now) ? c.Result!.PublishedAt : null,
            c.VotingStateAt(now));
    }

    public async Task<HomeDto> GetHomeAsync(CancellationToken ct)
    {
        var projects = await GetProjectsAsync(ct);
        var competitions = await GetCompetitionsAsync(null, ct);
        return new HomeDto(projects,
            competitions.Where(c => c.Phase == CompetitionPhase.Open).ToList(),
            competitions.Where(c => c.Voting == VotingState.Open).ToList());
    }

    private static CompetitionSummaryDto ToSummary(Competition c, ProjectRefDto project, int categoryCount,
        DateTimeOffset? resultsPublishedAt, DateTimeOffset now) =>
        new(c.Id, c.Slug, c.Title, c.ShortDescription, c.CoverImage, c.Status, CompetitionPhases.Of(c, now),
            c.StartDate, c.EndDate, !string.IsNullOrWhiteSpace(c.Prize), c.HasCategories, c.CategoryLabel,
            c.HasCategories ? categoryCount : 0, project,
            CompetitionPhases.AreResultsPublic(c, resultsPublishedAt, now), c.VotingStateAt(now));
}
