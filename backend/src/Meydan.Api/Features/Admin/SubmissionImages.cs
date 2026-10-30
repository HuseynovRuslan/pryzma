using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>A file a work could be pictured with on an admin sheet: only files of public fields, never hidden ones.</summary>
/// <param name="PreviewPage">For a PDF: the page an admin chose to stand for it (null: automatic).</param>
public sealed record ImageCandidate(
    int Id, string ContentType, int SortOrder, PublicAssetStatus PublicAssetStatus, PublicAssetStatus ThumbnailStatus,
    int? PublicAssetWidth, int? PublicAssetHeight, int? PreviewPage = null)
{
    public bool IsPdf => ContentType == "application/pdf";
}

/// <summary>
/// The picture that stands for a work on every admin screen and printed document — the jury's package, the review of
/// all works, the public vote's prints, the Final tab, the jury's rows, the vote ranking, the final show and the admin
/// lists: the admin's pick, else the first picture, else the first file (a PDF's copy is its first page) — among
/// public fields' files that are not hidden from the public. The metadata-free large copy when it is ready, else the
/// card-size one.
/// <para>
/// For a PDF, the page that stands for it is decided here and nowhere else (<see cref="RepresentativePage"/>): the
/// page an admin chose, else the second of two or more (a first page is often only a cover), else the first. The
/// choice is metadata only — the PDF itself is never changed, and every page stays in the full material.
/// </para>
/// </summary>
internal static class SubmissionImages
{
    /// <summary>
    /// The page of a <paramref name="pageCount"/>-page PDF that stands for it: the admin's choice when it is a page of
    /// the document, else page 2 of a document of two or more pages, else page 1 (owner, 7 October 2026).
    /// </summary>
    public static int RepresentativePage(int pageCount, int? chosen) =>
        chosen is { } page && page >= 1 && page <= pageCount ? page : pageCount >= 2 ? 2 : 1;

    /// <param name="pdfPages">Page counts of the work's PDFs (by file id) — needed to show a PDF by a page after its first.</param>
    public static AdminPrintImageDto? Pick(
        int? previewFileId, IEnumerable<ImageCandidate> files, IReadOnlyDictionary<int, int>? pdfPages = null)
    {
        var ordered = files
            .OrderBy(f => f.Id == previewFileId ? 0 : f.ContentType.StartsWith("image/", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(f => f.SortOrder).ThenBy(f => f.Id)
            .ToList();
        var large = ordered.FirstOrDefault(f => f.PublicAssetStatus == PublicAssetStatus.Ready);
        if (large is { IsPdf: true } && pdfPages is not null && pdfPages.TryGetValue(large.Id, out var count))
        {
            var page = RepresentativePage(count, large.PreviewPage);
            // Page 1 is the file's own safe copy, with its size; a later page is rendered on request.
            return page > 1
                ? new AdminPrintImageDto(large.Id, true, null, null, page, count)
                : new AdminPrintImageDto(large.Id, true, large.PublicAssetWidth, large.PublicAssetHeight, 1, count);
        }
        var small = large is null ? ordered.FirstOrDefault(f => f.ThumbnailStatus == PublicAssetStatus.Ready) : null;
        return large is not null
            ? new AdminPrintImageDto(large.Id, true, large.PublicAssetWidth, large.PublicAssetHeight)
            : small is not null ? new AdminPrintImageDto(small.Id, false, null, null) : null;
    }

    /// <summary>Each PDF among these files whose safe first page is ready, with its page count (as the ballot counts it).</summary>
    public static async Task<Dictionary<int, int>> PdfPageCountsAsync(
        IEnumerable<(ImageCandidate Candidate, string StoragePath)> files, DocumentPages pages, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        foreach (var (c, storagePath) in files.Where(f => f.Candidate.IsPdf && f.Candidate.PublicAssetStatus == PublicAssetStatus.Ready))
            counts[c.Id] = await pages.CountAsync(storagePath, ct);
        return counts;
    }

    /// <summary>The picture of each of the given works, read in one query.</summary>
    public static async Task<Dictionary<int, AdminPrintImageDto?>> ForAsync(
        IQueryable<Submission> works, DocumentPages pages, CancellationToken ct)
    {
        var rows = await works
            .Select(s => new
            {
                s.Id,
                s.PreviewFileId,
                Files = s.Files
                    .Where(f => f.Field != null && f.Field.IsPublicAfterResults && !f.HiddenFromPublic)
                    .Select(f => new
                    {
                        Candidate = new ImageCandidate(f.Id, f.ContentType, f.SortOrder, f.PublicAssetStatus, f.ThumbnailStatus,
                            f.PublicAssetWidth, f.PublicAssetHeight, f.PreviewPage),
                        f.StoragePath,
                    })
                    .ToList(),
            })
            .AsSplitQuery()
            .ToListAsync(ct);
        var images = new Dictionary<int, AdminPrintImageDto?>();
        foreach (var r in rows)
        {
            var counts = await PdfPageCountsAsync(r.Files.Select(f => (f.Candidate, f.StoragePath)), pages, ct);
            images[r.Id] = Pick(r.PreviewFileId, r.Files.Select(f => f.Candidate), counts);
        }
        return images;
    }

    /// <summary>
    /// The page an admin list's thumbnail shows for each of these files: for a PDF its representative page (only those
    /// other than 1 are returned), for anything else nothing — the list's own card-size copy is the picture.
    /// </summary>
    public static async Task<Dictionary<int, int>> ListPreviewPagesAsync(
        MeydanDbContext db, DocumentPages pages, IEnumerable<int> fileIds, CancellationToken ct)
    {
        var ids = fileIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var pdfs = await db.SubmissionFiles.AsNoTracking()
            .Where(f => ids.Contains(f.Id) && f.ContentType == "application/pdf" && f.PublicAssetStatus == PublicAssetStatus.Ready)
            .Select(f => new { f.Id, f.StoragePath, f.PreviewPage })
            .ToListAsync(ct);
        var result = new Dictionary<int, int>();
        foreach (var f in pdfs)
            if (RepresentativePage(await pages.CountAsync(f.StoragePath, ct), f.PreviewPage) is var page && page > 1)
                result[f.Id] = page;
        return result;
    }

    /// <summary>A list's preview with the page that stands for it, from <see cref="ListPreviewPagesAsync"/>.</summary>
    public static AdminPreviewDto? WithPage(AdminPreviewDto? preview, IReadOnlyDictionary<int, int> pages) =>
        preview is not null && pages.TryGetValue(preview.FileId, out var page) ? preview with { Page = page } : preview;

    /// <summary>The admin list's rows, each preview with the page that stands for it.</summary>
    public static async Task<List<AdminSubmissionListItem>> WithPreviewPagesAsync(
        List<AdminSubmissionListItem> items, MeydanDbContext db, DocumentPages pages, CancellationToken ct)
    {
        var previewPages = await ListPreviewPagesAsync(db, pages, items.Where(i => i.Preview is not null).Select(i => i.Preview!.FileId), ct);
        return previewPages.Count == 0 ? items : [.. items.Select(i => i with { Preview = WithPage(i.Preview, previewPages) })];
    }
}
