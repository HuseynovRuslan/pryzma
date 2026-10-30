using Meydan.Domain.Common;
using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

/// <summary>A themed umbrella (a region, a programme...) that groups competitions.</summary>
public class Project : IAuditable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImage { get; set; }
    /// <summary>Hex colour (#RRGGBB) that gives the project its visual identity.</summary>
    public string? AccentColor { get; set; }
    public ProjectStatus Status { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Created by the demo seeder (`seed-demo`); removed by `remove-demo`. Never shown publicly as such.</summary>
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Competition> Competitions { get; set; } = [];

    public static bool IsPubliclyVisible(ProjectStatus status) => status == ProjectStatus.Published;
}
