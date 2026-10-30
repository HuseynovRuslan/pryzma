using Meydan.Api.Features.Results;
using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Voting;

// The public voting surface. It carries what docs/voting/OWNER-DECISIONS.md D9 allows — the reference number,
// the work's title where that field is public, the nomination, the server-made safe image, the vote count — and
// the answers of fields an admin has marked public, which is the same switch, per field, that decides what the
// gallery shows. Never a name, an e-mail, an original file, an admin note, or a field left private.

/// <summary>A nomination on the voting page: how many works it holds and how many votes have been cast in it.</summary>
public sealed record VotingNominationDto(string Slug, string Name, int WorkCount, int VoteCount);

/// <summary>
/// Why the vote button on a work looks the way it does. One value per piece of copy the design asks for, so the
/// browser never works the reason out from dates, counts or its own idea of who the viewer is.
/// </summary>
public enum VoteEligibility
{
    /// <summary>"Səs ver".</summary>
    Allowed,
    /// <summary>Signed out: the sign-in dialog, then back here.</summary>
    NotSignedIn,
    /// <summary>"Səsvermə başa çatıb" — or has not started.</summary>
    VotingNotOpen,
    /// <summary>"Səsiniz qeydə alındı ✓".</summary>
    AlreadyVotedForThisWork,
    /// <summary>
    /// "Səsinizi artıq vermisiniz": this viewer has voted for another work of the competition — in any category,
    /// since a person votes once per competition. The value keeps its old name so a page loaded before a deploy
    /// still understands the answer.
    /// </summary>
    AlreadyVotedInNomination,
    /// <summary>A participant may not vote for their own work — D6.</summary>
    OwnWork,
    /// <summary>
    /// "E-maili təsdiqləyin": an account opened after e-mail confirmation became required, whose address is not
    /// confirmed yet (<see cref="Auth.EmailConfirmationPolicy"/>).
    /// </summary>
    EmailNotConfirmed,
}

/// <summary>
/// What this viewer may do, worked out once for the whole page. Null for a signed-out visitor, which is itself the
/// answer: every button shows "daxil olun".
/// </summary>
public sealed record VotingViewerDto(
    bool MayVote,
    VoteEligibility Reason,
    /// <summary>Nominations this viewer has already voted in, by slug.</summary>
    IReadOnlyList<string> VotedNominations,
    /// <summary>The works those votes went to, by reference — so a card can say which one was theirs.</summary>
    IReadOnlyList<string> VotedWorks);

/// <summary>A work as the ballot shows it.</summary>
public sealed record VotingWorkDto(
    string Reference,
    string? Title,
    GalleryCategoryDto Nomination,
    PublicImageDto? Preview,
    PreviewState PreviewState,
    int VoteCount,
    VoteEligibility Eligibility,
    /// <summary>
    /// A few lines of the work's own description, for the card. One field only — the public long-text answer,
    /// shortened — never the whole answer sheet: the list carries what a card shows and nothing more, and the
    /// full text stays behind the work's own page. Null when the field is not public or was left empty.
    /// </summary>
    string? PublicConceptDescription);

/// <summary>The voting page's header: the state, the window and the nominations, with totals.</summary>
public sealed record VotingDto(
    ResultCompetitionDto Competition,
    VotingState State,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<VotingNominationDto> Nominations,
    int WorkCount,
    int VoteCount,
    VotingViewerDto? Viewer,
    /// <summary>How many people have voted — a number, never a name: who voted stays private.</summary>
    int VoterCount,
    /// <summary>When the latest vote was cast; null before the first. Says the vote is alive, not who cast it.</summary>
    DateTimeOffset? LastVoteAt,
    /// <summary>
    /// The official result is public — published by an admin, by the same rule the results pages use. A result
    /// row being prepared does not count. Until then a closed vote is in review, not over.
    /// </summary>
    bool ResultsPublished,
    /// <summary>
    /// The server's clock when this answer was made. A page measures its own clock against it, so it asks again at
    /// the server's moment of closing rather than at whatever its device believes the time to be.
    /// </summary>
    DateTimeOffset ServerNow);

