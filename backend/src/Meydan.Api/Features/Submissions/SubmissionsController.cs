using Meydan.Api.Common;
using Meydan.Domain;
using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Meydan.Api.Features.Submissions;

[ApiController]
[Authorize]
[Route("api/submissions")]
public sealed class SubmissionsController(SubmissionService submissions, IOptions<UploadOptions> uploads) : ControllerBase
{
    /// <summary>Opens (or resumes) the participant's draft for a competition + category.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.SubmissionWrite)]
    public async Task<ActionResult<SubmissionDto>> Start(StartSubmissionRequest req, CancellationToken ct)
    {
        var (dto, created) = await submissions.StartAsync(User.GetUserId(), req, ct);
        return created ? CreatedAtAction(nameof(Get), new { id = dto.Id }, dto) : Ok(dto);
    }

    [HttpGet("{id:int}")]
    public Task<SubmissionDto> Get(int id, CancellationToken ct) => submissions.GetAsync(User.GetUserId(), id, ct);

    /// <summary>Saves draft answers (partial is fine). Shape errors come back per field.</summary>
    [HttpPut("{id:int}")]
    [EnableRateLimiting(RateLimitPolicies.SubmissionWrite)]
    public Task<SubmissionDto> Save(int id, SaveAnswersRequest req, CancellationToken ct) =>
        submissions.SaveAnswersAsync(User.GetUserId(), id, req, ct);

    /// <summary>
    /// Multipart upload of exactly one file (form fields "fieldKey" and "file"). Several files are several requests;
    /// the per-submission count is enforced by the service. The request body limit is the configured file size plus
    /// 1 MB of multipart framing ([UploadBodyLimit]), set before the form is read — larger bodies are never buffered.
    /// </summary>
    [HttpPost("{id:int}/files")]
    [EnableRateLimiting(RateLimitPolicies.Upload)]
    [UploadBodyLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)] // never the binding limit: the body limit above is smaller
    public async Task<ActionResult<SubmissionFileDto>> Upload(int id, CancellationToken ct)
    {
        var (form, file) = await UploadForms.ReadSingleFileAsync(Request, "file", uploads.Value, ct);
        var dto = await submissions.UploadAsync(User.GetUserId(), id, form["fieldKey"], file, ct);
        return Created($"/api/submissions/{id}/files/{dto.Id}", dto);
    }

    [HttpDelete("{id:int}/files/{fileId:int}")]
    public async Task<IActionResult> DeleteFile(int id, int fileId, CancellationToken ct)
    {
        await submissions.DeleteFileAsync(User.GetUserId(), id, fileId, ct);
        return NoContent();
    }

    /// <summary>Streams a stored file to its owner (or an admin, once submitted). Never executable in the browser.</summary>
    [HttpGet("{id:int}/files/{fileId:int}")]
    public async Task<IActionResult> Download(int id, int fileId, CancellationToken ct)
    {
        var (content, file) = await submissions.OpenFileAsync(User.GetUserId(), User.IsInRole(Roles.Admin), id, fileId, ct);
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(file.OriginalFileName);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return File(content, file.ContentType);
    }

    /// <summary>
    /// The card-size copy (WEBP, metadata-free) of a stored file, for lists: same access rule as the original (its
    /// owner, or an admin once submitted). 404 until the copy exists — lists then show a placeholder and never fall
    /// back to downloading the original. Cached privately and revalidated (304) on every use, so access is re-checked.
    /// </summary>
    [HttpGet("{id:int}/files/{fileId:int}/thumb")]
    public async Task<IActionResult> Thumbnail(int id, int fileId, CancellationToken ct)
    {
        var (content, etag) = await submissions.OpenThumbnailAsync(User.GetUserId(), User.IsInRole(Roles.Admin), id, fileId, ct);
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";
        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            await content.DisposeAsync();
            return StatusCode(StatusCodes.Status304NotModified);
        }
        return File(content, FileTypes.Webp.MimeType);
    }

    /// <summary>Runs every final-submission check without submitting (preview step).</summary>
    [HttpPost("{id:int}/check")]
    [EnableRateLimiting(RateLimitPolicies.SubmissionWrite)]
    public Task<SubmissionDto> Check(int id, CancellationToken ct) => submissions.CheckAsync(User.GetUserId(), id, ct);

    /// <summary>Final, intentional submission. Assigns the reference number; afterwards the item is read-only.</summary>
    [HttpPost("{id:int}/submit")]
    [EnableRateLimiting(RateLimitPolicies.SubmissionWrite)]
    public Task<SubmissionDto> Submit(int id, CancellationToken ct) => submissions.SubmitAsync(User.GetUserId(), id, ct);
}
