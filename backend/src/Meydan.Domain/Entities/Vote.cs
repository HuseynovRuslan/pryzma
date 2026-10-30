namespace Meydan.Domain.Entities;

/// <summary>
/// One person's vote for one work, in one nomination of one competition.
///
/// <para>
/// The rule — a person votes once per nomination — is the database's, not the code's: a unique index over
/// (UserId, CompetitionCategoryId) is what makes a double click, a second tab, a replayed request and two
/// parallel calls settle to one row. The API turns the resulting violation into a 409; it never checks first
/// and inserts afterwards, because between those two steps another request fits.
/// </para>
/// <para>
/// Naming the nomination names the competition, since a category belongs to exactly one — so there is no
/// CompetitionId here to fall out of step. The nomination cannot fall out of step with the work either:
/// (SubmissionId, CompetitionCategoryId) points at the submission's own (Id, CompetitionCategoryId), the same
/// device that stops one settlement's entry being stored as another's winner. A vote for a work in a
/// nomination it was never entered in is unstorable.
/// </para>
/// <para>A vote is final. Nothing updates or deletes one in normal operation — see docs/voting/OWNER-DECISIONS.md, D5.</para>
/// <para>
/// The one exception is an admin's finding that a vote is fraudulent (D12): the row stays, with when, by whom and
/// why it was set aside, and from then on it counts nowhere — every query reads only valid votes unless it asks
/// otherwise. It still spends the voter's one vote, so setting it aside never lets the same person vote again. An
/// admin can restore it the same way, with a reason; both steps are in the audit log.
/// </para>
/// </summary>
public class Vote
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public int SubmissionId { get; set; }

    /// <summary>The nomination, which is a category of the competition the work was entered in.</summary>
    public int CompetitionCategoryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When an admin set this vote aside as fraudulent; null for a valid vote.</summary>
    public DateTimeOffset? InvalidatedAt { get; set; }

    /// <summary>The admin who set it aside.</summary>
    public Guid? InvalidatedById { get; set; }

    /// <summary>Why, in the admin's words — required whenever <see cref="InvalidatedAt"/> is set.</summary>
    public string? InvalidationReason { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public ApplicationUser? InvalidatedBy { get; set; }
    public Submission Submission { get; set; } = null!;
    public CompetitionCategory Category { get; set; } = null!;
}
