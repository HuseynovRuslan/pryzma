namespace Meydan.Domain.Entities;

/// <summary>The two independent choices a result can announce (owner, 16 October 2026).</summary>
public enum AwardCategory
{
    /// <summary>The jury's choice: points from the jury's own protocol (for "Əmircanın simvolu", a paper vote).</summary>
    Jury,
    /// <summary>The public's choice: the work's valid votes, as the audited ballot counts them.</summary>
    Public,
}

/// <summary>
/// One place of one of the two podiums a competition's result can announce — the jury's choice and the public's
/// choice — with the number it is announced with: the jury's points, or the work's valid votes.
/// <para>
/// The two podiums are independent: they are never added together, no formula combines them and no overall winner
/// is derived from them. The same work may stand on both. Nothing here is public until the competition's
/// <see cref="CompetitionResult"/> is published, by the existing publication.
/// </para>
/// <para>
/// Invariants, enforced by the database as well as the code: three places at most per podium (place 1–3, unique per
/// competition and category); a work stands on a podium once; the work belongs to this very competition (composite
/// key on Submission (Id, CompetitionId)); points are never negative.
/// </para>
/// </summary>
public class CompetitionAward
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public AwardCategory Category { get; set; }

    /// <summary>1, 2 or 3.</summary>
    public int Place { get; set; }

    public int SubmissionId { get; set; }

    /// <summary>The participant's name as the announcement gives it — set by an admin, public once the result is.</summary>
    public string ParticipantName { get; set; } = "";

    /// <summary>The jury's points, or the work's valid votes when the podium was set.</summary>
    public int Points { get; set; }

    /// <summary>Where the number comes from, for the record (never public): "Kağız protokol", "Etibarlı səslər".</summary>
    public string Source { get; set; } = "";

    public Guid? RecordedById { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>PostgreSQL xmin row version.</summary>
    public uint Version { get; set; }

    public Competition Competition { get; set; } = null!;
    public Submission Submission { get; set; } = null!;

    /// <summary>Places on each podium.</summary>
    public const int PlaceCount = 3;

    public const int NameMax = 120;
    public const int SourceMax = 300;
}
