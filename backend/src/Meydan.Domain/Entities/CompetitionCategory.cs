namespace Meydan.Domain.Entities;

/// <summary>Optional subdivision of a competition: a village, a medium, a nomination...</summary>
public class CompetitionCategory
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? Image { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public Competition Competition { get; set; } = null!;
    public ICollection<Submission> Submissions { get; set; } = [];
    /// <summary>This category's winner, if one has been chosen.</summary>
    public CompetitionCategoryWinner? Winner { get; set; }

    /// <summary>Votes cast in this nomination; one per person, enforced by a unique index.</summary>
    public ICollection<Vote> Votes { get; set; } = [];
}
