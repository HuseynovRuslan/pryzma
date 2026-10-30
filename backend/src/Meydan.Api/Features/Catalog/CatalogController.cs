using Microsoft.AspNetCore.Mvc;

namespace Meydan.Api.Features.Catalog;

[ApiController]
[Route("api")]
public sealed class CatalogController(CatalogQueries queries) : ControllerBase
{
    /// <summary>Everything the universal home page needs in one round trip.</summary>
    [HttpGet("home")]
    public Task<HomeDto> Home(CancellationToken ct) => queries.GetHomeAsync(ct);

    [HttpGet("projects")]
    public Task<IReadOnlyList<ProjectCardDto>> Projects(CancellationToken ct) => queries.GetProjectsAsync(ct);

    [HttpGet("projects/{slug}")]
    public Task<ProjectDetailDto> Project(string slug, CancellationToken ct) => queries.GetProjectAsync(slug, ct);

    [HttpGet("competitions")]
    public Task<IReadOnlyList<CompetitionSummaryDto>> Competitions([FromQuery] string? project, CancellationToken ct) =>
        queries.GetCompetitionsAsync(project, ct);

    [HttpGet("competitions/{slug}")]
    public Task<CompetitionDetailDto> Competition(string slug, CancellationToken ct) =>
        queries.GetCompetitionAsync(slug, ct);
}
