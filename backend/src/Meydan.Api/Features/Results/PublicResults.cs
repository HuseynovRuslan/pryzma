using System.Security.Cryptography;
using System.Text;
using Meydan.Api.Common;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Results;

/// <summary>
/// Read side for published results and the post-result gallery. Every public method goes through
/// <see cref="PublishedAsync"/>: until an admin publishes the result (and the competition has stopped accepting
/// work) the winners, the entries and their files do not exist as far as the public API is concerned — the same
/// 404 whether the competition is unknown, a draft, or its result is still private.
/// <para>
/// Identity: a published result names its winners (a business decision: winning is public). Names are loaded for
/// winning submissions only, so a non-winner's name is never even read by this class.
/// </para>
/// </summary>
public sealed class PublicResults(MeydanDbContext db, IFileStorage storage, TimeProvider clock)
{
    private const string NotPublished = "Nəticələr hələ açıqlanmayıb.";

    private sealed record Gate(
        Competition Competition, CompetitionResult Result, IReadOnlyList<PublicField> Fields,
        IReadOnlyDictionary<int, string> WinnerNames, IReadOnlyDictionary<int, int> Places, IReadOnlyList<Award> Awards);

    /// <summary>A podium place of a two-choice result, as published.</summary>
    private sealed record Award(AwardCategory Category, int Place, int SubmissionId, int Points, string ParticipantName);

    private sealed record PublicField(int Id, string Label, CompetitionFieldType Type, bool IsTitle, int SortOrder);

    /// <summary>A submission file as the public side sees it: only the state of its public-safe copies.</summary>
    private sealed record EntryFile(
        int Id, int? FieldId, string ContentType, PublicAssetStatus Asset, int? Width, int? Height, int SortOrder, bool Hidden);

    private sealed record Entry(
        int Id, string Reference, string Title, int? CategoryId, string? CategorySlug, string? CategoryName, int CategoryOrder,
        DateTimeOffset? SubmittedAt, int? PreviewFileId, IReadOnlyList<EntryFile> Files);

    public async Task<IReadOnlyList<ResultListItemDto>> ListAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.Competitions.AsNoTracking()
            .Include(c => c.Project)
            .Include(c => c.Result)
            .Where(c => c.Result != null && c.Result.PublishedAt != null
                        && c.Project.Status == ProjectStatus.Published
                        && (c.Status == CompetitionStatus.Published || c.Status == CompetitionStatus.Closed))
            .ToListAsync(ct);

