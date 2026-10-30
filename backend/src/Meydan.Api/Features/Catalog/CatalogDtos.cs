using Meydan.Domain.Entities;
using Meydan.Domain.Enums;

namespace Meydan.Api.Features.Catalog;

/// <summary>
/// What a visitor should understand right now, computed from the stored status plus the dates — never
/// stored itself. A Published competition past its end date is shown as closed without anyone touching it.
/// </summary>
public enum CompetitionPhase
{
    Upcoming,
    Open,
    Closed,
}

public sealed record ProjectRefDto(string Slug, string Name, string? AccentColor);

public sealed record ProjectCardDto(
    int Id,
    string Slug,
    string Name,
    string ShortDescription,
    string? CoverImage,
    string? AccentColor,
    ProjectStatus Status,
    int SortOrder,
    int OpenCompetitionCount,
    int CompetitionCount);

public sealed record ProjectDetailDto(
    int Id,
    string Slug,
    string Name,
    string ShortDescription,
    string? Description,
    string? CoverImage,
    string? AccentColor,
    ProjectStatus Status,
    int SortOrder,
    IReadOnlyList<CompetitionSummaryDto> Competitions);

public sealed record CompetitionSummaryDto(
    int Id,
    string Slug,
    string Title,
    string ShortDescription,
    string? CoverImage,
    CompetitionStatus Status,
    CompetitionPhase Phase,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    bool HasPrize,
    bool HasCategories,
    string? CategoryLabel,
    int CategoryCount,
    ProjectRefDto Project,
    bool ResultsPublished,
    /// <summary>Where the public vote stands, so a card can offer it without asking a second endpoint.</summary>
    VotingState Voting);

public sealed record CategoryDto(int Id, string Slug, string Name, string? Description, string? Image);

public sealed record CompetitionDetailDto(
    int Id,
    string Slug,
    string Title,
    string ShortDescription,
    string? Description,
    string? Rules,
    string? Requirements,
    /// <summary>Nominations as the admin wrote them: one block each, its first line the name (see Competition.Nominations).</summary>
    string? Nominations,
    string? CoverImage,
    CompetitionStatus Status,
    CompetitionPhase Phase,
    bool IsAcceptingSubmissions,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    /// <summary>Announcement and exhibition days; null while undecided, and then not shown at all.</summary>
    DateTimeOffset? ResultsAnnouncedOn,
    DateTimeOffset? ExhibitionOn,
    /// <summary>What the entries are judged on, and what happens to an author's work; null when not written yet.</summary>
    string? JudgingCriteria,
    string? UsageTerms,
    /// <summary>Optional line for questions; entries themselves always come through the site.</summary>
    string? ContactNote,
    string? Prize,
    bool HasCategories,
    string? CategoryLabel,
    int? MaxSubmissionsPerUser,
    IReadOnlyList<CategoryDto> Categories,
    ProjectRefDto Project,
    /// <summary>Set only once results are public; the winner itself comes from the results endpoint.</summary>
    DateTimeOffset? ResultsPublishedAt,
    /// <summary>Where the public vote stands. `NotConfigured` — the ordinary case — means there is no vote here.</summary>
    VotingState Voting);

public sealed record HomeDto(
    IReadOnlyList<ProjectCardDto> Projects,
    IReadOnlyList<CompetitionSummaryDto> OpenCompetitions,
    /// <summary>
    /// Competitions whose public vote is open. Separate from the open ones because a vote usually runs after
    /// entries have closed: the same competition would otherwise be missing from the page just when it has
    /// something to ask of the reader.
    /// </summary>
    IReadOnlyList<CompetitionSummaryDto> VotingCompetitions);

public static class CompetitionPhases
{
    /// <summary>
    /// Results count as public only when an admin published them AND the competition is visible and no longer
    /// accepting work. Reopening a competition therefore hides its results and gallery again instead of
    /// showing entries to people who can still submit.
    /// </summary>
    public static bool AreResultsPublic(Competition c, DateTimeOffset? publishedAt, DateTimeOffset now) =>
        publishedAt is not null && Competition.IsPubliclyVisible(c.Status) && !c.IsAcceptingSubmissions(now);

    public static CompetitionPhase Of(Competition c, DateTimeOffset now) => c.Status switch
    {
        CompetitionStatus.Published when c.StartDate > now => CompetitionPhase.Upcoming,
        CompetitionStatus.Published when c.EndDate < now => CompetitionPhase.Closed,
        CompetitionStatus.Published => CompetitionPhase.Open,
        _ => CompetitionPhase.Closed,
    };
}
