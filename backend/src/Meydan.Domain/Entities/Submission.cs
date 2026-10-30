using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

public class Submission
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public int CompetitionId { get; set; }
    public int? CompetitionCategoryId { get; set; }
    /// <summary>Denormalised copy of the answer to the form's title field, for lists.</summary>
    public string Title { get; set; } = "";
    public SubmissionStatus Status { get; set; }
    /// <summary>Public reference such as SUB-2026-00124. Minted by the server at final submission; null for drafts.</summary>
    public string? ReferenceNumber { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token: renewed on every write so two final submits cannot both win.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    /// <summary>Admin-chosen gallery image (one of this submission's files with a ready public derivative); null = automatic.</summary>
    public int? PreviewFileId { get; set; }
    /// <summary>Created by the demo seeder (`seed-demo`); removed by `remove-demo`. Never shown publicly as such.</summary>
    public bool IsDemo { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public Competition Competition { get; set; } = null!;
    public CompetitionCategory? Category { get; set; }
    public ICollection<SubmissionFile> Files { get; set; } = [];
    public ICollection<SubmissionAnswer> Answers { get; set; } = [];
    public ICollection<SubmissionNote> Notes { get; set; } = [];
    public ICollection<SubmissionStatusChange> StatusChanges { get; set; } = [];
    /// <summary>Set when this work won its category.</summary>
    public CompetitionCategoryWinner? CategoryWin { get; set; }

    /// <summary>Public votes for this work. Only ever added to.</summary>
    public ICollection<Vote> Votes { get; set; } = [];

    /// <summary>
    /// Whether the participant may still change this submission. Today only drafts are editable; an admin
    /// "editing allowed" window would be added here, in one place, rather than in every endpoint.
    /// </summary>
    public bool IsEditableByParticipant => Status == SubmissionStatus.Draft;

    /// <summary>Counts against Competition.MaxSubmissionsPerUser.</summary>
    public static bool CountsTowardsLimit(SubmissionStatus status) =>
        status is not (SubmissionStatus.Draft or SubmissionStatus.Withdrawn);
}
