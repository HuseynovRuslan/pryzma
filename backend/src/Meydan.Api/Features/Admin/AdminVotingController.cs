using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// The public vote, counted for the administration. Read-only: the window is set on the competition itself
/// (where every other date lives) and the winner is chosen on the results screen, which already knows how to
/// record one per nomination. This only says what the vote produced.
/// <para>
/// It names participants, as every admin screen does — an admin choosing a winner has to know whose work it is.
/// It never names a voter: who voted for what is nobody's business, and the tally does not need it.
/// </para>
/// </summary>
[Route("api/admin/competitions/{competitionId:int}/voting")]
public sealed class AdminVotingController(
    MeydanDbContext db, TimeProvider clock, VoteReview review, PrintItems printItems, VoteCatalogAnalytics analytics) : AdminControllerBase
{
    /// <summary>
    /// The catalog of every work with its public vote (admins only): valid votes, public position and the votes set
    /// aside by approved audit rules, per work and per rule. Never sent with the jury's catalog, papers or API.
    /// </summary>
    [HttpGet("catalog")]
    [Jury.JuryNoStore]
    public Task<AdminVoteCatalogDto> Catalog(int competitionId, CancellationToken ct) => analytics.GetAsync(competitionId, ct);

    /// <summary>
    /// Every vote scored for signs of manipulation (<see cref="VoteSuspicion"/>), summed per work, over the votes that
    /// still count, with the votes set aside beside them. Read-only. Unlike the tally it names accounts — those whose
    /// vote scored 30 or more, because an admin cannot check a suspicion about nobody; every other voter stays unnamed.
    /// </summary>
    [HttpGet("suspicion")]
    public Task<AdminVoteSuspicionDto> Suspicion(int competitionId, CancellationToken ct) =>
        review.OverviewAsync(competitionId, ct);

    /// <summary>One work under review: its evidence in numbers, and every vote it received with its signs (D12).</summary>
    [HttpGet("suspicion/works/{submissionId:int}")]
    public Task<AdminWorkReviewDto> SuspicionWork(int competitionId, int submissionId, CancellationToken ct) =>
        review.WorkAsync(competitionId, submissionId, ct);

    /// <summary>
    /// Sets the chosen votes aside as fraudulent — all or none, with a reason, audited. Only votes with identity
    /// evidence qualify, and a vote is never deleted (D12).
    /// </summary>
    [HttpPost("votes/invalidate")]
    public Task<AdminVoteDecisionDto> Invalidate(int competitionId, VoteDecisionRequest req, CancellationToken ct) =>
        review.InvalidateAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>D14 for this competition: the frozen rule, what it meets now, and the masked evidence. Read-only.</summary>
    [HttpGet("rules/disposable-email")]
    public Task<AdminDisposableEmailDto> DisposableEmail(int competitionId, CancellationToken ct) =>
        review.DisposableEmailAsync(competitionId, ct);

    /// <summary>
    /// Applies D14 to every work of the competition in one decision: every valid vote meeting the rule is set aside
    /// (never deleted), with the reason, one audit entry each and one for the decision — only if the count is the one
    /// the admin was shown, and only after the vote has closed.
    /// </summary>
    [HttpPost("rules/disposable-email/apply")]
    public Task<AdminDisposableEmailDto> ApplyDisposableEmail(int competitionId, ApplyVotingRuleRequest req, CancellationToken ct) =>
        review.ApplyDisposableEmailAsync(competitionId, req, CurrentUserId, ct);

    /// <summary>Restores votes set aside earlier — with a reason, audited.</summary>
    [HttpPost("votes/restore")]
    public Task<AdminVoteDecisionDto> Restore(int competitionId, VoteDecisionRequest req, CancellationToken ct) =>
        review.RestoreAsync(competitionId, req, CurrentUserId, ct);

    [HttpGet]
    public async Task<AdminVotingDto> Get(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
                          ?? throw new Common.NotFoundException("Müsabiqə tapılmadı.");

        var categories = await db.CompetitionCategories.AsNoTracking()
            .Where(x => x.CompetitionId == competitionId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new { x.Id, x.Slug, x.Name, x.IsActive })
            .ToListAsync(ct);

        // Final works only: a draft is not on any ballot, and a rejected or withdrawn one has left it.
        var works = await db.Submissions.AsNoTracking()
            .Where(s => s.CompetitionId == competitionId
                        && CompetitionResult.EligibleStatuses.Contains(s.Status)
                        && s.ReferenceNumber != null)
            .Select(s => new
            {
                s.Id,
                Reference = s.ReferenceNumber!,
                s.Title,
                s.Status,
                s.CompetitionCategoryId,
                Participant = (s.User.FirstName + " " + s.User.LastName).Trim(),
            })
            .ToListAsync(ct);

        var counts = (await db.Votes.AsNoTracking()
                .Where(v => v.Category.CompetitionId == competitionId)
                .GroupBy(v => v.SubmissionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);

        // Votes from accounts opened after voting began: allowed, and counted apart so an admin can see them.
        var opened = competition.VotingStartsAt;
        var lateVotes = db.Votes.AsNoTracking()
            .Where(v => v.Category.CompetitionId == competitionId && opened != null && v.User.CreatedAt >= opened);
        var late = (await lateVotes
                .GroupBy(v => v.SubmissionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);
        var lateVoters = await lateVotes.Select(v => v.UserId).Distinct().CountAsync(ct);

        // Set aside by an admin (D12): not in the counts above, shown beside them.
        var setAside = (await db.Votes.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.Category.CompetitionId == competitionId && v.InvalidatedAt != null)
                .GroupBy(v => v.SubmissionId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Count);

        var winners = await db.CompetitionCategoryWinners.AsNoTracking()
            .Where(w => w.CompetitionId == competitionId)
            .Select(w => w.SubmissionId)
            .ToListAsync(ct);

        var voters = await db.Votes.AsNoTracking()
            .Where(v => v.Category.CompetitionId == competitionId)
            .Select(v => v.UserId)
            .Distinct()
            .CountAsync(ct);

        // The official result's places, kept apart from the vote's rank: a ranking row may show both.
        var result = await db.CompetitionResults.AsNoTracking().FirstOrDefaultAsync(r => r.CompetitionId == competitionId, ct);
        int? OfficialPlace(int id) => result?.PlaceOf(id) ?? (winners.Contains(id) ? 1 : null);

        // Each work's thumbnail as the printed cards show it — a PDF's content page, not its cover.
        var pictures = (await printItems.ForAsync([.. works.Select(w => w.Id)], includeAuthor: false, ct)).ToDictionary(i => i.Id);

        AdminVoteWorkDto Row(int id, string reference, string title, string participant, SubmissionStatus status, int place) =>
            new(id, reference, title, participant, status, counts.GetValueOrDefault(id), place, winners.Contains(id),
                late.GetValueOrDefault(id), setAside.GetValueOrDefault(id),
                pictures.GetValueOrDefault(id)?.Image, pictures.GetValueOrDefault(id)?.PdfPages ?? 0, OfficialPlace(id));

        var nominations = new List<AdminVotingNominationDto>();
        foreach (var k in categories)
        {
            // Most votes first; level works keep a stable order by reference so the list does not shuffle on reload.
            var ordered = works.Where(w => w.CompetitionCategoryId == k.Id)
                .OrderByDescending(w => counts.GetValueOrDefault(w.Id))
                .ThenBy(w => w.Reference, StringComparer.Ordinal)
                .ToList();

            var rows = new List<AdminVoteWorkDto>();
            var place = 0;
            var previous = int.MinValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                var votes = counts.GetValueOrDefault(ordered[i].Id);
                // Works level on votes share a place; the next one down takes the place its position implies.
                if (votes != previous)
                {
                    place = i + 1;
                    previous = votes;
                }
                var w = ordered[i];
                rows.Add(Row(w.Id, w.Reference, w.Title, w.Participant, w.Status, place));
            }

            var top = rows.FirstOrDefault()?.VoteCount ?? 0;
            var tie = top > 0 && rows.Count(r => r.VoteCount == top) > 1;
            nominations.Add(new AdminVotingNominationDto(
                k.Id, k.Slug, k.Name, k.IsActive, rows.Count, rows.Sum(r => r.VoteCount), tie, rows));
        }

        var unplaced = works.Where(w => w.CompetitionCategoryId is null)
            .OrderBy(w => w.Reference, StringComparer.Ordinal)
            .Select(w => Row(w.Id, w.Reference, w.Title, w.Participant, w.Status, 0))
            .ToList();

        return new AdminVotingDto(
            competition.VotingStateAt(clock.GetUtcNow()),
            competition.VotingStartsAt, competition.VotingEndsAt,
            voters, counts.Values.Sum(), lateVoters, nominations, unplaced, setAside.Values.Sum());
    }
}
