using Meydan.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace Meydan.Api.Features.Voting;

/// <summary>
/// The public vote. Reading is anonymous — anybody may see the ballot and the running counts (D4) — but the
/// answer is shaped to whoever is asking, so a signed-in visitor sees which nomination they have already used.
/// Casting requires a session, and the vote itself takes nothing from the request but the route: the voter comes
/// from the token, the nomination from the work, and the count from the database. There is no way to change or
/// withdraw a vote, by design (D5), so no PUT and no DELETE exists to be found.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/competitions/{slug}/voting")]
public sealed class VotingController(PublicVoting voting) : ControllerBase
{
    /// <summary>Where the vote stands, its window, its nominations and their totals.</summary>
    [HttpGet]
    public Task<VotingDto> Summary(string slug, CancellationToken ct) =>
        voting.SummaryAsync(slug, User.TryGetUserId(), ct);

    /// <summary>
    /// One page of the ballot. An unknown nomination gives an empty page, not an error. With <c>seed</c> the works
    /// come in that visitor's own shuffled order — the same order on every page and every reload for the same seed.
    /// </summary>
    [HttpGet("works")]
    public Task<VotingWorksDto> Works(
        string slug,
        [FromQuery] string? nomination,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        [FromQuery] int? seed = null,
        CancellationToken ct = default) =>
        voting.WorksAsync(slug, nomination, q, page, pageSize, seed, User.TryGetUserId(), ct);

    /// <summary>
    /// The live ranking: every work on the ballot by its count, with the work this viewer voted for. Read every few
    /// seconds by an open page, so it is one small answer for the whole list rather than a request per work.
    /// </summary>
    [HttpGet("leaderboard")]
    public Task<VotingLeaderboardDto> Leaderboard(string slug, CancellationToken ct) =>
        voting.LeaderboardAsync(slug, User.TryGetUserId(), ct);

    /// <summary>
    /// The public audit of the vote: valid, set-aside and total votes, and one line per admin decision (when, how
    /// many, under which rule). Aggregates only — no voter, account, phone, address, admin or work.
    /// </summary>
    [HttpGet("audit")]
    public Task<VotingAuditDto> Audit(string slug, CancellationToken ct) => voting.AuditAsync(slug, ct);

    /// <summary>One work, with every public-safe picture of it and what this viewer may do about it.</summary>
    [HttpGet("works/{reference}")]
    public Task<VotingWorkDetailDto> Work(string slug, string reference, CancellationToken ct) =>
        voting.WorkAsync(slug, reference, User.TryGetUserId(), ct);

    /// <summary>
    /// Casts this viewer's one vote in the work's nomination. 403 for their own work, 404 when the work is not on
    /// the ballot, 409 when they have already voted in that nomination, 422 once the window has shut, 400 on
    /// <c>captcha</c> when the robot check is on and the body carries no passing token for it.
    /// </summary>
    [HttpPost("works/{reference}/vote")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Vote)]
    public async Task<ActionResult<VoteReceiptDto>> Vote(
        string slug, string reference,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] VoteRequest? req,
        CancellationToken ct)
    {
        var receipt = await voting.CastAsync(
            User.GetUserId(), slug, reference, req?.CaptchaToken, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return Created($"/api/competitions/{slug}/voting/works/{reference}", receipt);
    }

    /// <summary>The public-safe derivative (a server-made WEBP) of a work on the ballot. Never the original.</summary>
    [HttpGet("works/{reference}/files/{fileId:int}")]
    public Task<IActionResult> File(string slug, string reference, int fileId, CancellationToken ct) =>
        Serve(slug, reference, fileId, thumbnail: false, ct);

    /// <summary>The card-size copy of the same file, same gate and headers.</summary>
    [HttpGet("works/{reference}/files/{fileId:int}/thumb")]
    public Task<IActionResult> Thumbnail(string slug, string reference, int fileId, CancellationToken ct) =>
        Serve(slug, reference, fileId, thumbnail: true, ct);

    /// <summary>Another page of a PDF work (2, 3, …) as a public-safe image. The PDF itself is never served.</summary>
    [HttpGet("works/{reference}/files/{fileId:int}/pages/{page:int}")]
    public Task<IActionResult> Page(string slug, string reference, int fileId, int page, CancellationToken ct) =>
        Serve(voting.OpenPageAsync(slug, reference, fileId, page, thumbnail: false, ct));

    [HttpGet("works/{reference}/files/{fileId:int}/pages/{page:int}/thumb")]
    public Task<IActionResult> PageThumbnail(string slug, string reference, int fileId, int page, CancellationToken ct) =>
        Serve(voting.OpenPageAsync(slug, reference, fileId, page, thumbnail: true, ct));

    private Task<IActionResult> Serve(string slug, string reference, int fileId, bool thumbnail, CancellationToken ct) =>
        Serve(voting.OpenFileAsync(slug, reference, fileId, thumbnail, ct));

    /// <summary>
    /// Browsers and proxies must revalidate every time (no-cache + ETag), so the voting gate runs on each request
    /// and closing the window, switching a nomination off or rejecting a work takes effect at once. Nothing in the
    /// response can run in the browser (sandbox CSP, nosniff).
    /// </summary>
    private async Task<IActionResult> Serve(Task<(Stream Content, FileType Type, string DownloadName, string ETag)> open)
    {
        var (content, type, name, etag) = await open;
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            await content.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(name);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return base.File(content, type.MimeType);
    }
}
