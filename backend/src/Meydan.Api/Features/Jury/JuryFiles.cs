using Meydan.Api.Common;
using Meydan.Api.Features.Results;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Jury;

/// <summary>
/// The pictures a signed-in juror sees: the same public-safe copies the ballot serves — files of public fields, never
/// one an admin hid, never an original, never a PDF itself — for the works of their own evaluation's roster only,
/// checked against the session on every request, so revoking an invitation stops the pictures at once.
/// </summary>
public sealed class JuryFiles(MeydanDbContext db, IFileStorage storage, DocumentPages pages)
{
    private const string Missing = "Fayl tapılmadı.";

    /// <param name="page">1 for the file itself (a picture, or a PDF's first page); 2, 3, … for a PDF's later pages.</param>
    public async Task<(Stream Content, string ETag)> OpenForJurorAsync(
        JuryAssignment juror, int submissionId, int fileId, int page, bool thumbnail, CancellationToken ct)
    {
        if (!await db.JuryRosterEntries.AnyAsync(r => r.EvaluationId == juror.EvaluationId && r.SubmissionId == submissionId, ct))
            throw new NotFoundException(Missing);
        return await OpenAsync(submissionId, fileId, page, thumbnail, ct);
    }

    private async Task<(Stream Content, string ETag)> OpenAsync(int submissionId, int fileId, int page, bool thumbnail, CancellationToken ct)
    {
        var file = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == submissionId && f.Field != null && f.Field.IsPublicAfterResults
                        && !f.HiddenFromPublic && f.PublicAssetStatus == PublicAssetStatus.Ready)
            .Select(f => new { f.PublicAssetKey, f.ThumbnailKey, f.ThumbnailStatus, f.StoragePath, f.ContentType })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(Missing);

        if (page == 1)
        {
            var key = thumbnail && file.ThumbnailStatus == PublicAssetStatus.Ready ? file.ThumbnailKey : file.PublicAssetKey;
            if (key is null || !StorageAreas.IsIn(key, StorageAreas.Derived)) throw new NotFoundException(Missing);
            try
            {
                return (storage.OpenRead(key), PublicResults.ETagOf(key));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
            {
                throw new NotFoundException(Missing);
            }
        }

        if (file.ContentType != "application/pdf" || page < 2 || page > await pages.CountAsync(file.StoragePath, ct))
            throw new NotFoundException("Səhifə tapılmadı.");
        try
        {
            var image = await pages.PageAsync(file.StoragePath, page, thumbnail, ct);
            return (new MemoryStream(image.Bytes, writable: false), PublicResults.ETagOf($"{file.StoragePath}#{page}{(thumbnail ? "t" : "")}"));
        }
        catch (DerivativeException)
        {
            throw new NotFoundException("Səhifə tapılmadı.");
        }
    }
}
