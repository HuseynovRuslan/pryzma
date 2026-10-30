using Meydan.Domain.Common;
using Meydan.Domain.Enums;

namespace Meydan.Domain.Entities;

public class Competition : IAuditable
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string? Description { get; set; }
    public string? Rules { get; set; }
    /// <summary>Who may take part and what must be delivered. Shown only when filled.</summary>
    public string? Requirements { get; set; }
    /// <summary>
    /// The competition's nominations, one per block: the block's first line is its name, the rest explains it, and
    /// blocks are separated by an empty line. Text, so an admin owns the wording; shown only when filled.
    /// </summary>
    public string? Nominations { get; set; }
    public string? CoverImage { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    /// <summary>When the results are announced; null while the date is not decided (the row is then not shown).</summary>
    public DateTimeOffset? ResultsAnnouncedOn { get; set; }
    /// <summary>An exhibition of the works, where there is one. Null: no row.</summary>
    public DateTimeOffset? ExhibitionOn { get; set; }
    /// <summary>
    /// What the entries are judged on, in the admin's own words — one criterion per line. Text, not a scoring
    /// formula: the platform never computes a score, it only says what is being looked at. Shown when filled.
    /// </summary>
    public string? JudgingCriteria { get; set; }

    /// <summary>
    /// What the organiser may do with a submitted work and what stays with its author. Text, because the answer
    /// differs per competition and is a matter for the organiser, not for the code. Shown when filled.
    /// </summary>
    public string? UsageTerms { get; set; }

    /// <summary>Optional line for questions (e-mail, phone, a room number); entries always come through the site.</summary>
    public string? ContactNote { get; set; }
    public string? Prize { get; set; }
    public CompetitionStatus Status { get; set; }
    public bool HasCategories { get; set; }
    /// <summary>Human label for the category list, e.g. "Kəndlər". Null means a generic label.</summary>
    public string? CategoryLabel { get; set; }
    public int? MaxSubmissionsPerUser { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Created by the demo seeder (`seed-demo`); removed by `remove-demo`. Never shown publicly as such.</summary>
    public bool IsDemo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<CompetitionCategory> Categories { get; set; } = [];
    public ICollection<CompetitionField> Fields { get; set; } = [];
    public ICollection<Submission> Submissions { get; set; } = [];
    public CompetitionResult? Result { get; set; }
    /// <summary>One winner per category, chosen by an admin; public only once the result is published.</summary>
    public ICollection<CompetitionCategoryWinner> CategoryWinners { get; set; } = [];

    public static bool IsPubliclyVisible(CompetitionStatus status) =>
        status is CompetitionStatus.Published or CompetitionStatus.Closed;

    /// <summary>
    /// When the public vote opens and closes. Deliberately not derived from the entry dates: entries close on
    /// EndDate and the results date is often not decided yet, so a derived window would have no end — and voting
    /// has to be able to open and close without anybody touching the deadline participants were given.
    /// Both null: this competition has no public vote.
    /// </summary>
    public DateTimeOffset? VotingStartsAt { get; set; }
    public DateTimeOffset? VotingEndsAt { get; set; }

    /// <summary>Where the public vote stands. The server's answer; a browser never works this out from dates.</summary>
    public VotingState VotingStateAt(DateTimeOffset now)
    {
        if (VotingStartsAt is not { } from || VotingEndsAt is not { } to) return VotingState.NotConfigured;
        if (now < from) return VotingState.Upcoming;
        return now < to ? VotingState.Open : VotingState.Closed;
    }

    /// <summary>Published status and the optional date window must both allow new submissions.</summary>
    public bool IsAcceptingSubmissions(DateTimeOffset now) =>
        Status == CompetitionStatus.Published
        && (StartDate is null || StartDate <= now)
        && (EndDate is null || EndDate >= now);
}
