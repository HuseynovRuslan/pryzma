using Meydan.Domain.Entities;

namespace Meydan.Api.Features.Final;

/// <summary>One finalist as the calculation needs it: the frozen public side and the jury's raw total.</summary>
public sealed record FinalistInput(
    int SubmissionId, string Reference, int ValidVotes, int PublicRank, int PublicPoints, decimal JuryTotal);

/// <summary>How a finalist's place was reached when it was level with others on every rule.</summary>
public enum FinalTie
{
    /// <summary>Not level with anyone.</summary>
    None,
    /// <summary>Level below the prizes (fourth or lower): the works share the place, as in any competition ranking.</summary>
    SharedPlace,
    /// <summary>Level where it touches a prize; the jury ordered the works and an admin recorded it.</summary>
    DecidedByJury,
    /// <summary>Level where it touches a prize and the jury has not decided yet: no place until it does.</summary>
    NeedsJuryDecision,
}

/// <param name="Rank">The competition rank by final score, then raw jury score, then valid votes: level works share it.</param>
/// <param name="Place">The place this gives: distinct once the jury has decided a tie at a prize, shared below the
/// prizes, and null while a tie at a prize waits for the jury.</param>
public sealed record FinalRow(
    int SubmissionId, string Reference, int ValidVotes, int PublicRank, int PublicPoints,
    decimal JuryTotal, int JuryRank, int JuryPoints, int FinalScore, int Rank, int? Place, FinalTie Tie);

/// <param name="Rows">Best first. Inside a group the jury has not decided yet the order means nothing — it is the
/// reference order, for a stable screen — and every row of the group says so.</param>
/// <param name="UnresolvedTies">Each group still waiting for the jury, as its works' ids in reference order.</param>
public sealed record FinalTable(IReadOnlyList<FinalRow> Rows, IReadOnlyList<IReadOnlyList<int>> UnresolvedTies)
{
    public bool PrizesDecided => UnresolvedTies.Count == 0;

    /// <summary>The work holding a place 1–3, when exactly one does.</summary>
    public FinalRow? AtPlace(int place)
    {
        var holders = Rows.Where(r => r.Place == place).ToList();
        return holders.Count == 1 ? holders[0] : null;
    }
}

/// <summary>
/// The final's arithmetic (D13), with nothing else in it — no database, no clock — so every rule is tested on its own.
/// <list type="bullet">
/// <item>Public points and jury points both run 10, 9 … 1 down the ranking of the ten finalists.</item>
/// <item>Level results share a rank and its points, and the next rank is skipped (1, 2, 2, 4): competition ranking.
/// There is no hidden tie-break — never the work's id or reference.</item>
/// <item>Final score = public points + jury points. Level final scores are separated by the raw jury score, then by
/// the valid votes. Works still level share the place — unless that touches first, second or third place or the line
/// between third and fourth, where the jury must decide and an admin records the order with a reason.</item>
/// </list>
/// </summary>
public static class FinalScoring
{
    /// <summary>The points a rank among the finalists gives: 10 for the first, 1 for the tenth.</summary>
    public static int PointsFor(int rank) => Math.Max(0, CompetitionFinal.FinalistCount + 1 - rank);

    /// <summary>
    /// Competition ranking of a list already sorted best first: an item level with the one before shares its rank,
    /// and the next one down takes its position (1, 2, 2, 4).
    /// </summary>
    public static int[] CompetitionRanks<T>(IReadOnlyList<T> sortedBestFirst, Func<T, T, bool> level)
    {
        var ranks = new int[sortedBestFirst.Count];
        for (var i = 0; i < ranks.Length; i++)
            ranks[i] = i > 0 && level(sortedBestFirst[i], sortedBestFirst[i - 1]) ? ranks[i - 1] : i + 1;
        return ranks;
    }

    /// <summary>A finalist's jury total: every juror's three marks added together.</summary>
    public static decimal JuryTotal(IEnumerable<JuryScore> scores) => scores.Sum(s => s.Total);

    /// <param name="decisions">The jury's recorded orders for level groups; one applies only to exactly its own set of works.</param>
    public static FinalTable Compute(IReadOnlyList<FinalistInput> finalists, IReadOnlyList<IReadOnlyList<int>> decisions)
    {
        // The jury's ranking and its points.
        var byJury = finalists.OrderByDescending(f => f.JuryTotal).ThenBy(f => f.Reference, StringComparer.Ordinal).ToList();
        var juryRanks = CompetitionRanks(byJury, (a, b) => a.JuryTotal == b.JuryTotal);
        var jury = byJury.Select((f, i) => (f, rank: juryRanks[i])).ToDictionary(x => x.f.SubmissionId, x => x.rank);

        var scored = finalists
            .Select(f => (f, juryRank: jury[f.SubmissionId], juryPoints: PointsFor(jury[f.SubmissionId]),
                final: f.PublicPoints + PointsFor(jury[f.SubmissionId])))
            .OrderByDescending(x => x.final).ThenByDescending(x => x.f.JuryTotal).ThenByDescending(x => x.f.ValidVotes)
            .ThenBy(x => x.f.Reference, StringComparer.Ordinal) // display order inside a level group only
            .ToList();
        static bool Level((FinalistInput f, int juryRank, int juryPoints, int final) a, (FinalistInput f, int juryRank, int juryPoints, int final) b) =>
            a.final == b.final && a.f.JuryTotal == b.f.JuryTotal && a.f.ValidVotes == b.f.ValidVotes;
        var ranks = CompetitionRanks(scored, Level);

        var rows = new List<FinalRow>(scored.Count);
        var unresolved = new List<IReadOnlyList<int>>();
        for (var i = 0; i < scored.Count;)
        {
            var end = i + 1;
            while (end < scored.Count && ranks[end] == ranks[i]) end++;
            var group = scored.GetRange(i, end - i);
            var rank = ranks[i];

            if (group.Count == 1)
            {
                rows.Add(Row(group[0], rank, rank, FinalTie.None));
            }
            else if (rank > 3)
            {
                // Below the prizes: level works share the place.
                rows.AddRange(group.Select(x => Row(x, rank, rank, FinalTie.SharedPlace)));
            }
            else
            {
                // At a prize, or on the line between third and fourth: only the jury separates them.
                var ids = group.Select(x => x.f.SubmissionId).ToList();
                var decision = decisions.FirstOrDefault(d => d.Count == ids.Count && !d.Except(ids).Any() && !ids.Except(d).Any());
                if (decision is not null)
                {
                    var ordered = decision.Select(id => group.First(x => x.f.SubmissionId == id)).ToList();
                    rows.AddRange(ordered.Select((x, k) => Row(x, rank, rank + k, FinalTie.DecidedByJury)));
                }
                else
                {
                    rows.AddRange(group.Select(x => Row(x, rank, null, FinalTie.NeedsJuryDecision)));
                    unresolved.Add(ids);
                }
            }
            i = end;
        }
        return new FinalTable(rows, unresolved);

        static FinalRow Row((FinalistInput f, int juryRank, int juryPoints, int final) x, int rank, int? place, FinalTie tie) =>
            new(x.f.SubmissionId, x.f.Reference, x.f.ValidVotes, x.f.PublicRank, x.f.PublicPoints,
                x.f.JuryTotal, x.juryRank, x.juryPoints, x.final, rank, place, tie);
    }

    /// <summary>
    /// The order a show reveals the top three in, and the order an announcement reads them: third, second, first.
    /// </summary>
    public static readonly int[] TopThreeRevealOrder = [3, 2, 1];
}
