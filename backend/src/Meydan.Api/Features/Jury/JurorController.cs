using Meydan.Api.Common;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Meydan.Api.Features.Jury;

/// <summary>
/// The juror's own API. It takes only the juror session cookie (<see cref="JuryAccess"/>) — never a user's token —
/// and every answer is shaped by the juror DTOs, which cannot hold a vote, a public rank, points or another juror's
/// score. Nothing is cached anywhere on the way.
/// </summary>
[ApiController]
[Route("api/jury")]
[AllowAnonymous]
[JuryNoStore]
public sealed class JurorController(JuryAccess access, JuryEvaluationService jury, JuryFiles files) : ControllerBase
{
    /// <summary>The invitation's secret (from the link's #fragment) and the separate access code. Sets the session cookie.</summary>
    [HttpPost("sign-in")]
    [EnableRateLimiting(RateLimitPolicies.JuryAuth)]
    public async Task<JurorSessionDto> SignIn(JurorSignInRequest req, CancellationToken ct) =>
        JuryEvaluationService.SessionOf(await access.SignInAsync(HttpContext, req, ct));

    [HttpPost("sign-out")]
    public async Task<IActionResult> SignOut(CancellationToken ct)
    {
        await access.SignOutAsync(HttpContext, ct);
        return NoContent();
    }

    [HttpGet("session")]
    public async Task<JurorSessionDto> Session(CancellationToken ct) =>
        JuryEvaluationService.SessionOf(await access.RequireAsync(HttpContext, ct));

    /// <summary>The juror confirms that the name shown is theirs; nothing else opens until they do.</summary>
    [HttpPost("confirm")]
    public async Task<JurorSessionDto> Confirm(CancellationToken ct) =>
        await jury.ConfirmIdentityAsync(await access.RequireAsync(HttpContext, ct), ct);

    [HttpGet("dashboard")]
    public async Task<JurorDashboardDto> Dashboard(CancellationToken ct) => await jury.DashboardAsync(await ConfirmedAsync(ct), ct);

    [HttpGet("works/{submissionId:int}")]
    public async Task<JurorWorkDto> Work(int submissionId, CancellationToken ct) =>
        await jury.WorkAsync(await ConfirmedAsync(ct), submissionId, ct);

    /// <summary>Saves the juror's 1–10 score (and optional note) for one work at once. 409 if another device saved meanwhile.</summary>
    [HttpPut("works/{submissionId:int}/score")]
    [EnableRateLimiting(RateLimitPolicies.JuryWrite)]
    public async Task<JurorScoreDto> Score(int submissionId, JurorScoreRequest req, CancellationToken ct) =>
        await jury.SaveScoreAsync(await ConfirmedAsync(ct), submissionId, req, ct);

    /// <summary>Finishes the evaluation: every work must be scored; the scores lock.</summary>
    [HttpPost("submit")]
    [EnableRateLimiting(RateLimitPolicies.JuryWrite)]
    public async Task<JurorDashboardDto> Submit(CancellationToken ct) => await jury.SubmitAsync(await ConfirmedAsync(ct), ct);

    [HttpGet("works/{submissionId:int}/files/{fileId:int}")]
    public Task<IActionResult> File(int submissionId, int fileId, CancellationToken ct) => Serve(submissionId, fileId, 1, false, ct);

    [HttpGet("works/{submissionId:int}/files/{fileId:int}/thumb")]
    public Task<IActionResult> Thumbnail(int submissionId, int fileId, CancellationToken ct) => Serve(submissionId, fileId, 1, true, ct);

    [HttpGet("works/{submissionId:int}/files/{fileId:int}/pages/{page:int}")]
    public Task<IActionResult> Page(int submissionId, int fileId, int page, [FromQuery] bool thumb, CancellationToken ct) =>
        Serve(submissionId, fileId, page, thumb, ct);

    private async Task<JuryAssignment> ConfirmedAsync(CancellationToken ct)
    {
        var juror = await access.RequireAsync(HttpContext, ct);
        if (juror.IdentityConfirmedAt is null) throw new ForbiddenException("Əvvəlcə adınızı təsdiqləyin.");
        return juror;
    }

    private async Task<IActionResult> Serve(int submissionId, int fileId, int page, bool thumbnail, CancellationToken ct)
    {
        var (content, etag) = await files.OpenForJurorAsync(await ConfirmedAsync(ct), submissionId, fileId, page, thumbnail, ct);
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            await content.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }
        return base.File(content, FileTypes.Webp.MimeType);
    }
}

/// <summary>Juror answers (and the admin's invitation secrets) are never stored by a browser or a proxy. Pictures set their own.</summary>
public sealed class JuryNoStoreAttribute : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        if (!headers.ContainsKey(HeaderNames.CacheControl)) headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        base.OnResultExecuting(context);
    }
}
