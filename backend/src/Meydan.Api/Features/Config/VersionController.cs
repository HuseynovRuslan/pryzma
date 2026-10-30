using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Meydan.Api.Features.Config;

/// <summary>The commit this release was packed from ("unknown" outside a deploy); nothing else.</summary>
public sealed record VersionDto(string Commit);

/// <summary>
/// Which commit the site runs. The GitHub workflow waits on it after a push to main: the VPS pulls and deploys the
/// commit itself (its SSH port is not reachable from GitHub), and this is how the workflow learns it went live.
/// </summary>
[ApiController]
[Route("api/version")]
[DisableRateLimiting]
public sealed class VersionController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true)]
    public VersionDto Get() => new(config["MEYDAN_COMMIT"] is { Length: > 0 } commit ? commit : "unknown");
}
