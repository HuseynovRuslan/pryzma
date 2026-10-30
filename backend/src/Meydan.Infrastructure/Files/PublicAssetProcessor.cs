using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meydan.Infrastructure.Files;

/// <summary>
/// Makes the public-safe copies of one stored file: the metadata-free derivative (1600 px WEBP, a PDF's first page)
/// and its card-size thumbnail. Used by <see cref="PublicAssetWorker"/> for uploads and backfills, and by anything
/// else that imports files (the demo seeder) — so every file goes through exactly the same rules. The private
/// original is only ever read.
/// </summary>
public sealed class PublicAssetProcessor(
    IServiceScopeFactory scopes,
    IFileStorage storage,
    PublicDerivatives derivatives,
    TimeProvider clock,
    ILogger<PublicAssetProcessor> logger)
{
    /// <summary>Derivative + thumbnail for a Pending file; a file that cannot be converted ends as Failed (placeholder), never shown as the original.</summary>
    public async Task ProcessAsync(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var file = await db.SubmissionFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || file.PublicAssetStatus != PublicAssetStatus.Pending) return;

        try
        {
            byte[] original;
            try
            {
                await using var stream = storage.OpenRead(file.StoragePath);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                original = buffer.ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new DerivativeException("original-missing", "Orijinal fayl tapılmadı.");
            }

            var type = FileTypes.ByExtension(Path.GetExtension(file.StoragePath));
            PublicImage image;
            try
            {
                image = type == FileTypes.Pdf
                    ? await derivatives.FromPdfFirstPageAsync(original, ct)
                    : PublicDerivatives.RasterTypes.Contains(type)
                        ? await derivatives.FromRasterAsync(original, ct)
                        : throw new DerivativeException("unsupported", "Bu fayl növü üçün önizləmə yaradılmır.");
            }
            catch (Exception ex) when (ex is not DerivativeException and not OperationCanceledException)
            {
                // A decoder choking on a malformed file is a property of the file: retrying will not help.
                logger.LogWarning("Public derivative for file {FileId}: decoder error {ErrorType}", id, ex.GetType().Name);
                throw new DerivativeException("conversion-error", "Önizləmə yaradıla bilmədi.");
            }

            var key = await storage.SaveAsync(new MemoryStream(image.Bytes, writable: false), ".webp", ct, StorageAreas.Derived);
            file.PublicAssetKey = key;
            file.PublicAssetStatus = PublicAssetStatus.Ready;
            file.PublicAssetError = null;
            file.PublicAssetWidth = image.Width;
            file.PublicAssetHeight = image.Height;
            file.PublicAssetCreatedAt = clock.GetUtcNow();
            await AddThumbnailAsync(file, image.Bytes, ct);
        }
        catch (DerivativeException ex)
        {
            file.PublicAssetStatus = PublicAssetStatus.Failed;
            file.PublicAssetError = ex.Code;
            file.ThumbnailStatus = PublicAssetStatus.Failed;
            logger.LogWarning("Public derivative for file {FileId} not created: {Code}", id, ex.Code);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Card-size copy of a fresh derivative. A failure here never costs the derivative itself.</summary>
    private async Task AddThumbnailAsync(SubmissionFile file, byte[] derivative, CancellationToken ct)
    {
        try
        {
            var thumb = await derivatives.ThumbnailAsync(derivative, ct);
            file.ThumbnailKey = await storage.SaveAsync(new MemoryStream(thumb.Bytes, writable: false), ".webp", ct, StorageAreas.Derived);
            file.ThumbnailStatus = PublicAssetStatus.Ready;
            file.ThumbnailWidth = thumb.Width;
            file.ThumbnailHeight = thumb.Height;
        }
        catch (Exception ex) when (ex is DerivativeException or ArgumentException)
        {
            file.ThumbnailStatus = PublicAssetStatus.Failed;
            logger.LogWarning("Thumbnail for file {FileId} not created: {ErrorType}", file.Id, ex.GetType().Name);
        }
    }

    public async Task ThumbnailFromDerivativeAsync(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var file = await db.SubmissionFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null || file.PublicAssetStatus != PublicAssetStatus.Ready || file.ThumbnailStatus != PublicAssetStatus.Pending) return;
        byte[] derivative;
        try
        {
            await using var stream = storage.OpenRead(file.PublicAssetKey!);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            derivative = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            file.ThumbnailStatus = PublicAssetStatus.Failed;
            await db.SaveChangesAsync(ct);
            return;
        }
        await AddThumbnailAsync(file, derivative, ct);
        await db.SaveChangesAsync(ct);
    }
}
