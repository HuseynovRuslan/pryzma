using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

/// <summary>
/// A competition's result as a whole: whether it is public, the announcement and the results cover. The winners
/// themselves are chosen per category (<see cref="CompetitionCategoryWinner"/>): every settlement of "Bakı kəndləri"
/// can have its own winner. It is a separate concept from a submission's moderation status — moderation says what
/// an admin thinks of one work, the result says which work won.
/// <para>
/// A competition without categories has a single winner, kept here as the overall winner — its first place — and
/// may award a second and a third place beside it. For competitions with categories the overall winner is reserved
/// for a future "best of all" award: it is never set and never shown, and neither are the places.
/// </para>
/// <para>
/// Invariants, enforced by the database as well as the code: one result per competition (unique CompetitionId);
/// the overall winner and the places belong to that same competition (composite foreign keys on Submission
/// (Id, CompetitionId)); no work holds two places (a check constraint).
/// </para>
/// </summary>
public class CompetitionResult
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }

    /// <summary>The single winner of a competition without categories — its first place; null otherwise.</summary>
    public int? OverallWinnerSubmissionId { get; set; }
    public Guid? OverallWinnerSelectedById { get; set; }
    public DateTimeOffset? OverallWinnerSelectedAt { get; set; }

    /// <summary>
    /// Second and third place of a competition without categories. An admin awards them like the first — the vote
    /// informs the choice, it never makes it — and they are public, with their authors' names, once the result is.
    /// They are filled from the top — no second place without a first, no third without a second — and one work
    /// holds one place at most (that one held by the database as well).
    /// </summary>
    public int? SecondPlaceSubmissionId { get; set; }
    public Guid? SecondPlaceSelectedById { get; set; }
    public DateTimeOffset? SecondPlaceSelectedAt { get; set; }
    public int? ThirdPlaceSubmissionId { get; set; }
    public Guid? ThirdPlaceSelectedById { get; set; }
    public DateTimeOffset? ThirdPlaceSelectedAt { get; set; }

    /// <summary>Null while the result is being prepared; nothing about it is public until this is set.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedById { get; set; }
    /// <summary>Optional text shown on the public results page.</summary>
    public string? Announcement { get; set; }
    /// <summary>
    /// Optional results banner chosen by an admin (URL of a re-encoded, metadata-free image in the public covers
    /// area). Never taken from a submission automatically.
    /// </summary>
    public string? CoverImage { get; set; }
    /// <summary>The cover was put there by the demo seeder (removed with the demo data unless an admin replaced it).</summary>
    public bool CoverIsDemo { get; set; }
    /// <summary>The row itself was created by the demo seeder (removed with the demo data if nothing real uses it).</summary>
    public bool IsDemo { get; set; }
    /// <summary>PostgreSQL xmin row version: two admins changing the result at once cannot both succeed.</summary>
    public uint Version { get; set; }

    public Competition Competition { get; set; } = null!;
    public Submission? OverallWinner { get; set; }
    public Submission? SecondPlace { get; set; }
    public Submission? ThirdPlace { get; set; }

    public bool IsPublished => PublishedAt is not null;

    /// <summary>How many places a competition without categories awards.</summary>
    public const int PlaceCount = 3;

    /// <summary>The work holding a place (1–3); the first place is the overall winner.</summary>
    public int? HolderOf(int place) => place switch
    {
        1 => OverallWinnerSubmissionId,
        2 => SecondPlaceSubmissionId,
        3 => ThirdPlaceSubmissionId,
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    /// <summary>The place a work holds, if any.</summary>
    public int? PlaceOf(int submissionId)
    {
        for (var place = 1; place <= PlaceCount; place++)
            if (HolderOf(place) == submissionId) return place;
        return null;
    }

    public (Guid? By, DateTimeOffset? At) ChosenOf(int place) => place switch
    {
        1 => (OverallWinnerSelectedById, OverallWinnerSelectedAt),
        2 => (SecondPlaceSelectedById, SecondPlaceSelectedAt),
        3 => (ThirdPlaceSelectedById, ThirdPlaceSelectedAt),
        _ => throw new ArgumentOutOfRangeException(nameof(place)),
    };

    /// <summary>Gives a place to a work (or empties it), recording who chose it and when.</summary>
    public void SetPlace(int place, int? submissionId, Guid? by, DateTimeOffset? at)
    {
        if (submissionId is null) (by, at) = (null, null);
        switch (place)
        {
            case 1: (OverallWinnerSubmissionId, OverallWinnerSelectedById, OverallWinnerSelectedAt) = (submissionId, by, at); break;
            case 2: (SecondPlaceSubmissionId, SecondPlaceSelectedById, SecondPlaceSelectedAt) = (submissionId, by, at); break;
            case 3: (ThirdPlaceSubmissionId, ThirdPlaceSelectedById, ThirdPlaceSelectedAt) = (submissionId, by, at); break;
            default: throw new ArgumentOutOfRangeException(nameof(place));
        }
    }

    /// <summary>
    /// Final submissions that can win and that appear in the public gallery once results are published:
    /// submitted and not rejected. Drafts are not final; rejected and withdrawn work is out of the competition.
    /// </summary>
    public static readonly SubmissionStatus[] EligibleStatuses =
        [SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.Approved];

    public static bool IsEligible(SubmissionStatus status) => EligibleStatuses.Contains(status);
}

/// <summary>
/// The winner of one category of a competition (a settlement of "Bakı kəndləri"). Not a moderation status.
/// <para>
/// Invariants, enforced by the database as well as the code: at most one winner per category (unique
/// (CompetitionCategoryId, CompetitionId)); a submission wins at most once; the category belongs to the competition
/// and the winning submission belongs to both — to the competition (composite key (Id, CompetitionId)) and to that
/// very category (composite key (Id, CompetitionCategoryId)), so a Balaxanı entry can never be stored as Ramana's
/// winner, not even by hand.
/// </para>
/// </summary>
public class CompetitionCategoryWinner
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public int CompetitionCategoryId { get; set; }
    public int SubmissionId { get; set; }
    public Guid? SelectedById { get; set; }
    /// <summary>When the current winner was chosen (a replacement moves it).</summary>
    public DateTimeOffset SelectedAt { get; set; }
    /// <summary>PostgreSQL xmin row version: a stale replacement cannot overwrite a newer one.</summary>
    public uint Version { get; set; }

    public Competition Competition { get; set; } = null!;
    public CompetitionCategory Category { get; set; } = null!;
    public Submission Submission { get; set; } = null!;
}
