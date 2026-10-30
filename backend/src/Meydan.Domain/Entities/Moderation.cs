using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

/// <summary>Internal remark by staff on a submission. Never shown to the participant.</summary>
public class SubmissionNote
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public Guid AuthorId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    public Submission Submission { get; set; } = null!;
    public ApplicationUser Author { get; set; } = null!;
}

/// <summary>One step in a submission's life: who moved it from which status to which, and when.</summary>
public class SubmissionStatusChange
{
    public long Id { get; set; }
    public int SubmissionId { get; set; }
    public SubmissionStatus FromStatus { get; set; }
    public SubmissionStatus ToStatus { get; set; }
    public Guid? ChangedById { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? Comment { get; set; }

    public Submission Submission { get; set; } = null!;
    public ApplicationUser? ChangedBy { get; set; }
}

/// <summary>
/// Append-only record of administrative actions. Holds identifiers and a short human summary only — never
/// passwords, tokens, personal data or submitted content.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
    public const string Published = "Published";
    public const string Unpublished = "Unpublished";
    public const string Closed = "Closed";
    public const string Archived = "Archived";
    public const string CoverChanged = "CoverChanged";
    public const string CategoriesImported = "CategoriesImported";
    public const string CategoriesReordered = "CategoriesReordered";
    public const string FieldsReordered = "FieldsReordered";
    public const string StatusChanged = "StatusChanged";
    public const string NoteAdded = "NoteAdded";
    /// <summary>Overall winner (competitions without categories).</summary>
    public const string WinnerSelected = "WinnerSelected";
    public const string WinnerChanged = "WinnerChanged";
    public const string WinnerRemoved = "WinnerRemoved";
    /// <summary>A category's (settlement's) winner; the summary names the category and the submission reference.</summary>
    public const string CategoryWinnerSelected = "CategoryWinnerSelected";
    public const string CategoryWinnerChanged = "CategoryWinnerChanged";
    public const string CategoryWinnerRemoved = "CategoryWinnerRemoved";
    public const string ResultCoverChanged = "ResultCoverChanged";
    public const string ResultsPublished = "ResultsPublished";
    /// <summary>
    /// One podium of a two-choice result — the jury's or the public's — recorded whole (owner, 16 October 2026): the
    /// summary names the choice, each place's work, announced name and number, and the source of the numbers.
    /// </summary>
    public const string AwardsSet = "AwardsSet";
    /// <summary>A podium taken off a result that was not yet public; the summary keeps what it held.</summary>
    public const string AwardsCleared = "AwardsCleared";
    public const string ResultsUnpublished = "ResultsUnpublished";
    public const string ResultUpdated = "ResultUpdated";
    public const string PreviewChanged = "PreviewChanged";
    /// <summary>A file taken off, or put back on, every public page; the summary names the file.</summary>
    public const string FileHidden = "FileHidden";
    public const string FileShown = "FileShown";
    /// <summary>The page that stands for a PDF was chosen, or given back to the automatic choice; the summary names both.</summary>
    public const string PdfPreviewPageChanged = "PdfPreviewPageChanged";
    /// <summary>
    /// A member of the public cast a vote. The entity is the submission; the summary names the nomination. Written
    /// in the same transaction as the vote itself, so a vote without its record, or a record without its vote,
    /// cannot exist.
    /// </summary>
    public const string VoteCast = "VoteCast";
    /// <summary>An admin set a vote aside as fraudulent; the summary is the work and the reason (D12).</summary>
    public const string VoteInvalidated = "VoteInvalidated";
    /// <summary>An admin restored a vote set aside earlier; the summary is the work and the reason.</summary>
    public const string VoteRestored = "VoteRestored";
    /// <summary>
    /// A competition-wide rule applied to a competition's votes in one decision (D14): the summary names the rule, its
    /// version, the count per work and the reason. Each vote set aside has its own VoteInvalidated entry as well.
    /// </summary>
    public const string VotingRuleApplied = "VotingRuleApplied";
    /// <summary>
    /// An admin moved, opened or cleared the public voting window. Its own line, because under D3 the vote
    /// decides who receives a prize: when the window changed, and who changed it, is not a detail of an edit.
    /// </summary>
    public const string VotingWindowChanged = "VotingWindowChanged";
    public const string QrlogLinked = "QrlogLinked";
    public const string QrlogUnlinked = "QrlogUnlinked";
    /// <summary>Demo content added by `seed-demo` / taken away by `remove-demo`; the summary holds the counts.</summary>
    public const string DemoSeeded = "DemoSeeded";
    public const string DemoRemoved = "DemoRemoved";
    /// <summary>
    /// An admin removed content for good, with everything under it. The summary keeps what went — the slug or
    /// reference and the dependency counts — because the row it describes no longer exists to be looked up.
    /// </summary>
    public const string ProjectPermanentlyDeleted = "ProjectPermanentlyDeleted";
    public const string CompetitionPermanentlyDeleted = "CompetitionPermanentlyDeleted";
    public const string SubmissionPermanentlyDeleted = "SubmissionPermanentlyDeleted";
    /// <summary>An account that held no work was removed; the summary keeps the name and e-mail it had.</summary>
    public const string ParticipantPermanentlyDeleted = "ParticipantPermanentlyDeleted";
    /// <summary>The platform's own name or public copy was edited from the admin; the summary says which part.</summary>
    public const string PlatformBrandSettingsUpdated = "PlatformBrandSettingsUpdated";
    /// <summary>
    /// The jury final (D13). The entity is the competition. Frozen: the ten works with their valid votes, ranks and
    /// points, and the vote state they were read from (totals and fingerprint). Scores: which juror's marks changed
    /// from what to what. Unlock and tie decisions carry the admin's reason.
    /// </summary>
    public const string FinalistsFrozen = "FinalistsFrozen";
    /// <summary>A frozen ten replaced by a new freeze: the summary keeps the old snapshot in full and the reason.</summary>
    public const string FinalistsSuperseded = "FinalistsSuperseded";

