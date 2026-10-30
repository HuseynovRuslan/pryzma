namespace Meydan.Infrastructure.Files;

/// <summary>
/// Platform-wide upload ceiling (config section "Uploads"). A competition's File field can narrow these
/// limits through its OptionsJson but can never exceed them or add a format outside AllowedTypes.
/// </summary>
public sealed class UploadOptions
{
    public const string Section = "Uploads";

    /// <summary>Directory for stored files; relative paths resolve against the app's content root. Keep it outside wwwroot.</summary>
    public string StorageRoot { get; set; } = "App_Data/uploads";
    public int MaxFileSizeMb { get; set; } = 10;
    /// <summary>Total files across all File fields of one submission.</summary>
    public int MaxFilesPerSubmission { get; set; } = 5;
    /// <summary>
    /// Keys from <see cref="FileTypes"/>; unknown keys are ignored, empty means every known type.
    /// Deliberately no initializer: the configuration binder appends to an initialised array.
    /// </summary>
    public string[] AllowedTypes { get; set; } = [];

    public IReadOnlyList<FileType> AllowedFileTypes =>
        AllowedTypes.Length == 0
            ? FileTypes.All
            : AllowedTypes.Select(FileTypes.ByKey).OfType<FileType>().Distinct().ToList();

    public long MaxFileSizeBytes => MaxFileSizeMb * 1024L * 1024L;

    /// <summary>Decompression-bomb guard: images above this pixel count are refused before decoding.</summary>
    public long MaxImagePixels { get; set; } = 40_000_000;
    /// <summary>Longest side accepted for an uploaded image.</summary>
    public int MaxImageSide { get; set; } = 12_000;
    /// <summary>Longest side of a public derivative (gallery / results image).</summary>
    public int PublicImageMaxSide { get; set; } = 1600;
    /// <summary>WEBP quality of public derivatives (0–100).</summary>
    public int PublicImageQuality { get; set; } = 82;
    /// <summary>
    /// Longest side of the card-size copy used by grids and admin lists: sharp at 2× density up to a 320 px card,
    /// a few dozen kilobytes instead of the multi-megabyte original.
    /// </summary>
    public int ThumbnailMaxSide { get; set; } = 640;
    /// <summary>WEBP quality of the card-size copy (0–100).</summary>
    public int ThumbnailQuality { get; set; } = 78;
    /// <summary>Image/PDF conversions running at the same time (memory bound).</summary>
    public int MaxConcurrentImageJobs { get; set; } = 2;

    // Background derivative generation (PublicAssetWorker).
    /// <summary>Pending files read from the database per backfill step (earlier uploads, interrupted work).</summary>
    public int DerivativeBackfillBatchSize { get; set; } = 20;
    /// <summary>Pause after each backfilled file, so a large backlog cannot monopolise CPU or disk.</summary>
    public int DerivativeBackfillPauseMs { get; set; } = 200;
    /// <summary>How often the backfill runs again to pick up anything still pending.</summary>
    public int DerivativeSweepMinutes { get; set; } = 10;
    /// <summary>New-upload notifications held in memory; beyond this the next sweep picks the file up.</summary>
    public int DerivativeQueueCapacity { get; set; } = 1000;
}
