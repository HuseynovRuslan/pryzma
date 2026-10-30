namespace Meydan.Domain.Enums;

/// <summary>What an admin decided about a project's visibility.</summary>
public enum ProjectStatus
{
    /// <summary>Being prepared; never public.</summary>
    Draft = 0,
    /// <summary>Public.</summary>
    Published = 1,
    /// <summary>Withdrawn from the site, kept with all its data.</summary>
    Archived = 2,
}

/// <summary>
/// What an admin decided about a competition. Whether it accepts work right now is a separate, computed
/// "phase" (see CompetitionPhases): a Published competition before its start date is "upcoming", after its
/// end date "closed" — without anyone changing the stored status.
/// </summary>
public enum CompetitionStatus
{
    /// <summary>Being prepared; never public.</summary>
    Draft = 0,
    /// <summary>Public; accepts submissions within its optional date window.</summary>
    Published = 1,
    /// <summary>Public, but closed for submissions regardless of dates.</summary>
    Closed = 2,
    /// <summary>Withdrawn from the site, kept with all its data.</summary>
    Archived = 3,
}

public enum SubmissionStatus
{
    /// <summary>Being filled in; editable and visible only to its owner. Not a final submission.</summary>
    Draft = 0,
    /// <summary>Final. Has a reference number; the participant can no longer change it.</summary>
    Submitted = 1,
    /// <summary>An admin is checking it.</summary>
    UnderReview = 2,
    Approved = 3,
    Rejected = 4,
    /// <summary>
    /// Legacy value kept for stored data compatibility; never set. The winner is a competition result
    /// (<see cref="Entities.CompetitionResult"/>), not a moderation status.
    /// </summary>
    Winner = 5,
    Withdrawn = 6,
}

/// <summary>Input types an admin can use when building a competition's submission form.</summary>
public enum CompetitionFieldType
{
    ShortText = 0,
    LongText = 1,
    Number = 2,
    Url = 3,
    Select = 4,
    Checkbox = 5,
    Date = 6,
    /// <summary>Uploaded files; limits and accepted types come from the field's OptionsJson within the global upload policy.</summary>
    File = 7,
}

/// <summary>Result of the optional antivirus/content scanner for an upload.</summary>
public enum FileScanStatus
{
    /// <summary>No scanner is configured; only the built-in type/signature/content checks ran.</summary>
    NotScanned = 0,
    Clean = 1,
    Infected = 2,
}

/// <summary>State of the public-safe derivative of a submission file (re-encoded image or PDF page preview).</summary>
public enum PublicAssetStatus
{
    /// <summary>Not generated yet; generated when an admin reviews the work or publishes results.</summary>
    Pending = 0,
    Ready = 1,
    /// <summary>The original could not be converted safely; the public side shows a placeholder, never the original.</summary>
    Failed = 2,
}

/// <summary>
/// Where a competition's public vote stands. Computed on the server from the voting window so that a browser
/// with a wrong clock — or a hopeful one — cannot decide that voting is open.
/// </summary>
public enum VotingState
{
    /// <summary>No voting window has been set; the competition has no public vote.</summary>
    NotConfigured = 0,
    /// <summary>A window exists and has not started.</summary>
    Upcoming = 1,
    Open = 2,
    Closed = 3,
}
