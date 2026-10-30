using System.ComponentModel.DataAnnotations;
using Meydan.Api.Features.Admin;
using Meydan.Domain.Entities;

namespace Meydan.Api.Features.Final;

/// <summary>A work the final would take, as the admin sees it before freezing: its valid votes and rank.</summary>
public sealed record FinalCandidateDto(int SubmissionId, string Reference, string? Title, int ValidVotes, int Rank);

/// <param name="Next">The first work outside the ten, so the admin sees the margin at the line.</param>
/// <param name="BoundaryTie">The tenth and the eleventh are level: the ten are not determined and cannot be frozen.</param>
/// <param name="Problems">Why freezing is not possible now; empty when it is.</param>
public sealed record FinalFreezePreviewDto(
    bool VotingClosed, bool ResultPublished, int ValidVotes, int InvalidatedVotes, string VotesFingerprint,
    IReadOnlyList<FinalCandidateDto> Top, FinalCandidateDto? Next, bool BoundaryTie, IReadOnlyList<string> Problems);

public sealed record FinalJurorDto(int Id, string Label, bool HasMarks);

public sealed record JuryMarkDto(int JurorId, decimal? Idea, decimal? Execution, decimal? Relevance, decimal Subtotal, bool Complete);

/// <summary>
/// A finalist on the jury's screens: what the work is and the marks given — never its votes or its public rank, so
/// nothing of the public vote reaches the people entering the jury's judgement.
/// </summary>
/// <param name="Author">The author's name — the admin's screens are internal (owner, 7 October 2026); never a vote.</param>
public sealed record JuryFinalistDto(
    int SubmissionId, string Reference, string? Title, AdminPrintImageDto? Image,
    IReadOnlyList<JuryMarkDto> Marks, decimal JuryTotal, bool Complete, string? Author = null);

public sealed record FinalRowDto(
    int SubmissionId, string Reference, string? Title, int ValidVotes, int PublicRank, int PublicPoints,
    decimal JuryTotal, int JuryRank, int JuryPoints, int FinalScore, int Rank, int? Place, FinalTie Tie, int? PrizeAzn);

public sealed record FinalTieMemberDto(int SubmissionId, string Reference, string? Title);

/// <summary>Works level on every rule where it touches a prize; the jury must order them.</summary>
public sealed record FinalTieGroupDto(int Rank, IReadOnlyList<FinalTieMemberDto> Works);

public sealed record FinalTieBreakDto(IReadOnlyList<string> References, string Reason, DateTimeOffset DecidedAt, string? DecidedBy);

/// <summary>The combined result, open to the admin once the jury is locked.</summary>
public sealed record FinalCalculationDto(
    int JurorCount, decimal JuryMax, IReadOnlyList<FinalRowDto> Rows, IReadOnlyList<FinalTieGroupDto> UnresolvedTies,
    IReadOnlyList<FinalTieBreakDto> TieBreaks, IReadOnlyList<string> PublishProblems);

/// <param name="Stage">Null while nothing is frozen; <paramref name="Preview"/> then says what would be.</param>
/// <param name="VotesChangedSinceFreeze">The competition's votes no longer match the fingerprint they were frozen
/// with (a vote set aside or restored since). The final does not follow; the admin is told.</param>
/// <param name="Finalists">Reference order, blind: see <see cref="JuryFinalistDto"/>.</param>
/// <param name="Calculation">Only from the jury's lock on — never while marks are being entered.</param>
public sealed record AdminFinalDto(
    int CompetitionId, string CompetitionTitle, FinalStage? Stage, FinalFreezePreviewDto? Preview,
    DateTimeOffset? FrozenAt, string? FrozenBy, int? FrozenValidVotes, int? FrozenInvalidatedVotes,
    string? FrozenVotesFingerprint, bool VotesChangedSinceFreeze, IReadOnlyList<int> Prizes,
    IReadOnlyList<FinalJurorDto> Jurors, IReadOnlyList<JuryFinalistDto> Finalists, int MissingMarks, bool CanRefreeze,
    DateTimeOffset? JuryLockedAt, string? JuryLockedBy, FinalCalculationDto? Calculation,
    DateTimeOffset? PublishedAt, string? PublishedBy);

/// <summary>A finalist on the show's screen. No author, no voter, no juror — the work and its numbers.</summary>
public sealed record FinalShowWorkDto(
    int SubmissionId, string Reference, string? Title, AdminPrintImageDto? Image,
    int ValidVotes, int PublicRank, int PublicPoints, decimal JuryTotal, int JuryRank, int JuryPoints, int FinalScore,
    int Rank, int? Place, FinalTie Tie, int? PrizeAzn);

