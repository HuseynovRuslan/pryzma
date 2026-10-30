using System.ComponentModel.DataAnnotations;
using Meydan.Api.Features.Admin;
using Meydan.Domain.Entities;

namespace Meydan.Api.Features.Jury;

// ---- The juror's side ------------------------------------------------------------------------------------------
// The juror sees the work, its public vote as the public saw it counted (position, valid votes, votes set aside —
// aggregate numbers only, owner, 8 October: informed evaluation) and their own score. Nothing here carries a voter, an
// audit rule or its evidence, another juror's score, an average or the jury's internal ranking.

public sealed record JurorSignInRequest
{
    /// <summary>The secret from the invitation link (its #fragment — never sent in a URL).</summary>
    [Required] public string Token { get; init; } = "";
    /// <summary>The access code given to the juror separately from the link.</summary>
    [Required] public string Code { get; init; } = "";
}

/// <param name="Confirmed">The juror has confirmed that the name shown is theirs.</param>
/// <param name="Locked">The juror has submitted: their scores are locked.</param>
public sealed record JurorSessionDto(
    string JurorName, int CompetitionId, string CompetitionTitle, bool Confirmed, bool Locked, string EvaluationStatus);

/// <summary>
/// A work's public vote as the jury sees it: its public position by valid votes (competition ranking — level works share
/// it, <paramref name="TieSize"/> of them), its valid votes and the votes set aside by approved audit decisions. Counts
/// only — no rule, no voter.
/// </summary>
public sealed record JuryPublicVoteDto(int Position, int TieSize, int ValidVotes, int InvalidatedVotes);

/// <summary>One work in the juror's list, in catalog order: its reference, title, picture, public vote and the juror's
/// own score if any.</summary>
public sealed record JurorWorkItemDto(
    int SubmissionId, string Reference, string? Title, AdminPrintImageDto? Image, JuryPublicVoteDto Vote, int? Score, bool HasComment);

public sealed record JurorDashboardDto(
    string JurorName, int CompetitionId, string CompetitionTitle, int Total, int Scored, int Remaining, bool Locked,
    DateTimeOffset? SubmittedAt, bool Open, IReadOnlyList<JurorWorkItemDto> Works);

/// <summary>
/// One work as the juror judges it: the public description, every public picture and PDF page and its public vote — no
/// author, no voter, no jury standing — with the juror's own score, its version for a safe save, and the neighbours in
/// the catalog order (<paramref name="Index"/> of <paramref name="Total"/>).
/// </summary>
public sealed record JurorWorkDto(
    int SubmissionId, string Reference, string? Title, string? Category, IReadOnlyList<AdminPrintFactDto> Facts,
    string? ConceptLabel, string? Concept, AdminPrintImageDto? Image, int PdfPages, IReadOnlyList<AdminPrintMaterialDto> Material,
    JuryPublicVoteDto Vote, int Index, int Total, int? PreviousId, int? NextId, int? Score, string? Comment, uint? Version, bool Locked);

public sealed record JurorScoreRequest
{
    /// <summary>A whole number from 1 to 10. A fraction does not even bind.</summary>
    public int Score { get; init; }
    [StringLength(1000, ErrorMessage = "Qeyd 1000 simvoldan uzun olmamalıdır.")]
    public string? Comment { get; init; }
    /// <summary>The version the juror last saw; a save over a newer one is refused, never silently overwritten.</summary>
    public uint? Version { get; init; }
}

public sealed record JurorScoreDto(int SubmissionId, int Score, string? Comment, uint Version, int Scored, int Total);

// ---- The admin's side ------------------------------------------------------------------------------------------

/// <param name="State">None, Active, Expired, Revoked or Locked (too many wrong codes).</param>
public sealed record JurorInvitationStateDto(
    string State, DateTimeOffset? IssuedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastSignInAt,
    DateTimeOffset? IdentityConfirmedAt, int FailedAttempts);

/// <param name="Status">NotStarted, InProgress or Submitted.</param>
public sealed record AdminJurorProgressDto(
    int JurorId, string Label, int Scored, int Missing, string Status, DateTimeOffset? SubmittedAt, bool SubmittedByAdmin,
    JurorInvitationStateDto Invitation);

/// <param name="EligibleNow">Works the ballot's own rule admits right now — compared with the frozen roster.</param>
/// <param name="RosterDiffers">The ballot changed after the roster was frozen; the roster did not follow (by design).</param>
public sealed record AdminJuryEvaluationDto(
    bool Exists, int CompetitionId, string CompetitionTitle, string? Format, string? Status, DateTimeOffset? CreatedAt,
    DateTimeOffset? RosterFrozenAt, int EligibleNow, int RosterSize, bool RosterDiffers, bool RosterLocked,
    int JurorCount, int ExpectedScores, int CompletedScores, int MissingScores,
    IReadOnlyList<AdminJurorProgressDto> Jurors, bool HasCombinedFinal);

/// <summary>Shown once, when issued: the link (secret in its #fragment) and the separate access code.</summary>
public sealed record IssuedInvitationDto(int JurorId, string Label, string Link, string Code, DateTimeOffset ExpiresAt);

