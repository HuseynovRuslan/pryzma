using Meydan.Api.Features.Final;
using Meydan.Domain.Entities;

namespace Meydan.Api.Tests;

/// <summary>
/// The final's arithmetic on its own (D13): points 10→1 down each ranking, level results share a rank and its points,
/// final score = public + jury points, separated by raw jury score and then valid votes — and never by id or reference.
/// </summary>
public sealed class FinalScoringTests
{
    private static FinalistInput F(int id, int votes, int publicRank, decimal jury) =>
        new(id, $"SUB-{id:00}", votes, publicRank, FinalScoring.PointsFor(publicRank), jury);

    [Fact]
    public void Points_run_from_ten_for_the_first_to_one_for_the_tenth()
    {
        Assert.Equal([10, 9, 8, 7, 6, 5, 4, 3, 2, 1], Enumerable.Range(1, 10).Select(FinalScoring.PointsFor));
        Assert.Equal(0, FinalScoring.PointsFor(11));
    }

    [Fact]
    public void Level_results_share_a_rank_and_the_next_rank_is_skipped()
    {
        int[] votes = [196, 150, 150, 90, 90, 90, 12];
        Assert.Equal([1, 2, 2, 4, 4, 4, 7], FinalScoring.CompetitionRanks(votes, (a, b) => a == b));
        // Shared rank, shared points: 1st = 10, 2nd = 9, 2nd = 9, 4th = 7.
        Assert.Equal([10, 9, 9, 7], FinalScoring.CompetitionRanks(votes, (a, b) => a == b).Take(4).Select(FinalScoring.PointsFor));
    }

    [Fact]
    public void Marks_are_zero_to_ten_in_steps_of_a_half()
    {
        Assert.All(new[] { 0m, 0.5m, 1m, 7.5m, 9.5m, 10m, 10.0m }, m => Assert.True(JuryScore.IsValidMark(m)));
        Assert.All(new[] { -0.5m, 10.5m, 7.25m, 3.3m, 11m }, m => Assert.False(JuryScore.IsValidMark(m)));
        var s = new JuryScore { Idea = 9.5m, Execution = 10m, Relevance = 7.5m };
        Assert.Equal(27m, s.Total);
        Assert.True(s.IsComplete);
        Assert.False(new JuryScore { Idea = 9.5m, Execution = 10m }.IsComplete);
    }

