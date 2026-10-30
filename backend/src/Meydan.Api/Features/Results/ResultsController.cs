using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Meydan.Api.Features.Results;

/// <summary>Public, anonymous read endpoints for published results. See <see cref="PublicResults"/> for the gate.</summary>
[ApiController]
[Route("api")]
public sealed class ResultsController(PublicResults results) : ControllerBase
{
    [HttpGet("results")]
    public Task<IReadOnlyList<ResultListItemDto>> List(CancellationToken ct) => results.ListAsync(ct);

    [HttpGet("competitions/{slug}/results")]
    public Task<CompetitionResultDto> Result(string slug, CancellationToken ct) => results.GetAsync(slug, ct);

    [HttpGet("competitions/{slug}/gallery")]
    public Task<GalleryDto> Gallery(string slug, [FromQuery] string? category, CancellationToken ct) =>
        results.GalleryAsync(slug, category, ct);

    [HttpGet("competitions/{slug}/gallery/{reference}")]
    public Task<GalleryWorkDto> Work(string slug, string reference, CancellationToken ct) =>
        results.WorkAsync(slug, reference, ct);

    /// <summary>
    /// The public-safe derivative (a server-encoded WEBP) of a public-after-results file. Browsers and proxies must
    /// revalidate every time (no-cache + ETag), so the result gate runs on each request and un-publishing takes
    /// effect immediately; nothing can run in the browser (sandbox CSP, nosniff).
    /// </summary>
    [HttpGet("competitions/{slug}/gallery/{reference}/files/{fileId:int}")]
    public Task<IActionResult> GalleryFile(string slug, string reference, int fileId, CancellationToken ct) =>
        Serve(slug, reference, fileId, thumbnail: false, ct);

    /// <summary>The card-size copy of the same file (a few dozen kilobytes), same gate and headers.</summary>
    [HttpGet("competitions/{slug}/gallery/{reference}/files/{fileId:int}/thumb")]
    public Task<IActionResult> GalleryThumbnail(string slug, string reference, int fileId, CancellationToken ct) =>
        Serve(slug, reference, fileId, thumbnail: true, ct);

    private async Task<IActionResult> Serve(string slug, string reference, int fileId, bool thumbnail, CancellationToken ct)
    {
        var (content, type, name, etag) = await results.OpenFileAsync(slug, reference, fileId, thumbnail, ct);
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
        return File(content, type.MimeType);
    }
}
