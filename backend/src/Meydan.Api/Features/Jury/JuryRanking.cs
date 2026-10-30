using Meydan.Api.Common;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Jury;

/// <summary>One work's jury evidence: the sum and the number of its scores (one per juror).</summary>
public sealed record JuryRankingInput(int SubmissionId, string Reference, string? Title, long ScoreSum, int ScoreCount)
{
    /// <summary>The exact average, for display (rounded by the caller); the ranking compares fractions, never this.</summary>
    public decimal Average => ScoreCount == 0 ? 0 : (decimal)ScoreSum / ScoreCount;
}

/// <param name="Position">Competition ranking: works with the same average share the position, the next position skips
/// past them (1, 2, 2, 4).</param>
/// <param name="TieSize">How many works share this position (1: no tie).</param>
public sealed record JuryRankingRow(int Position, JuryRankingInput Work, int TieSize);

/// <summary>
/// The internal ranking of every work by the jury's exact average (<see cref="Domain.Entities.JuryFormats.InternalJuryRankingV1"/>).
/// Averages are compared as fractions (a.sum × b.count against b.sum × a.count), never after rounding. Works with the same
/// average share their position and are marked as tied — nothing (no id, no reference, no vote) separates them; the
/// reference only keeps a tied group in a stable order on screen.
/// </summary>
public static class JuryRanking
{
    public static List<JuryRankingRow> Rank(IEnumerable<JuryRankingInput> works)
    {
        var ordered = works.OrderBy(w => w, Comparer<JuryRankingInput>.Create(Better))
            .ThenBy(w => w.Reference, StringComparer.Ordinal).ToList();
        var rows = new List<JuryRankingRow>(ordered.Count);
        var i = 0;
        while (i < ordered.Count)
        {
            var j = i;
            while (j + 1 < ordered.Count && Better(ordered[i], ordered[j + 1]) == 0) j++;
            var size = j - i + 1;
            for (var k = i; k <= j; k++) rows.Add(new JuryRankingRow(i + 1, ordered[k], size));
            i = j + 1;
        }
        return rows;
    }

    /// <summary>Higher average first; a work without scores after every scored one.</summary>
    private static int Better(JuryRankingInput a, JuryRankingInput b)
    {
        if (a.ScoreCount == 0 || b.ScoreCount == 0) return b.ScoreCount.CompareTo(a.ScoreCount);
        return (b.ScoreSum * a.ScoreCount).CompareTo(a.ScoreSum * b.ScoreCount);
    }
}

/// <summary>
/// Once a competition has a jury evaluation, the earlier combined Top 10 final (the 50/50 formula of public points and
/// jury points) can no longer score, re-freeze, show or publish anything for it. Its records stay, untouched, as
/// history. The public vote and its own results are not affected.
/// </summary>
public static class JuryFormatGuard
{
    public const string Superseded =
        "Bu müsabiqədə münsiflər bütün işləri daxili qiymətləndirir — köhnə 50/50 final yalnız tarixçə kimi saxlanılır.";

    public static async Task EnsureCombinedAllowedAsync(MeydanDbContext db, int competitionId, CancellationToken ct)
    {
        if (await db.JuryEvaluations.AnyAsync(e => e.CompetitionId == competitionId, ct))
            throw new ConflictException(Superseded);
    }
}
