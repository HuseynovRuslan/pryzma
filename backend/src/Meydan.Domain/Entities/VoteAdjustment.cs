namespace Meydan.Domain.Entities;

/// <summary>
/// Documented recovery of votes lost to a proven technical incident. Never creates a voter or a Vote.
/// Pending requests have no effect; only a second administrator may approve one.
/// Approved corrections are disclosed separately from actual votes in public audit data.
/// </summary>
public sealed class VoteAdjustment
{
    public long Id { get; set; }
    public int CompetitionId { get; set; }
    public int SubmissionId { get; set; }
    public int Delta { get; set; }
    public string Reason { get; set; } = "";
    public string EvidenceReference { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public Guid RequestedById { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public Competition Competition { get; set; } = null!;
    public Submission Submission { get; set; } = null!;
}
