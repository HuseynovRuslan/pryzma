using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

public class SubmissionFile
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    /// <summary>The File-type form field this upload answers.</summary>
    public int? CompetitionFieldId { get; set; }
    /// <summary>Sanitised original name, kept as metadata only — never used to build a path.</summary>
    public string OriginalFileName { get; set; } = "";
    /// <summary>Random storage key relative to the file store root, e.g. 2026/09/3f2a…e1.png.</summary>
    public string StoragePath { get; set; } = "";
    /// <summary>Canonical MIME type of the format detected from the file's signature.</summary>
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public FileScanStatus ScanStatus { get; set; }

    // Public-safe derivative. The original above is private, never modified and never served publicly; the gallery
    // only ever serves this re-encoded copy, and only while the competition's result is published.
    public string? PublicAssetKey { get; set; }
    public PublicAssetStatus PublicAssetStatus { get; set; }
    /// <summary>Short machine code of a failed conversion ("too-large", "pdf-unreadable"…), never file content.</summary>
    public string? PublicAssetError { get; set; }
    public int? PublicAssetWidth { get; set; }
    public int? PublicAssetHeight { get; set; }
    public DateTimeOffset? PublicAssetCreatedAt { get; set; }

    // Card-size copy of the derivative (longest side UploadOptions.ThumbnailMaxSide) for grids and admin lists, so a
    // small preview never downloads a multi-megabyte original. Made together with the derivative; same privacy rules.
    public string? ThumbnailKey { get; set; }
    public PublicAssetStatus ThumbnailStatus { get; set; }
    public int? ThumbnailWidth { get; set; }
    public int? ThumbnailHeight { get; set; }

    /// <summary>
    /// An admin took this file off every public page — the ballot, the work's page, the gallery — while the work
    /// stays in the competition with its other files (e.g. a picture that names its author during an anonymous vote,
    /// D2). The file itself and its copies are untouched, and putting it back is one click.
    /// </summary>
    public bool HiddenFromPublic { get; set; }

    /// <summary>
    /// For a PDF: the page (1-based) an admin chose to stand for the work on every admin screen and printed card; null
    /// leaves the choice automatic (page 2 of two or more, else page 1). Metadata only — the PDF itself is never
    /// changed, and every page stays in the work's full material.
    /// </summary>
    public int? PreviewPage { get; set; }

    public Submission Submission { get; set; } = null!;
    public CompetitionField? Field { get; set; }
}

/// <summary>A participant's value for one <see cref="CompetitionField"/>.</summary>
public class SubmissionAnswer
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public int CompetitionFieldId { get; set; }
    public string? Value { get; set; }
    /// <summary>
    /// The field's label as the participant saw it, frozen at final submission, so renaming a field later
    /// never changes the question a submitted answer belongs to.
    /// </summary>
    public string? FieldLabel { get; set; }

    public Submission Submission { get; set; } = null!;
    public CompetitionField Field { get; set; } = null!;
}