        var list = new List<ResultListItemDto>();
        foreach (var c in candidates.Where(c => CompetitionPhases.AreResultsPublic(c, c.Result!.PublishedAt, now)))
        {
            var result = c.Result!;
            var categories = c.HasCategories ? await VisibleCategoryCountAsync(c.Id, ct) : 0;
            var won = await db.CompetitionCategoryWinners.Where(w => w.CompetitionId == c.Id).Select(w => w.SubmissionId).ToListAsync(ct);
            won.AddRange(Enumerable.Range(1, CompetitionResult.PlaceCount).Select(result.HolderOf).OfType<int>());
            won.AddRange(await db.CompetitionAwards.Where(a => a.CompetitionId == c.Id).Select(a => a.SubmissionId).ToListAsync(ct));
            var winners = won.Distinct().Count();
            var entries = await Eligible(c.Id).CountAsync(ct);
            // The admin's results banner first; a submission's image is never picked automatically.
            var (cover, kind) = result.CoverImage is { } rc ? (rc, ResultCoverKind.Result)
                : c.CoverImage is { } cc ? (cc, ResultCoverKind.Competition)
                : c.Project.CoverImage is { } pc ? (pc, (ResultCoverKind?)ResultCoverKind.Project)
                : (null, null);
            list.Add(new ResultListItemDto(CompetitionDto(c), result.PublishedAt!.Value, cover, kind, categories, winners, entries));
        }
        return list.OrderByDescending(r => r.PublishedAt).ToList();
    }

    public async Task<CompetitionResultDto> GetAsync(string slug, CancellationToken ct)
    {
        var gate = await PublishedAsync(slug, ct);
        var entries = await EntriesAsync(gate, null, ct);
        var winners = await db.CompetitionCategoryWinners.AsNoTracking()
            .Where(w => w.CompetitionId == gate.Competition.Id)
            .ToDictionaryAsync(w => w.CompetitionCategoryId, w => w.SubmissionId, ct);

        var categories = new List<ResultCategoryDto>();
        foreach (var k in await CategoriesAsync(gate.Competition.Id, ct))
        {
            var winner = winners.TryGetValue(k.Id, out var id) ? entries.FirstOrDefault(e => e.Id == id) : null;
            var count = entries.Count(e => e.CategoryId == k.Id);
            if (!k.IsActive && count == 0 && winner is null) continue;
            if (!gate.Competition.HasCategories && winner is null) continue;
            categories.Add(new ResultCategoryDto(k.Slug, k.Name, count, winner is null ? null : Card(gate, winner)));
        }
        var overall = gate.Result.OverallWinnerSubmissionId is { } oid ? entries.FirstOrDefault(e => e.Id == oid) : null;
        var places = entries.Where(e => gate.Places.ContainsKey(e.Id)).OrderBy(e => gate.Places[e.Id])
            .Select(e => Card(gate, e)).ToList();

        return new CompetitionResultDto(CompetitionDto(gate.Competition), gate.Result.PublishedAt!.Value,
            gate.Result.Announcement, gate.Result.CoverImage, entries.Count, gate.WinnerNames.Count,
            categories, overall is null ? null : Card(gate, overall), places, await FinalAsync(gate, ct), Awards(gate, entries));
    }

    /// <summary>
    /// The two choices of a two-choice result, each in place order, with the work's public card. Only works still
    /// in the gallery (eligible) are shown — the publication refuses anything else.
    /// </summary>
    private static PublicAwardsDto? Awards(Gate gate, List<Entry> entries)
    {
        if (gate.Awards.Count == 0) return null;
        var byId = entries.ToDictionary(e => e.Id);
        List<PublicAwardDto> Of(AwardCategory category) => gate.Awards
            .Where(a => a.Category == category && byId.ContainsKey(a.SubmissionId))
            .OrderBy(a => a.Place)
            .Select(a => new PublicAwardDto(a.Place, a.Points, a.ParticipantName, Card(gate, byId[a.SubmissionId])))
            .ToList();
        return new PublicAwardsDto(Of(AwardCategory.Jury), Of(AwardCategory.Public));
    }

    /// <summary>
    /// The final's numbers for the placed works, as stored when the final was published — never recalculated from the
    /// votes. Only for a place still held by the work the final put there: a place an admin changed by hand afterwards
    /// has no final behind it, and none is claimed.
    /// </summary>
    private async Task<PublicFinalDto?> FinalAsync(Gate gate, CancellationToken ct)
    {
        if (gate.Places.Count == 0) return null;
        var final = await db.CompetitionFinals.AsNoTracking()
            .Where(f => f.CompetitionId == gate.Competition.Id && f.Stage == FinalStage.Published)
            .Select(f => new
            {
                Jurors = f.Jurors.Count,
                Placed = f.Finalists.Where(x => x.FinalPlace != null && x.FinalPlace <= CompetitionResult.PlaceCount)
                    .Select(x => new
                    {
                        x.SubmissionId, Place = x.FinalPlace!.Value, Reference = x.Submission.ReferenceNumber,
                        x.ValidVotes, x.PublicRank, x.PublicPoints, x.JuryTotal, x.JuryRank, x.JuryPoints, x.FinalScore,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (final is null) return null;
        var rows = final.Placed
            .Where(x => gate.Places.TryGetValue(x.SubmissionId, out var held) && held == x.Place
                        && x.JuryTotal is not null && x.JuryRank is not null && x.JuryPoints is not null && x.FinalScore is not null)
            .OrderBy(x => x.Place)
            .Select(x => new PublicFinalPlaceDto(x.Place, x.Reference ?? "", x.ValidVotes, x.PublicRank, x.PublicPoints,
                x.JuryTotal!.Value, x.JuryRank!.Value, x.JuryPoints!.Value, x.FinalScore!.Value))
            .ToList();
        return rows.Count == 0 ? null
            : new PublicFinalDto(final.Jurors, final.Jurors * CompetitionFinal.CriteriaCount * CompetitionFinal.CriterionMax, rows);
    }

    public async Task<GalleryDto> GalleryAsync(string slug, string? category, CancellationToken ct)
    {
        var gate = await PublishedAsync(slug, ct);
        var all = await EntriesAsync(gate, null, ct);
        var categories = gate.Competition.HasCategories
            ? (await CategoriesAsync(gate.Competition.Id, ct))
                .Select(x => new GalleryCategoryCountDto(x.Slug, x.Name, all.Count(e => e.CategoryId == x.Id)))
                .Where(x => x.Count > 0)
                .ToList()
            : [];
        var items = string.IsNullOrWhiteSpace(category) ? all : all.Where(e => e.CategorySlug == category).ToList();
        return new GalleryDto(CompetitionDto(gate.Competition), gate.Result.PublishedAt!.Value, categories,
            items.Select(e => Card(gate, e)).ToList(), all.Count, gate.WinnerNames.Count);
    }

    public async Task<GalleryWorkDto> WorkAsync(string slug, string reference, CancellationToken ct)
    {
        var gate = await PublishedAsync(slug, ct);
        var entry = (await EntriesAsync(gate, reference, ct)).FirstOrDefault()
                    ?? throw new NotFoundException("Bu iş qalereyada yoxdur.");
        return await WorkAsync(gate, entry, ct);
    }

    /// <summary>
    /// A public-safe copy of a gallery entry's file — never the original: the 1600 px derivative, or with
    /// <paramref name="thumbnail"/> the card-size copy (the derivative itself if that copy is not made yet). Only for
    /// files of fields that are public after results, whose derivative is ready, of an eligible entry of a published
    /// result; re-checked on every request, so an old URL stops working the moment any of that changes. The caller
    /// never learns a storage key or the participant's original file name.
    /// </summary>
    public async Task<(Stream Content, FileType Type, string DownloadName, string ETag)> OpenFileAsync(
        string slug, string reference, int fileId, bool thumbnail, CancellationToken ct)
    {
        var gate = await PublishedAsync(slug, ct);
        var entry = (await EntriesAsync(gate, reference, ct)).FirstOrDefault()
                    ?? throw new NotFoundException("Fayl tapılmadı.");
        var publicFiles = PublicFiles(gate, entry);
        var index = publicFiles.FindIndex(f => f.Id == fileId);
        if (index < 0) throw new NotFoundException("Fayl tapılmadı.");

        var stored = await db.SubmissionFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.SubmissionId == entry.Id && f.PublicAssetStatus == PublicAssetStatus.Ready)
            .Select(f => new { f.PublicAssetKey, f.ThumbnailKey, f.ThumbnailStatus })
            .FirstOrDefaultAsync(ct);
        var key = thumbnail && stored?.ThumbnailStatus == PublicAssetStatus.Ready ? stored.ThumbnailKey : stored?.PublicAssetKey;
        if (key is null || !StorageAreas.IsIn(key, StorageAreas.Derived))
            throw new NotFoundException("Fayl tapılmadı.");
        var type = FileTypes.ByExtension(Path.GetExtension(key)) ?? throw new NotFoundException("Fayl tapılmadı.");
        Stream content;
        try
        {
            content = storage.OpenRead(key);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new NotFoundException("Fayl tapılmadı.");
        }
        return (content, type, $"{entry.Reference}-{index + 1}{type.CanonicalExtension}", ETagOf(key));
    }

    /// <summary>A weak-enough validator that says nothing about the key itself.</summary>
    public static string ETagOf(string key) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)))[..20] + "\"";

    // ---- The gate ---------------------------------------------------------------------------------

    private async Task<Gate> PublishedAsync(string slug, CancellationToken ct)
    {
        var c = await db.Competitions.AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Result)
            .FirstOrDefaultAsync(x => x.Slug == slug && x.Project.Status == ProjectStatus.Published, ct);
        if (c?.Result is null || !CompetitionPhases.AreResultsPublic(c, c.Result.PublishedAt, clock.GetUtcNow()))
            throw new NotFoundException(NotPublished);

        // Past the gate: the result is public, so its winners — every placed work among them — are named. Only
        // winners' names are read.
        var ids = await db.CompetitionCategoryWinners.AsNoTracking()
            .Where(w => w.CompetitionId == c.Id).Select(w => w.SubmissionId).ToListAsync(ct);
        var places = new Dictionary<int, int>();
        for (var p = 1; p <= CompetitionResult.PlaceCount; p++)
        {
            if (c.Result.HolderOf(p) is not { } held) continue;
            ids.Add(held);
            if (!c.HasCategories) places[held] = p;
        }
        var names = await db.Submissions.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && CompetitionResult.EligibleStatuses.Contains(s.Status))
            .Select(s => new { s.Id, s.User.FirstName, s.User.LastName })
            .ToDictionaryAsync(s => s.Id, s => $"{s.FirstName} {s.LastName}".Trim(), ct);

        // A two-choice result names the works on its podiums with the names it announced — never read from accounts.
        var awards = await db.CompetitionAwards.AsNoTracking()
            .Where(a => a.CompetitionId == c.Id && CompetitionResult.EligibleStatuses.Contains(a.Submission.Status))
            .OrderBy(a => a.Category).ThenBy(a => a.Place)
            .Select(a => new Award(a.Category, a.Place, a.SubmissionId, a.Points, a.ParticipantName))
            .ToListAsync(ct);
        foreach (var a in awards)
            names.TryAdd(a.SubmissionId, a.ParticipantName);
        return new Gate(c, c.Result, await PublicFieldsAsync(c.Id, ct), names, places, awards);
    }

    private Task<List<PublicField>> PublicFieldsAsync(int competitionId, CancellationToken ct) =>
        db.CompetitionFields.AsNoTracking()
            .Where(f => f.CompetitionId == competitionId && f.IsPublicAfterResults)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
            .Select(f => new PublicField(f.Id, f.Label, f.FieldType, f.IsSubmissionTitle, f.SortOrder))
            .ToListAsync(ct);

    private IQueryable<Submission> Eligible(int competitionId) =>
        db.Submissions.AsNoTracking().Where(s => s.CompetitionId == competitionId
                                                 && CompetitionResult.EligibleStatuses.Contains(s.Status)
                                                 && s.ReferenceNumber != null);

    private sealed record CategoryRow(int Id, string Slug, string Name, bool IsActive);

    private Task<List<CategoryRow>> CategoriesAsync(int competitionId, CancellationToken ct) =>
        db.CompetitionCategories.AsNoTracking()
            .Where(x => x.CompetitionId == competitionId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new CategoryRow(x.Id, x.Slug, x.Name, x.IsActive))
            .ToListAsync(ct);

    /// <summary>Categories shown on the results page: the active ones, plus switched-off ones that received work or hold a winner.</summary>
    private Task<int> VisibleCategoryCountAsync(int competitionId, CancellationToken ct) =>
        db.CompetitionCategories.CountAsync(k => k.CompetitionId == competitionId
            && (k.IsActive || k.Winner != null
                || k.Submissions.Any(s => CompetitionResult.EligibleStatuses.Contains(s.Status) && s.ReferenceNumber != null)), ct);

    /// <summary>Final, not rejected/withdrawn entries of the competition, grouped by category; each winner first in its category.</summary>
    private async Task<List<Entry>> EntriesAsync(Gate gate, string? reference, CancellationToken ct)
    {
        var query = Eligible(gate.Competition.Id);
        if (reference is not null) query = query.Where(s => s.ReferenceNumber == reference);

        var rows = await query
            .Select(s => new Entry(
                s.Id, s.ReferenceNumber!, s.Title,
                s.CompetitionCategoryId,
                s.Category != null ? s.Category.Slug : null,
                s.Category != null ? s.Category.Name : null,
                s.Category != null ? s.Category.SortOrder : 0,
                s.SubmittedAt,
                s.PreviewFileId,
                s.Files.OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                    .Select(f => new EntryFile(f.Id, f.CompetitionFieldId, f.ContentType, f.PublicAssetStatus,
                        f.PublicAssetWidth, f.PublicAssetHeight, f.SortOrder, f.HiddenFromPublic))
                    .ToList()))
            .ToListAsync(ct);

        return rows
            .OrderBy(e => e.CategoryOrder)
            .ThenBy(e => e.CategoryName)
            .ThenByDescending(e => gate.WinnerNames.ContainsKey(e.Id))
            .ThenBy(e => gate.Places.GetValueOrDefault(e.Id, int.MaxValue))
            .ThenBy(e => e.SubmittedAt)
            .ToList();
    }

    // ---- Shaping ----------------------------------------------------------------------------------

    private static ResultCompetitionDto CompetitionDto(Competition c) =>
        new(c.Slug, c.Title, c.ShortDescription, c.CategoryLabel, c.HasCategories,
            new ProjectRefDto(c.Project.Slug, c.Project.Name, c.Project.AccentColor));

    private static string? PublicTitle(Gate gate, Entry e) =>
        gate.Fields.Any(f => f.IsTitle) && !string.IsNullOrWhiteSpace(e.Title) ? e.Title : null;

    /// <summary>
    /// Files of public File fields (any state) that an admin has not hidden: what the public side may know exists.
    /// </summary>
    private static List<EntryFile> PublicFieldFiles(Gate gate, Entry e)
    {
        var fileFields = gate.Fields.Where(f => f.Type == CompetitionFieldType.File).Select(f => f.Id).ToHashSet();
        return e.Files.Where(f => !f.Hidden && f.FieldId is { } id && fileFields.Contains(id)).ToList();
    }

    /// <summary>Files of public File fields whose public-safe derivative is ready. Pending or failed ones stay hidden.</summary>
    private static List<EntryFile> PublicFiles(Gate gate, Entry e) =>
        PublicFieldFiles(gate, e).Where(f => f.Asset == PublicAssetStatus.Ready).ToList();

    private static bool IsImage(EntryFile f) => f.ContentType.StartsWith("image/", StringComparison.Ordinal);

    private static string FileUrl(Gate gate, Entry e, int fileId) =>
        $"/api/competitions/{gate.Competition.Slug}/gallery/{e.Reference}/files/{fileId}";

    private static PublicImageDto Image(Gate gate, Entry e, EntryFile f)
    {
        var url = FileUrl(gate, e, f.Id);
        return new PublicImageDto(url, url + "/thumb", f.Width, f.Height);
    }

    /// <summary>The admin's choice if it is still public-safe, else the first image, else a PDF page preview.</summary>
    private static (PublicImageDto? Image, PreviewState State) Preview(Gate gate, Entry e)
    {
        var ready = PublicFiles(gate, e);
        var chosen = ready.FirstOrDefault(f => f.Id == e.PreviewFileId) ?? ready.FirstOrDefault(IsImage) ?? ready.FirstOrDefault();
        if (chosen is not null) return (Image(gate, e, chosen), PreviewState.Ready);
        var all = PublicFieldFiles(gate, e);
        return all.Count == 0 ? (null, PreviewState.None)
            : all.Any(f => f.Asset == PublicAssetStatus.Pending) ? (null, PreviewState.Processing)
            : (null, PreviewState.Unavailable);
    }

    /// <summary>
    /// The work's category — in a competition that has categories. Without them a work's nomination is only the
    /// vote's filing (one "Ümumi" list) and saying it on every card would suggest a choice that never existed.
    /// </summary>
    private static GalleryCategoryDto? Category(Gate gate, Entry e) =>
        e.CategorySlug is null || !gate.Competition.HasCategories ? null : new GalleryCategoryDto(e.CategorySlug, e.CategoryName!);

    private static GalleryItemDto Card(Gate gate, Entry e)
    {
        var (image, state) = Preview(gate, e);
        var isWinner = gate.WinnerNames.TryGetValue(e.Id, out var name);
        int? place = isWinner && gate.Places.TryGetValue(e.Id, out var p) ? p : null;
        var awards = gate.Awards.Where(a => a.SubmissionId == e.Id).Select(a => new PublicItemAwardDto(a.Category, a.Place, a.Points)).ToList();
        return new GalleryItemDto(e.Reference, PublicTitle(gate, e), Category(gate, e), isWinner, isWinner ? name : null,
            image, state, place, awards.Count == 0 ? null : awards);
    }

    private async Task<GalleryWorkDto> WorkAsync(Gate gate, Entry e, CancellationToken ct)
    {
        var textFields = gate.Fields.Where(f => f.Type != CompetitionFieldType.File && !f.IsTitle).ToList();
        var ids = textFields.Select(f => f.Id).ToList();
        var answers = await db.SubmissionAnswers.AsNoTracking()
            .Where(a => a.SubmissionId == e.Id && ids.Contains(a.CompetitionFieldId) && a.Value != null && a.Value != "")
            .Select(a => new { a.CompetitionFieldId, a.FieldLabel, a.Value })
            .ToListAsync(ct);

        var shownAnswers = textFields
            .Select(f => (Field: f, Answer: answers.FirstOrDefault(a => a.CompetitionFieldId == f.Id)))
            .Where(x => x.Answer is not null)
            .Select(x => new GalleryAnswerDto(x.Answer!.FieldLabel ?? x.Field.Label, x.Field.Type, x.Answer.Value!))
            .ToList();

        // Shown in reading order: the chosen preview, other images, then PDF page previews. Names keep the stable
        // upload position, matching the download names ("SUB-…-2.webp").
        var publicFiles = PublicFiles(gate, e);
        var files = publicFiles
            .OrderByDescending(f => f.Id == e.PreviewFileId)
            .ThenByDescending(IsImage)
            .Select(f =>
            {
                var url = FileUrl(gate, e, f.Id);
                return new GalleryFileDto(f.Id, IsImage(f) ? "image" : "document-preview", url, url + "/thumb", f.Width, f.Height,
                    $"Fayl {publicFiles.IndexOf(f) + 1}");
            })
            .ToList();

        var card = Card(gate, e);
        return new GalleryWorkDto(e.Reference, card.Title, card.Category, card.IsWinner, card.WinnerName,
            card.Preview, card.PreviewState, shownAnswers, files, card.Place, card.Awards);
    }
}
