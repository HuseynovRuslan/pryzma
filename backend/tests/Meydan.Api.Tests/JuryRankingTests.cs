using Meydan.Api.Features.Jury;

namespace Meydan.Api.Tests;

/// <summary>
/// The internal ranking rule on its own: exact averages compared as fractions, competition ranking, and equal averages
/// sharing a position and marked as tied — never separated by an id, a reference or anything else.
/// </summary>
public sealed class JuryRankingTests
{
    private static JuryRankingInput W(int id, long sum, int count = 3) => new(id, $"SUB-{id:00000}", null, sum, count);

    [Fact]
    public void Averages_are_compared_exactly_not_after_rounding()
    {
        // 22/3 = 7.3333… and 29/4 = 7.25 differ, though both round to 7.3; 44/6 is exactly 22/3.
        var rows = JuryRanking.Rank([W(1, 29, 4), W(2, 22, 3), W(3, 44, 6), W(4, 10, 3)]);
        Assert.Equal([2, 3, 1, 4], rows.Select(r => r.Work.SubmissionId));
        Assert.Equal([1, 1, 3, 4], rows.Select(r => r.Position));
        Assert.Equal([2, 2, 1, 1], rows.Select(r => r.TieSize));
    }

    [Fact]
    public void A_tie_at_the_top_is_shown_as_a_tie_and_nobody_is_made_first()
    {
        var rows = JuryRanking.Rank([W(7, 27), W(3, 27), W(5, 27), W(1, 26)]);
        Assert.All(rows.Take(3), r =>
        {
            Assert.Equal(1, r.Position);
            Assert.Equal(3, r.TieSize);
        });
        // The next position skips past the three (competition ranking), and the reference only orders the group on screen.
        Assert.Equal((4, 1), (rows[3].Position, rows[3].TieSize));
        Assert.Equal([3, 5, 7], rows.Take(3).Select(r => r.Work.SubmissionId));
    }

    [Fact]
    public void Every_work_is_ranked_once_and_a_work_without_scores_comes_last()
    {
        var rows = JuryRanking.Rank([W(1, 0, 0), W(2, 30), W(3, 3), W(4, 0, 0)]);
        Assert.Equal([2, 3, 1, 4], rows.Select(r => r.Work.SubmissionId));
        Assert.Equal([1, 2, 3, 3], rows.Select(r => r.Position));
        Assert.Equal(4, rows.Select(r => r.Work.SubmissionId).Distinct().Count());
    }

    [Fact]
    public void Ninety_nine_works_get_positions_1_to_99_with_ties_sharing()
    {
        // Three jurors, whole scores: sums 3–30, so many works are level — as the real 99 will be.
        var works = Enumerable.Range(1, 99).Select(i => W(i, 3 + (i * 7 % 28))).ToList();
        var rows = JuryRanking.Rank(works);
        Assert.Equal(99, rows.Count);
        Assert.Equal(1, rows[0].Position);
        Assert.True(rows.Last().Position <= 99);
        // A position is shared by exactly TieSize works, and the next one starts right after them.
        foreach (var group in rows.GroupBy(r => r.Position))
        {
            Assert.Equal(group.Count(), group.First().TieSize);
            Assert.Single(group.Select(r => r.Work.ScoreSum).Distinct());
        }
        var positions = rows.Select(r => r.Position).Distinct().ToList();
        for (var k = 1; k < positions.Count; k++)
            Assert.Equal(positions[k - 1] + rows.Count(r => r.Position == positions[k - 1]), positions[k]);
    }
}
