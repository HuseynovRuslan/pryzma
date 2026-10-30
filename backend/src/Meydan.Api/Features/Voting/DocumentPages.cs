using Meydan.Infrastructure.Files;
using Microsoft.Extensions.Caching.Memory;

namespace Meydan.Api.Features.Voting;

/// <summary>
/// Every page of a PDF entry, not only its first. A brand book or a presentation is read inside; a ballot that shows
/// only its cover is unfair to it next to a work whose every picture is shown.
/// <para>
/// Pages after the first are made exactly as the first one is — rendered by PDFium from the private original, with
/// no annotations, forms or scripts, and re-encoded as a metadata-free WEBP — but on the first request rather than
/// at upload, and kept in memory, so no stored file and no schema change is needed while a vote is running. The PDF
/// itself is never served. Rendering goes through <see cref="PublicDerivatives"/>' own gate, so it can never take
/// more of the machine than the upload pipeline may.
/// </para>
/// </summary>
public sealed class DocumentPages(IFileStorage storage, PublicDerivatives derivatives, IMemoryCache cache)
{
    /// <summary>The most pages shown of one document — enough for any brand book here, and a bound on the work.</summary>
    public const int MaxPages = 40;

    private static readonly TimeSpan Keep = TimeSpan.FromHours(12);

    /// <summary>
    /// How many pages of the document are shown (1 when it cannot be read, or its original is missing: its first page
    /// is all there is) — a list of works never fails over one unreadable file.
    /// </summary>
    public async Task<int> CountAsync(string originalKey, CancellationToken ct) =>
        await cache.GetOrCreateAsync(("pdf-pages", originalKey), async entry =>
        {
            entry.Size = 1;
            entry.SlidingExpiration = Keep;
            try
            {
                return Math.Clamp(derivatives.PdfPageCount(await ReadAsync(originalKey, ct)), 1, MaxPages);
            }
            catch (Exception ex) when (ex is DerivativeException or FileNotFoundException or DirectoryNotFoundException)
            {
                return 1;
            }
        });

    /// <summary>Page <paramref name="page"/> (1-based) as a public-safe image, full size or card size.</summary>
    public async Task<PublicImage> PageAsync(string originalKey, int page, bool thumbnail, CancellationToken ct)
    {
        var full = await cache.GetOrCreateAsync(("pdf-page", originalKey, page), async entry =>
        {
            var image = await derivatives.FromPdfPageAsync(await ReadAsync(originalKey, ct), page - 1, ct);
            entry.Size = image.Bytes.Length;
            entry.SlidingExpiration = Keep;
            return image;
        });
        if (!thumbnail) return full!;
        return (await cache.GetOrCreateAsync(("pdf-page-thumb", originalKey, page), async entry =>
        {
            var image = await derivatives.ThumbnailAsync(full!.Bytes, ct);
            entry.Size = image.Bytes.Length;
            entry.SlidingExpiration = Keep;
            return image;
        }))!;
    }

    private async Task<byte[]> ReadAsync(string key, CancellationToken ct)
    {
        await using var source = storage.OpenRead(key);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