public sealed record IssueInvitationRequest
{
    [Range(1, 60, ErrorMessage = "Müddət 1–60 gün olmalıdır.")]
    public int Days { get; init; } = 14;
}

public sealed record JurorLabelRequest
{
    [Required(ErrorMessage = "Münsifin adını yazın.")]
    [StringLength(60, MinimumLength = 1, ErrorMessage = "Ad 1–60 simvol olmalıdır.")]
    public string Label { get; init; } = "";
}

public sealed record ReasonRequest
{
    [Required(ErrorMessage = "Səbəbi yazın.")]
    [StringLength(1000, MinimumLength = 10, ErrorMessage = "Səbəb 10–1000 simvol olmalıdır.")]
    public string Reason { get; init; } = "";
}

public sealed record JuryMatrixJurorDto(int Id, string Label, bool Submitted);

/// <param name="Scores">One per juror, in the jurors' order; null where not scored yet.</param>
/// <param name="Average">Only once every juror has submitted.</param>
public sealed record JuryMatrixRowDto(
    int SubmissionId, string Reference, string? Title, IReadOnlyList<int?> Scores, IReadOnlyList<string?> Sources,
    IReadOnlyList<bool> HasComment, decimal? Average);

/// <param name="Rows">In catalog order, as the printed catalog the paper scores are read from.</param>
public sealed record JuryMatrixDto(IReadOnlyList<JuryMatrixJurorDto> Jurors, IReadOnlyList<JuryMatrixRowDto> Rows, bool Complete);

public sealed record PaperScoreLine
{
    /// <summary>The work's reference as printed on the sheet (e.g. SUB-2026-00090).</summary>
    public string? Reference { get; init; }
    public int? SubmissionId { get; init; }
    /// <summary>As written on paper; anything but a whole 1–10 is reported, never saved.</summary>
    public string? Score { get; init; }
}

public sealed record PaperResolution
{
    public int SubmissionId { get; init; }
    /// <summary>"paper" (the sheet wins) or "existing" (the score already saved stays).</summary>
    public string Keep { get; init; } = "";
    /// <summary>The saved score the admin saw in the preview; if it has changed since, nothing is saved.</summary>
    public int? Seen { get; init; }
}

public sealed record PaperScoresRequest
{
    public int JurorId { get; init; }
    public IReadOnlyList<PaperScoreLine> Lines { get; init; } = [];
    /// <summary>For each conflict with a score already saved: which one stands.</summary>
    public IReadOnlyList<PaperResolution> Resolutions { get; init; } = [];
    /// <summary>Required when any conflict is resolved.</summary>
    public string? Reason { get; init; }
}

/// <param name="Status">New (only on paper), Same (paper equals the saved score), Conflict (they differ), Existing (only
/// saved, nothing on paper — it stays) or Missing (neither).</param>
public sealed record PaperPreviewRowDto(
    int SubmissionId, string Reference, int? Paper, int? Existing, string? ExistingSource, string Status);

public sealed record PaperPreviewDto(
    int JurorId, string Label, IReadOnlyList<PaperPreviewRowDto> Rows, int New, int Same, int Conflicts, int Missing,
    IReadOnlyList<string> InvalidLines, bool JurorLocked);

public sealed record PaperCommitDto(int Saved, int Unchanged, int ConflictsResolved, int Scored, int Total);

/// <summary>One work in the internal ranking (admins only — no public page or endpoint carries it).</summary>
/// <param name="Position">Shared by works with the same exact average (competition ranking: 1, 2, 2, 4).</param>
/// <param name="Average">The exact average rounded to 4 places for display; <paramref name="ScoreSum"/> /
/// <paramref name="ScoreCount"/> is the exact value the ranking compared.</param>
/// <param name="TieSize">How many works share this position; <paramref name="Tied"/> when more than one.</param>
public sealed record JuryRankingRowDto(
    int Position, int SubmissionId, string Reference, string? Title, decimal Average, long ScoreSum, int ScoreCount,
    bool Tied, int TieSize);

/// <param name="Complete">Every juror has submitted and every work has every juror's score; only then are there rows.</param>
/// <param name="TieGroups">How many positions are shared by two or more works.</param>
public sealed record JuryRankingDto(
    string CompetitionTitle, bool Complete, int RosterSize, int JurorCount, int SubmittedJurors, int ExpectedScores,
    int CompletedScores, IReadOnlyList<JuryRankingRowDto> Rows, int TieGroups, DateTimeOffset GeneratedAt);

/// <summary>
/// The works of the frozen roster for the printed jury catalog — the evaluation form itself — in catalog order, with the
/// jurors actually configured (one score field each).
/// </summary>
public sealed record JuryPrintDto(
    string CompetitionTitle, IReadOnlyList<JuryPrintWorkDto> Works, IReadOnlyList<JuryMatrixJurorDto> Jurors);

/// <param name="Material">The work's whole public material, for the catalog's optional appendix.</param>
public sealed record JuryPrintWorkDto(
    int SubmissionId, string Reference, string Title, AdminPrintImageDto? Image, int PdfPages,
    IReadOnlyList<AdminPrintMaterialDto> Material, JuryPublicVoteDto Vote);
