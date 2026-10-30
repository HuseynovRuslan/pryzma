namespace Meydan.Domain.Entities;

/// <summary>How a competition's jury works.</summary>
public static class JuryFormats
{
    /// <summary>
    /// The owner's decision of 8 October 2026 (simplified the same day): every juror scores every eligible work with
    /// one whole number from 1 to 10, and the exact average ranks the works for the administration — an internal ranking
    /// only. The jury publishes nothing and awards no prize; the public vote's results stay as they are.
    /// </summary>
    public const string InternalJuryRankingV1 = "InternalJuryRankingV1";
}

public enum JuryEvaluationStatus
{
    /// <summary>Created; the roster is not frozen yet and nobody can score.</summary>
    Draft,
    /// <summary>The roster is frozen and the jurors score.</summary>
    Open,
}

/// <summary>
/// The jury's evaluation of every eligible work of one competition (<see cref="JuryFormats.InternalJuryRankingV1"/>).
/// Its existence closes the earlier combined Top 10 final (public points plus jury points) for marks, the show and
/// publication; its records stay readable as history. The public vote and its results are not touched.
/// </summary>
public class JuryEvaluation
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public string Format { get; set; } = JuryFormats.InternalJuryRankingV1;
    public JuryEvaluationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    /// <summary>When the list of works was frozen; it never changes once a score exists.</summary>
    public DateTimeOffset? RosterFrozenAt { get; set; }
    public Guid? RosterFrozenById { get; set; }

    /// <summary>PostgreSQL xmin: two admins acting at once never both win.</summary>
    public uint Version { get; set; }

    public Competition Competition { get; set; } = null!;
    public List<JuryRosterEntry> Roster { get; set; } = [];
    public List<JuryAssignment> Assignments { get; set; } = [];

    public const int MinScore = 1;
    public const int MaxScore = 10;

    /// <summary>A score is one whole number from 1 to 10; there is no zero and no half point.</summary>
    public static bool IsValidScore(int score) => score is >= MinScore and <= MaxScore;
}

/// <summary>
/// One work of the frozen list every juror scores, with the reference it was frozen under. The order is the reference
/// order, the same on the printed catalog, the paper score sheet and the online list.
/// </summary>
public class JuryRosterEntry
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    /// <summary>The evaluation's competition, so the work is bound to it by a composite key.</summary>
    public int CompetitionId { get; set; }
    public int SubmissionId { get; set; }
    public string Reference { get; set; } = "";
    public int SortOrder { get; set; }

    public JuryEvaluation Evaluation { get; set; } = null!;
    public Submission Submission { get; set; } = null!;
}

/// <summary>
/// A juror's part in one evaluation: their personal invitation (stored only as hashes), their sign-ins and whether they
/// have submitted — after which their scores are locked until an admin reopens them with a reason. The juror themself
/// is an existing <see cref="FinalJuror"/>: their official name is the admin's.
/// </summary>
public class JuryAssignment
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    public int JurorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Submitted: the scores are locked. Null while the juror is still scoring.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>The admin who completed it on the juror's behalf (paper sheet); null when the juror did it online.</summary>
    public Guid? SubmittedById { get; set; }

    /// <summary>SHA-256 of the invitation link's secret; the secret itself is shown once and never stored.</summary>
    public string? InviteTokenHash { get; set; }
    /// <summary>PBKDF2 hash of the separate access code (shown once, given to the juror apart from the link).</summary>
    public string? InviteCodeHash { get; set; }
    public DateTimeOffset? InviteIssuedAt { get; set; }
    public Guid? InviteIssuedById { get; set; }
    public DateTimeOffset? InviteExpiresAt { get; set; }
    public DateTimeOffset? InviteRevokedAt { get; set; }
    /// <summary>Wrong codes since the invitation was issued; at the limit it locks until the admin reissues it.</summary>
    public int FailedCodeAttempts { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
    /// <summary>When the juror confirmed, after signing in, that the name shown is theirs.</summary>
    public DateTimeOffset? IdentityConfirmedAt { get; set; }

    public uint Version { get; set; }

    public JuryEvaluation Evaluation { get; set; } = null!;
    public FinalJuror Juror { get; set; } = null!;
}

/// <summary>A juror's signed-in browser: only the hash of its cookie is stored; it ends, or is revoked with the invitation.</summary>
public class JurySession
{
    public int Id { get; set; }
    public int AssignmentId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public JuryAssignment Assignment { get; set; } = null!;
}

public enum JuryScoreSource
{
    /// <summary>Entered by the juror on the site.</summary>
    Online,
    /// <summary>Entered by an admin from the juror's paper score sheet.</summary>
    Paper,
}

/// <summary>
/// The one authoritative score of one juror for one work of one competition — whether it came from the site or from
/// paper. There is never a second: the database holds one row per competition, juror and work.
/// </summary>
public class JuryWorkScore
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    public int CompetitionId { get; set; }
    public int JurorId { get; set; }
    public int SubmissionId { get; set; }
    /// <summary>1 to 10, whole.</summary>
    public int Score { get; set; }
    /// <summary>The juror's private note; never shown to anyone but admins.</summary>
    public string? Comment { get; set; }
    public JuryScoreSource Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The admin who last wrote it (paper entry, reconciliation); null when the juror did.</summary>
    public Guid? UpdatedById { get; set; }

    public uint Version { get; set; }
}