/// <param name="Works">Reference order; the show builds each reveal's order from the numbers.</param>
/// <param name="PrizesDecided">False while a tie at a prize waits for the jury: the top three stay closed.</param>
public sealed record FinalShowDto(
    string CompetitionTitle, int JurorCount, decimal JuryMax, IReadOnlyList<FinalShowWorkDto> Works,
    bool PrizesDecided, IReadOnlyList<int> Prizes);

public enum LeaderboardPrintMode
{
    /// <summary>The ten finalists from the frozen snapshot.</summary>
    Top10,
    /// <summary>Every work on the ballot, by valid votes, from the closed vote as it stands.</summary>
    Full,
}

/// <param name="Rank">Competition ranking by valid votes (1, 2, 2, 4) — the public rank, not a prize place.</param>
/// <param name="PublicPoints">The final's points for the rank (Top 10 only).</param>
/// <param name="Item">The work as the admin's printed cards show it (PrintItems): picture, reference, title,
/// nomination and — when asked for — the author.</param>
public sealed record LeaderboardPrintRowDto(int Rank, int ValidVotes, int? PublicPoints, AdminPrintItemDto Item);

/// <summary>
/// The public vote on paper, for the admin: no voter, no author — references, titles and valid votes only.
/// </summary>
/// <param name="FrozenAt">When the snapshot was frozen (Top 10); null for the full ranking, read as it stands.</param>
/// <param name="VotesFingerprint">md5 of the vote state the numbers come from (see CompetitionFinal).</param>
public sealed record LeaderboardPrintDto(
    LeaderboardPrintMode Mode, string CompetitionTitle, DateTimeOffset? FrozenAt, int ValidVotes, int InvalidatedVotes,
    int TotalVotes, string VotesFingerprint, IReadOnlyList<LeaderboardPrintRowDto> Rows, DateTimeOffset GeneratedAt);

public sealed record FreezeFinalRequest
{
    /// <summary>The admin confirms the final integrity audit is complete and approved.</summary>
    public bool AuditCompleted { get; init; }

    /// <summary>Prize money for 1st, 2nd and 3rd place, in manats.</summary>
    [Required(ErrorMessage = "Mükafatları göstərin.")]
    public int[] Prizes { get; init; } = [];

    /// <summary>
    /// Required when a frozen ten exists: why it is superseded. The old snapshot is written to the audit in full,
    /// with this reason, before the new one replaces it.
    /// </summary>
    public string? SupersedeReason { get; init; }
}

public sealed record JuryMarkInput
{
    public int SubmissionId { get; init; }
    public decimal? Idea { get; init; }
    public decimal? Execution { get; init; }
    public decimal? Relevance { get; init; }
}

/// <summary>One juror's sheet, as typed in: the marks for any of the finalists (null: not given yet).</summary>
public sealed record JuryMarksRequest
{
    public int JurorId { get; init; }

    [Required(ErrorMessage = "Balları göstərin.")]
    public JuryMarkInput[] Marks { get; init; } = [];
}

/// <summary>A juror's name as the admin's screens and printed sheets show it, e.g. the juror's own name.</summary>
public sealed record RenameJurorRequest
{
    [Required(ErrorMessage = "Münsifin adını yazın.")]
    [StringLength(60, MinimumLength = 1, ErrorMessage = "Ad 1–60 simvol olmalıdır.")]
    public string Label { get; init; } = "";
}

public sealed record UnlockJuryRequest
{
    [Required(ErrorMessage = "Kilidin niyə açıldığını yazın.")]
    [StringLength(1000, MinimumLength = 5, ErrorMessage = "Səbəb 5–1000 simvol olmalıdır.")]
    public string Reason { get; init; } = "";
}

public sealed record TieBreakRequest
{
    /// <summary>The level works, best first, as the jury ordered them.</summary>
    [Required(ErrorMessage = "İşlərin sırasını göstərin.")]
    public int[] SubmissionIds { get; init; } = [];

    [Required(ErrorMessage = "Münsiflərin qərarının səbəbini yazın.")]
    [StringLength(1000, MinimumLength = 5, ErrorMessage = "Səbəb 5–1000 simvol olmalıdır.")]
    public string Reason { get; init; } = "";
}

public sealed record PublishFinalRequest
{
    /// <summary>The admin saw that this writes the official places and makes them public at once.</summary>
    public bool Confirm { get; init; }
}
