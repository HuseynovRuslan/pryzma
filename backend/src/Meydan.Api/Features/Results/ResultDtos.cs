using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Results;

// Public shapes. They carry only what an admin chose to make public after results: the reference number, the
// category, the work's title, the answers/files of fields marked "public after results" — and, for the WINNERS of a
// published result only, the winner's name. Never anyone's contact details, never a non-winner's identity, never
// moderation status, notes, history, original file names or storage keys.

public sealed record GalleryCategoryDto(string Slug, string Name);

/// <summary>
/// A public-safe picture of a work: <c>Url</c> is the server-encoded copy (longest side 1600 px), <c>ThumbUrl</c> the
/// card-size copy for grids (served from the same gate). Width/Height are the copy's pixel size, for layout.
/// </summary>
public sealed record PublicImageDto(string Url, string ThumbUrl, int? Width, int? Height);

/// <summary>Why a card shows what it shows.</summary>
public enum PreviewState
{
    /// <summary>A public-safe image is available.</summary>
    Ready,
    /// <summary>The safe copy is still being made; the card says so instead of guessing.</summary>
    Processing,
    /// <summary>No safe copy could be made (corrupt or unsupported file): a placeholder, never the original.</summary>
    Unavailable,
    /// <summary>No file of this work is public.</summary>
    None,
}

/// <summary>A card in the gallery or on the results page. Title is null when the title field is not public.</summary>
public sealed record GalleryItemDto(
    string Reference,
    string? Title,
    GalleryCategoryDto? Category,
    bool IsWinner,
    /// <summary>The winner's name — set only for winners of a published result; null for everybody else.</summary>
    string? WinnerName,
    PublicImageDto? Preview,
    PreviewState PreviewState,
    /// <summary>1, 2 or 3 for a placed work of a competition without categories; null everywhere else.</summary>
    int? Place,
    /// <summary>The podium places this work holds in a two-choice result; null when it holds none.</summary>
    IReadOnlyList<PublicItemAwardDto>? Awards = null);

/// <summary>A podium place a work holds: which choice (the jury's or the public's), the place and its number.</summary>
public sealed record PublicItemAwardDto(AwardCategory Category, int Place, int Points);

/// <summary>One place of a podium: the place, its number (the jury's points or the valid votes), the announced name and the work.</summary>
public sealed record PublicAwardDto(int Place, int Points, string ParticipantName, GalleryItemDto Work);

/// <summary>
/// The two independent choices of a result (owner, 16 October 2026), each in place order. Never combined: there is no
/// sum, no overall winner, and the jury's protocol, sources and individual marks are not part of it.
/// </summary>
public sealed record PublicAwardsDto(IReadOnlyList<PublicAwardDto> Jury, IReadOnlyList<PublicAwardDto> Public);

public sealed record GalleryAnswerDto(string Label, CompetitionFieldType FieldType, string Value);

/// <summary>
/// A public-safe image: "image" = re-encoded copy of an uploaded image, "document-preview" = first page of a PDF
/// rendered to an image. Originals (and PDFs themselves) are never public.
/// </summary>
/// <param name="Page">For "document-page": which page of the document (2, 3, …); the first page is the "document-preview".</param>
public sealed record GalleryFileDto(int Id, string Kind, string Url, string ThumbUrl, int? Width, int? Height, string Name, int? Page = null);

public sealed record GalleryWorkDto(
    string Reference,
    string? Title,
    GalleryCategoryDto? Category,
    bool IsWinner,
    string? WinnerName,
    PublicImageDto? Preview,
    PreviewState PreviewState,
    IReadOnlyList<GalleryAnswerDto> Answers,
    IReadOnlyList<GalleryFileDto> Files,
    int? Place,
    IReadOnlyList<PublicItemAwardDto>? Awards = null);

public sealed record ResultCompetitionDto(
    string Slug,
    string Title,
    string ShortDescription,
    string? CategoryLabel,
    bool HasCategories,
    ProjectRefDto Project);

/// <summary>A category (settlement) on the results page: how many works it received and its winner, if chosen.</summary>
public sealed record ResultCategoryDto(string Slug, string Name, int EntryCount, GalleryItemDto? Winner);

public sealed record CompetitionResultDto(
    ResultCompetitionDto Competition,
    DateTimeOffset PublishedAt,
    string? Announcement,
    /// <summary>The admin's results banner; null when none was uploaded (never a submission's image).</summary>
    string? CoverImage,
    int EntryCount,
    int WinnerCount,
    /// <summary>Every category in its order, with or without a winner. Empty for competitions without categories.</summary>
    IReadOnlyList<ResultCategoryDto> Categories,
    /// <summary>The single winner of a competition without categories — its first place.</summary>
    GalleryItemDto? OverallWinner,
    /// <summary>First, second and third place of a competition without categories, in order; only those awarded.</summary>
    IReadOnlyList<GalleryItemDto> Places,
    /// <summary>How each place was reached, when the places came from a jury final (D13); null otherwise.</summary>
    PublicFinalDto? Final = null,
    /// <summary>The jury's and the public's choices of a two-choice result; null for any other result.</summary>
    PublicAwardsDto? Awards = null);

/// <summary>
/// The jury final behind a result (D13): how many jurors marked the works and the most they could give together.
/// Jurors are counted, never named.
/// </summary>
public sealed record PublicFinalDto(int JurorCount, decimal JuryMax, IReadOnlyList<PublicFinalPlaceDto> Places);

/// <summary>
/// One placed work's numbers, as announced: its valid (audited) votes and public rank among the finalists with the
/// points that gave, the jury's combined raw score and rank with the points that gave, and the sum.
/// </summary>
public sealed record PublicFinalPlaceDto(
    int Place, string Reference, int ValidVotes, int PublicRank, int PublicPoints,
    decimal JuryTotal, int JuryRank, int JuryPoints, int FinalScore);

/// <summary>Where a results card's picture comes from: the admin's results banner, else the competition's or project's cover.</summary>
public enum ResultCoverKind { Result, Competition, Project }

public sealed record ResultListItemDto(
    ResultCompetitionDto Competition,
    DateTimeOffset PublishedAt,
    string? CoverImage,
    ResultCoverKind? CoverKind,
    int CategoryCount,
    int WinnerCount,
    int EntryCount);

public sealed record GalleryCategoryCountDto(string Slug, string Name, int Count);

public sealed record GalleryDto(
    ResultCompetitionDto Competition,
    DateTimeOffset PublishedAt,
    IReadOnlyList<GalleryCategoryCountDto> Categories,
    IReadOnlyList<GalleryItemDto> Items,
    int Total,
    int WinnerCount);
