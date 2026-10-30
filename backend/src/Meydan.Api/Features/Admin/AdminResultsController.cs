using Meydan.Api.Common;
using Meydan.Infrastructure.Files;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// A competition's result: one winner per category (settlement) — chosen, replaced or withdrawn explicitly — the
/// announcement, the results banner, and publishing. Choosing winners never makes anything public by itself.
/// </summary>
[Route("api/admin/competitions/{competitionId:int}/result")]
public sealed class AdminResultsController(ResultAdministration results, AwardAdministration awards, IOptions<UploadOptions> uploads)
    : AdminControllerBase
{
    [HttpGet]
    public Task<AdminResultDto> Get(int competitionId, CancellationToken ct) => results.GetAsync(competitionId, ct);

    /// <summary>Chooses the winner of the submission's own category. 409 if that category has one — use PUT to replace it.</summary>
    [HttpPost("winners")]
    public Task<AdminResultDto> Select(int competitionId, SelectWinnerRequest req, CancellationToken ct) =>
        results.SelectAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>Replaces the winner of the submission's category; the current winner is named in the request.</summary>
    [HttpPut("winners")]
    public Task<AdminResultDto> Change(int competitionId, ChangeWinnerRequest req, CancellationToken ct) =>
        results.ChangeAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>Withdraws a work's win; its category has no winner again.</summary>
    [HttpDelete("winners/{submissionId:int}")]
    public Task<AdminResultDto> Remove(int competitionId, int submissionId, [FromQuery] bool confirmPublished, CancellationToken ct) =>
        results.RemoveAsync(competitionId, submissionId, confirmPublished, CurrentUserId, ct);

    /// <summary>
    /// The first, second and third place of a competition without categories, saved together — the screen fills
    /// them in one go (from the vote, or by hand) and a stale screen is refused.
    /// </summary>
    [HttpPut("places")]
    public Task<AdminResultDto> Places(int competitionId, SetPlacesRequest req, CancellationToken ct) =>
        results.SetPlacesAsync(competitionId, req, CurrentUserId, ct);

    [HttpPut("announcement")]
    public Task<AdminResultDto> Announcement(int competitionId, AnnouncementRequest req, CancellationToken ct) =>
        results.SetAnnouncementAsync(competitionId, req.Announcement, CurrentUserId, ct);

    /// <summary>The results banner (multipart field "cover"): PNG/JPG/WEBP, stored as a metadata-free WEBP.</summary>
    [HttpPost("cover")]
    [UploadBodyLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<AdminResultDto> UploadCover(int competitionId, CancellationToken ct)
    {
        var (_, file) = await UploadForms.ReadSingleFileAsync(Request, "cover", uploads.Value, ct);
        return await results.SetCoverAsync(competitionId, file, uploads.Value, CurrentUserId, ct);
    }

    [HttpDelete("cover")]
    public Task<AdminResultDto> RemoveCover(int competitionId, CancellationToken ct) =>
        results.RemoveCoverAsync(competitionId, CurrentUserId, ct);

    [HttpPost("publish")]
    public Task<AdminResultDto> Publish(int competitionId, CancellationToken ct) =>
        results.PublishAsync(competitionId, CurrentUserId, ct);

    /// <summary>The two podiums of a two-choice result (the jury's and the public's), with every work that can stand on one.</summary>
    [HttpGet("awards")]
    public Task<AdminAwardsDto> Awards(int competitionId, CancellationToken ct) => awards.GetAsync(competitionId, ct);

    /// <summary>One podium — "jury" or "public" — whole, three places; a stale screen and a silent change of a published result are refused.</summary>
    [HttpPut("awards/{category}")]
    public Task<AdminAwardsDto> SetAwards(int competitionId, string category, SetAwardsRequest req, CancellationToken ct) =>
        awards.SetAsync(competitionId, CategoryOf(category), req, CurrentUserId, ct);

    /// <summary>Takes one podium off a result that is not public yet.</summary>
    [HttpDelete("awards/{category}")]
    public Task<AdminAwardsDto> ClearAwards(int competitionId, string category, CancellationToken ct) =>
        awards.ClearAsync(competitionId, CategoryOf(category), CurrentUserId, ct);

    private static Domain.Entities.AwardCategory CategoryOf(string category) =>
        Enum.TryParse<Domain.Entities.AwardCategory>(category, ignoreCase: true, out var c) && Enum.IsDefined(c)
            ? c
            : throw new NotFoundException("Belə nəticə kateqoriyası yoxdur.");

    [HttpPost("unpublish")]
    public Task<AdminResultDto> Unpublish(int competitionId, CancellationToken ct) =>
        results.UnpublishAsync(competitionId, CurrentUserId, ct);
}
