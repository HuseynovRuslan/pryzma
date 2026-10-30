using Meydan.Api.Features.Voting;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// The works as the admin's printed documents show them — the jury's package, the review of all works, and the public
/// vote's two documents: reference, title, nomination, the public description, the picture the gallery would show,
/// and, for an internal document, the author's name. Never a contact or registration detail.
/// <para>
/// Each work also lists its whole material for an optional appendix: every public picture and every page of every
/// public PDF — files of public fields only, never one an admin hid from the public. A PDF is counted by its pages,
/// rendered as the ballot renders them (<see cref="DocumentPages"/>); the PDF itself is never served.
/// </para>
/// <para>
/// When the card's picture is a PDF, the card shows the page that stands for it — the admin's choice, else the second
/// of two or more pages (the first is often only a cover), else the first: <see cref="SubmissionImages"/> decides, the
/// same for every admin screen. Nothing in the file changes, and the material still lists every page.
/// </para>
/// </summary>
public sealed class PrintItems(MeydanDbContext db, DocumentPages pages)
{
    private sealed record PrintFile(ImageCandidate Candidate, string StoragePath);

    /// <summary>The works with these ids, in this order. The author's name is read only when asked for.</summary>
    public async Task<List<AdminPrintItemDto>> ForAsync(IReadOnlyList<int> ids, bool includeAuthor, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var rows = (await db.Submissions.AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .Select(s => new
                {
                    s.Id, s.ReferenceNumber, s.Title, s.CompetitionId, s.PreviewFileId,
                    s.Competition.HasCategories,
                    Category = s.Category == null ? null : s.Category.Name,
                    Answers = s.Answers.Select(a => new { a.CompetitionFieldId, a.Value }).ToList(),
                    Files = s.Files
                        .Where(f => f.Field != null && f.Field.IsPublicAfterResults && !f.HiddenFromPublic)
                        .Select(f => new PrintFile(
                            new ImageCandidate(f.Id, f.ContentType, f.SortOrder, f.PublicAssetStatus, f.ThumbnailStatus,
                                f.PublicAssetWidth, f.PublicAssetHeight, f.PreviewPage),
                            f.StoragePath))
                        .ToList(),
                })
                .AsSplitQuery()
                .ToListAsync(ct))
            .ToDictionary(r => r.Id);

        // Names only when asked for, in their own query: a document printed without them never reads one.
        var authors = includeAuthor
            ? await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new { s.Id, Name = s.User.FirstName + " " + s.User.LastName })
                .ToDictionaryAsync(x => x.Id, x => x.Name.Trim(), ct)
            : null;

        var competitionIds = rows.Values.Select(r => r.CompetitionId).Distinct().ToList();
        var fields = (await db.CompetitionFields.AsNoTracking()
                .Where(f => competitionIds.Contains(f.CompetitionId) && f.IsPublicAfterResults)
                .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                .Select(f => new { f.Id, f.CompetitionId, f.Label, f.FieldType, f.IsSubmissionTitle })
                .ToListAsync(ct))
            .ToLookup(f => f.CompetitionId);

        var items = new List<AdminPrintItemDto>(ids.Count);
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var r)) continue;
            var publicFields = fields[r.CompetitionId].ToList();
            string? Answer(int fieldId) =>
                r.Answers.FirstOrDefault(a => a.CompetitionFieldId == fieldId)?.Value is { } v && !string.IsNullOrWhiteSpace(v)
                    ? v.Trim()
                    : null;

            // The same field the ballot's card describes a work with (PublicVoting.Gate.ConceptFieldId).
            var concept = publicFields.FirstOrDefault(f => f.FieldType == CompetitionFieldType.LongText && !f.IsSubmissionTitle);
            var facts = publicFields
                .Where(f => f.FieldType == CompetitionFieldType.Select && !f.IsSubmissionTitle)
                .Select(f => (f.Label, Value: Answer(f.Id)))
                .Where(x => x.Value is not null)
                .Select(x => new AdminPrintFactDto(x.Label, x.Value!))
                .ToList();

            var counts = await SubmissionImages.PdfPageCountsAsync(r.Files.Select(f => (f.Candidate, f.StoragePath)), pages, ct);
            // The gallery's choice, in the gallery's order — for a PDF, the page that stands for it (SubmissionImages).
            var image = SubmissionImages.Pick(r.PreviewFileId, r.Files.Select(f => f.Candidate), counts);
            var (material, pdfPages) = Material(r.PreviewFileId, r.Files, counts);

            items.Add(new AdminPrintItemDto(
                r.Id, r.ReferenceNumber, r.Title, r.HasCategories ? r.Category : null, facts,
                concept?.Label, concept is null ? null : Answer(concept.Id), image, authors?.GetValueOrDefault(r.Id),
                pdfPages, material));
        }
        return items;
    }

    /// <summary>
    /// The work's whole material in the gallery's reading order — the chosen picture, the other pictures, then each
    /// PDF page by page — and how many PDF pages there are. A file whose safe copy is not ready is left out.
    /// </summary>
    private static (List<AdminPrintMaterialDto> Material, int PdfPages) Material(
        int? previewFileId, IReadOnlyList<PrintFile> files, IReadOnlyDictionary<int, int> pdfCounts)
    {
        var material = new List<AdminPrintMaterialDto>();
        var pdfPages = 0;
        var ordered = files
            .OrderBy(f => f.Candidate.Id == previewFileId ? 0 : f.Candidate.ContentType.StartsWith("image/", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(f => f.Candidate.SortOrder).ThenBy(f => f.Candidate.Id);
        foreach (var f in ordered)
        {
            var c = f.Candidate;
            var ready = c.PublicAssetStatus == PublicAssetStatus.Ready;
            if (c.ContentType == "application/pdf")
            {
                if (!ready || !pdfCounts.TryGetValue(c.Id, out var count)) continue;
                pdfPages += count;
                for (var p = 1; p <= count; p++) material.Add(new AdminPrintMaterialDto(c.Id, "pdf", p, count));
            }
            else if (ready)
            {
                material.Add(new AdminPrintMaterialDto(c.Id, "image", 1, 1));
            }
        }
        return (material, pdfPages);
    }
}
