namespace Meydan.Domain.Entities;

/// <summary>Where a competition's final stands. It only ever moves forward, except that an admin may unlock the jury.</summary>
public enum FinalStage
{
    /// <summary>The ten finalists are frozen; the jury's scores are being entered.</summary>
    Frozen,
    /// <summary>Every score is in and locked; the calculation and the show are open, the official result is not yet.</summary>
    JuryLocked,
    /// <summary>The official places were written and published from this final. Nothing in it changes again.</summary>
    Published,
}

/// <summary>
/// The jury final of a competition (owner's decision D13, 7 October 2026): the ten works with the most valid public
/// votes, frozen by an admin after the integrity audit, scored by the jury, and combined — public points plus jury
/// points — into the official places, which an admin then publishes explicitly.
/// <para>
/// Three separate things stay separate: the public vote (Votes, never touched here), the jury's scores (here), and
/// the official result (<see cref="CompetitionResult"/>, written only by the explicit publish). One final per
/// competition, held by the database.
/// </para>
/// </summary>
public class CompetitionFinal
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public FinalStage Stage { get; set; }

    /// <summary>When the finalists were frozen, and by whom; with the valid vote state they were read from.</summary>
    public DateTimeOffset FrozenAt { get; set; }
    public Guid? FrozenById { get; set; }
    public int FrozenValidVotes { get; set; }
    public int FrozenInvalidatedVotes { get; set; }
    /// <summary>
    /// md5 of the competition's votes as (id | work | valid) in id order — the same fingerprint the operator's audit
    /// queries compute — so anyone can check later that the vote state the final was frozen from has not moved.
    /// </summary>
    public string FrozenVotesFingerprint { get; set; } = "";

    /// <summary>Prize money for first, second and third place, in manats; set when the finalists are frozen.</summary>
    public int FirstPrizeAzn { get; set; }
    public int SecondPrizeAzn { get; set; }
    public int ThirdPrizeAzn { get; set; }

    public DateTimeOffset? JuryLockedAt { get; set; }
    public Guid? JuryLockedById { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedById { get; set; }

    /// <summary>PostgreSQL xmin row version: two admins moving the final at once cannot both succeed.</summary>
    public uint Version { get; set; }

    public Competition Competition { get; set; } = null!;
    public List<CompetitionFinalist> Finalists { get; set; } = [];
    public List<FinalJuror> Jurors { get; set; } = [];
    public List<FinalTieBreak> TieBreaks { get; set; } = [];

    /// <summary>How many works go to the final; the points run from this down to 1.</summary>
    public const int FinalistCount = 10;
    /// <summary>The jury scores three criteria, each 0–10 in steps of 0.5: at most 30 per juror per work.</summary>
    public const int CriteriaCount = 3;
    public const decimal CriterionMax = 10m;

    public int PrizeOf(int place) => place switch
    {
        1 => FirstPrizeAzn,
        2 => SecondPrizeAzn,
        3 => ThirdPrizeAzn,
        _ => 0,
    };
}

/// <summary>
/// One finalist as frozen: its valid votes at that moment, its public rank among the ten and the points that rank
/// gives. None of it follows later changes to the votes. The jury columns are written once, when the official
/// result is published from the final — the result as announced, never recalculated afterwards.
/// </summary>
public class CompetitionFinalist
{
    public int Id { get; set; }
    public int FinalId { get; set; }
    /// <summary>The final's competition, so the work is bound to it by a composite key (Submission (Id, CompetitionId)).</summary>
    public int CompetitionId { get; set; }
    public int SubmissionId { get; set; }
    public int ValidVotes { get; set; }
    public int PublicRank { get; set; }
    public int PublicPoints { get; set; }
    public DateTimeOffset SnapshotAt { get; set; }

    public decimal? JuryTotal { get; set; }
    public int? JuryRank { get; set; }
    public int? JuryPoints { get; set; }
    public int? FinalScore { get; set; }
    public int? FinalPlace { get; set; }

    public CompetitionFinal Final { get; set; } = null!;
    public Submission Submission { get; set; } = null!;
    public List<JuryScore> Scores { get; set; } = [];
}

/// <summary>
/// A member of the jury, by label only ("Münsif 1"). The jury scores on paper and an admin enters the sheets, so a
/// juror is not an account; the label never leaves the admin screens.
/// </summary>
public class FinalJuror
{
    public int Id { get; set; }
    public int FinalId { get; set; }
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public CompetitionFinal Final { get; set; } = null!;
}

/// <summary>
/// One juror's three marks for one finalist. A mark may be missing while the sheets are being typed in; the jury can
/// only be locked once none is. Each mark is 0–10 in steps of 0.5 — held by a check constraint as well.
/// </summary>
public class JuryScore
{
    public int Id { get; set; }
    public int FinalistId { get; set; }
    public int JurorId { get; set; }
    /// <summary>İdeya.</summary>
    public decimal? Idea { get; set; }
    /// <summary>Vizual / icra.</summary>
    public decimal? Execution { get; set; }
    /// <summary>Mövzuya uyğunluq.</summary>
    public decimal? Relevance { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }

    public CompetitionFinalist Finalist { get; set; } = null!;
    public FinalJuror Juror { get; set; } = null!;

    public bool IsComplete => Idea is not null && Execution is not null && Relevance is not null;
    public decimal Total => (Idea ?? 0) + (Execution ?? 0) + (Relevance ?? 0);

    /// <summary>A valid mark: 0 to 10, in steps of 0.5.</summary>
    public static bool IsValidMark(decimal mark) =>
        mark >= 0 && mark <= CompetitionFinal.CriterionMax && decimal.Truncate(mark * 2) == mark * 2;
}

/// <summary>
/// The jury's decision on works still level after every rule (final score, raw jury score, valid votes) where the tie
/// touches a prize — first, second, third place or the line between third and fourth. The jurors decide; an admin
/// only records the order they gave, with their reason. It applies only to exactly that set of works.
/// </summary>
public class FinalTieBreak
{
    public int Id { get; set; }
    public int FinalId { get; set; }
    /// <summary>The tied works, best first, as the jury ordered them.</summary>
    public int[] SubmissionIds { get; set; } = [];
    public string Reason { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
    public Guid? DecidedById { get; set; }

    public CompetitionFinal Final { get; set; } = null!;
}