    [Fact]
    public void The_final_score_is_public_points_plus_jury_points_and_level_scores_go_to_the_jury_then_the_votes()
    {
        var table = FinalScoring.Compute(
        [
            F(1, 196, 1, 30m), // 10 + 5 = 15
            F(2, 150, 2, 50m), // 9 + 9 = 18
            F(3, 120, 3, 45m), // 8 + 8 = 16
            F(4, 100, 4, 40m), // 7 + 7 = 14
            F(5, 90, 5, 55m),  // 6 + 10 = 16 — level with 3 on 16: higher raw jury score wins
            F(6, 80, 6, 35m),  // 5 + 6 = 11
            F(7, 70, 7, 25m),  // 4 + 4 = 8
            F(8, 60, 8, 20m),  // 3 + 3 = 6
            F(9, 50, 9, 15m),  // 2 + 2 = 4
            F(10, 40, 10, 10m), // 1 + 1 = 2
        ], []);

        Assert.Equal([2, 5, 3, 1, 4, 6, 7, 8, 9, 10], table.Rows.Select(r => r.SubmissionId));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], table.Rows.Select(r => r.Place!.Value));
        var five = table.Rows.Single(r => r.SubmissionId == 5);
        Assert.Equal((1, 10, 6, 16), (five.JuryRank, five.JuryPoints, five.PublicPoints, five.FinalScore));
        Assert.True(table.PrizesDecided);
        Assert.Equal(2, table.AtPlace(1)!.SubmissionId);
    }

    [Fact]
    public void Level_on_score_the_raw_jury_score_decides_then_the_valid_votes()
    {
        var table = FinalScoring.Compute(
        [
            F(1, 200, 1, 40m),
            F(2, 180, 2, 40m),
            F(3, 100, 3, 50m),
        ], []);
        // 1: 10 + 9 = 19 (jury 40 shares the jury's second rank with 2), 2: 9 + 9 = 18, 3: 8 + 10 = 18 — 2 and 3
        // level on 18; jury 40 < 50, so 3 is ahead.
        Assert.Equal([1, 3, 2], table.Rows.Select(r => r.SubmissionId).ToArray());

        // Level on final score and on jury total, the valid votes decide. With frozen data this cannot happen — equal
        // points on both sides mean an equal public rank, so equal votes — but the rule is the owner's, and it holds.
        table = FinalScoring.Compute(
        [
            new FinalistInput(1, "SUB-01", 110, 2, 9, 40m),
            new FinalistInput(2, "SUB-02", 120, 2, 9, 40m),
        ], []);
        Assert.Equal([2, 1], table.Rows.Select(r => r.SubmissionId).ToArray());
        Assert.All(table.Rows, r => Assert.Equal(FinalTie.None, r.Tie));
    }

    [Fact]
    public void A_tie_at_a_prize_waits_for_the_jury_and_is_never_settled_by_reference()
    {
        // 3 and 4 are level on everything — final score, jury total, valid votes — at second place.
        FinalistInput[] finalists =
        [
            F(2, 150, 2, 50m), // 9 + 10 = 19
            F(3, 120, 3, 45m), // 8 + 9 = 17
            F(4, 120, 3, 45m), // 8 + 9 = 17
            F(1, 196, 1, 25m), // 10 + 7 = 17 too, but jury 25 < 45: fourth
        ];
        var table = FinalScoring.Compute(finalists, []);
        Assert.False(table.PrizesDecided);
        var group = Assert.Single(table.UnresolvedTies);
        Assert.Equal([3, 4], group.Order());
        Assert.All(table.Rows.Where(r => r.SubmissionId is 3 or 4), r =>
        {
            Assert.Equal(FinalTie.NeedsJuryDecision, r.Tie);
            Assert.Null(r.Place);
            Assert.Equal(2, r.Rank);
        });
        Assert.Null(table.AtPlace(2));
        Assert.Null(table.AtPlace(3));

        // The jury decides 4 before 3; the decision applies to exactly that pair.
        table = FinalScoring.Compute(finalists, [[4, 3]]);
        Assert.True(table.PrizesDecided);
        Assert.Equal((4, 2), (table.AtPlace(2)!.SubmissionId, table.AtPlace(2)!.Place!.Value));
        Assert.Equal(3, table.AtPlace(3)!.SubmissionId);
        Assert.All(table.Rows.Where(r => r.SubmissionId is 3 or 4), r => Assert.Equal(FinalTie.DecidedByJury, r.Tie));

        // A decision about another set of works does not apply.
        table = FinalScoring.Compute(finalists, [[4, 3, 1]]);
        Assert.False(table.PrizesDecided);
    }

    [Fact]
    public void A_tie_across_third_and_fourth_needs_the_jury_and_one_below_the_prizes_shares_the_place()
    {
        FinalistInput[] finalists =
        [
            F(1, 196, 1, 60m), // 10 + 10 = 20
            F(2, 150, 2, 55m), // 9 + 9 = 18
            F(3, 120, 3, 40m), // 8 + 7 = 15 (jury rank 4)
            F(4, 120, 3, 40m), // 8 + 7 = 15 — level with 3 at third: the 3/4 line
            F(5, 90, 5, 50m),  // 6 + 8 = 14
            F(6, 80, 6, 30m),  // 5 + 5 = 10 (jury rank 6, shares with 7)
            F(7, 80, 6, 30m),  // 5 + 5 = 10 — level at sixth: shared, no decision needed
        ];
        var table = FinalScoring.Compute(finalists, []);
        Assert.Equal([3, 4], Assert.Single(table.UnresolvedTies).Order());
        Assert.All(table.Rows.Where(r => r.SubmissionId is 6 or 7), r =>
        {
            Assert.Equal(FinalTie.SharedPlace, r.Tie);
            Assert.Equal(6, r.Place);
        });
        Assert.Equal(5, table.Rows.Single(r => r.SubmissionId == 5).Place);
    }

    [Fact]
    public void Prizes_map_and_the_top_three_are_revealed_third_second_first()
    {
        var final = new CompetitionFinal { FirstPrizeAzn = 250, SecondPrizeAzn = 150, ThirdPrizeAzn = 100 };
        Assert.Equal([250, 150, 100, 0], new[] { 1, 2, 3, 4 }.Select(final.PrizeOf));
        Assert.Equal([3, 2, 1], FinalScoring.TopThreeRevealOrder);
        Assert.Equal(FinalAdministration.DefaultPrizes, [250, 150, 100]);
    }
}