/// <summary>One page of works.</summary>
public sealed record VotingWorksDto(
    VotingState State,
    IReadOnlyList<VotingWorkDto> Items,
    int Total,
    int Page,
    int PageSize,
    VotingViewerDto? Viewer);

/// <summary>One work: every public-safe picture of it, and the answers an admin made public.</summary>
public sealed record VotingWorkDetailDto(
    VotingWorkDto Work,
    IReadOnlyList<GalleryAnswerDto> Answers,
    IReadOnlyList<GalleryFileDto> Files,
    VotingState State,
    DateTimeOffset? EndsAt,
    VotingViewerDto? Viewer);

/// <summary>
/// Why votes were set aside, in public words: one value per rule an admin may set votes aside under (D12), never the
/// admin's own text. A rule added to <c>VoteReview.DecideAsync</c> needs its own value here.
/// </summary>
public enum VotingAuditReason
{
    /// <summary>"təkrarlanan telefon məlumatlarına görə" — the phone-cluster rule.</summary>
    DuplicatePhone,
    /// <summary>"koordinasiyalı hesab klasteri qaydasına əsasən" — R2, coordinated identity.</summary>
    CoordinatedAccounts,
    /// <summary>"müvəqqəti e-poçt qaydasına əsasən" — D14, a disposable e-mail on an account opened during the vote.</summary>
    DisposableEmail,
}

/// <summary>Votes set aside under one rule, all decisions together.</summary>
public sealed record VotingAuditRuleDto(VotingAuditReason Reason, int Votes);

/// <summary>One admin decision as the public sees it: when, how many votes, under which rule. Nothing else.</summary>
public sealed record VotingAuditEntryDto(DateTimeOffset At, int Votes, VotingAuditReason Reason);

/// <summary>
/// The public audit of a vote: how many votes count, how many were set aside and the total, and one line per
/// decision. Never a voter, an account, a phone, an address, an admin, a work, or how a rule is worked out.
/// </summary>
/// <param name="Rules">Per rule, the votes set aside under it (rules with none are left out).</param>
public sealed record VotingAuditDto(
    VotingState State, int ValidVotes, int InvalidatedVotes, int TotalVotes,
    IReadOnlyList<VotingAuditEntryDto> Entries, DateTimeOffset UpdatedAt,
    IReadOnlyList<VotingAuditRuleDto>? Rules = null);

/// <summary>What a cast vote answers with: the work, its nomination and the count the vote produced.</summary>
public sealed record VoteReceiptDto(string Reference, string NominationSlug, int VoteCount, DateTimeOffset CastAt);

/// <summary>
/// A work's place in the public vote. <c>Rank</c> is shared by works with the same count (1, 2, 2, 4): a tie is
/// shown as a tie, never broken by something the public cannot see. The order inside a tie is the submission's
/// own id, which only keeps the list still between two reads.
/// </summary>
public sealed record VotingLeaderboardEntryDto(
    int Rank,
    string Reference,
    string? Title,
    int VoteCount,
    PublicImageDto? Preview);

/// <summary>
/// The live ranking of the public vote over every work on the ballot, whatever its category. It ranks votes only:
/// the places an admin awards (CompetitionResult) are decided elsewhere and are never read or written here.
/// </summary>
public sealed record VotingLeaderboardDto(
    VotingState State,
    /// <summary>When the counts were read.</summary>
    DateTimeOffset UpdatedAt,
    /// <summary>The votes on the works listed — the sum of their counts.</summary>
    int TotalVotes,
    IReadOnlyList<VotingLeaderboardEntryDto> Entries,
    /// <summary>The work this viewer voted for, by reference; null when signed out or not voted yet.</summary>
    string? MyVoteReference);