    // The jury's evaluation of every work (InternalJuryRankingV1). Summaries name jurors by their official
    // name and works by reference — never an invitation secret, an access code, a session or an address.
    public const string JuryEvaluationCreated = "JuryEvaluationCreated";
    public const string JuryRosterFrozen = "JuryRosterFrozen";
    public const string JuryJurorAssigned = "JuryJurorAssigned";
    public const string JuryJurorUnassigned = "JuryJurorUnassigned";
    public const string JuryInvitationIssued = "JuryInvitationIssued";
    public const string JuryInvitationRevoked = "JuryInvitationRevoked";
    public const string JurorSignedIn = "JurorSignedIn";
    public const string JurorSignInFailed = "JurorSignInFailed";
    public const string JurorIdentityConfirmed = "JurorIdentityConfirmed";
    public const string JurorSignedOut = "JurorSignedOut";
    /// <summary>A juror's score set or changed online: the summary names the work, old → new.</summary>
    public const string JuryWorkScored = "JuryWorkScored";
    /// <summary>Scores entered by an admin from a juror's paper sheet, on that juror's behalf.</summary>
    public const string JuryPaperScoresEntered = "JuryPaperScoresEntered";
    public const string JuryEvaluationSubmitted = "JuryEvaluationSubmitted";
    public const string JuryEvaluationReopened = "JuryEvaluationReopened";
    public const string FinalJurorAdded = "FinalJurorAdded";
    public const string FinalJurorRemoved = "FinalJurorRemoved";
    /// <summary>A juror's name on the admin's screens and sheets changed; the summary names the old and the new.</summary>
    public const string FinalJurorRenamed = "FinalJurorRenamed";
    public const string JuryScoresChanged = "JuryScoresChanged";
    public const string JuryLocked = "JuryLocked";
    public const string JuryUnlocked = "JuryUnlocked";
    public const string FinalTieBreakRecorded = "FinalTieBreakRecorded";
    public const string FinalPublished = "FinalPublished";
}
