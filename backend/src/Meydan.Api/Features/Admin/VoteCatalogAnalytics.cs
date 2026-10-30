using Meydan.Api.Common;
using Meydan.Api.Features.Voting;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>Votes set aside, per approved rule (D12 phone cluster, R2 coordinated identity, D14 disposable e-mail).</summary>
public sealed record VoteAuditBreakdownDto(int DuplicatePhone, int CoordinatedAccounts, int DisposableEmail)
{
    public int Total => DuplicatePhone + CoordinatedAccounts + DisposableEmail;
}

/// <param name="Position">The work's public position by valid votes among every work on the ballot (competition
/// ranking: level works share it).</param>
/// <param name="TieSize">How many works share that position.</param>
/// <param name="Invalidated">Votes this work lost to approved audit decisions, by rule.</param>
public sealed record AdminVoteCatalogWorkDto(
    int Position, int TieSize, int SubmissionId, string Reference, string? Title, AdminPrintImageDto? Image, int PdfPages,
    int ValidVotes, int InvalidatedVotes, VoteAuditBreakdownDto Invalidated);

/// <param name="Works">Every work on the ballot, most valid votes first (reference order within a level group).</param>
/// <param name="PendingReview">Votes waiting for a manual review decision — null: no such state is recorded anywhere,
/// so nothing is shown as pending.</param>
/// <param name="OutsideCatalog">Votes of the competition cast for works no longer on the ballot (counted, not shown).</param>
public sealed record AdminVoteCatalogDto(
    string CompetitionTitle, int ValidVotes, int InvalidatedVotes, VoteAuditBreakdownDto Invalidated, int WorksWithInvalidations,
    int? PendingReview, int OutsideCatalog, IReadOnlyList<AdminVoteCatalogWorkDto> Works, DateTimeOffset GeneratedAt);

/// <summary>One work's public vote: its position among the works counted together, valid votes and votes set aside.</summary>
public sealed record WorkVoteStats(int Position, int TieSize, int ValidVotes, VoteAuditBreakdownDto Invalidated)
{
    public int InvalidatedVotes => Invalidated.Total;
}

/// <summary>
/// The public vote over the catalog of every work, for the administration only: each work's valid votes, its public
/// position and the votes it lost to approved audit decisions, by rule — counted from the votes themselves, classified
/// by <see cref="VoteAuditRules"/> exactly as the public audit is. It labels decisions already taken, never a person or a
/// risk. Read-only. The jury sees only each work's aggregate (<see cref="StatsAsync"/>: position, valid, set aside) —
/// never the rules, a voter or the evidence.
/// </summary>
public sealed class VoteCatalogAnalytics(MeydanDbContext db, PrintItems printItems, TimeProvider clock)
{
    public async Task<AdminVoteCatalogDto> GetAsync(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
                          ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        var works = await PublicVoting.BallotWorks(db, competitionId)
            .Select(s => new { s.Id, Reference = s.ReferenceNumber! })
            .ToListAsync(ct);

        var stats = await StatsAsync(competitionId, [.. works.Select(w => w.Id)], ct);
        var ordered = works
            .Select(w => (w.Id, w.Reference, Stats: stats[w.Id]))
            .OrderBy(w => w.Stats.Position).ThenBy(w => w.Reference, StringComparer.Ordinal)
            .ToList();
        var items = (await printItems.ForAsync([.. ordered.Select(w => w.Id)], includeAuthor: false, ct)).ToDictionary(i => i.Id);
        var rows = ordered.Select(w =>
        {
            var item = items.GetValueOrDefault(w.Id);
            return new AdminVoteCatalogWorkDto(w.Stats.Position, w.Stats.TieSize, w.Id, w.Reference, item?.Title, item?.Image,
                item?.PdfPages ?? 0, w.Stats.ValidVotes, w.Stats.InvalidatedVotes, w.Stats.Invalidated);
        }).ToList();

        var shown = rows.Select(r => r.SubmissionId).ToHashSet();
        return new AdminVoteCatalogDto(competition.Title,
            rows.Sum(r => r.ValidVotes), rows.Sum(r => r.InvalidatedVotes),
            new VoteAuditBreakdownDto(rows.Sum(r => r.Invalidated.DuplicatePhone), rows.Sum(r => r.Invalidated.CoordinatedAccounts),
                rows.Sum(r => r.Invalidated.DisposableEmail)),
            rows.Count(r => r.InvalidatedVotes > 0), PendingReview: null,
            await db.Votes.IgnoreQueryFilters().CountAsync(v => v.Category.CompetitionId == competitionId && !shown.Contains(v.SubmissionId), ct),
            rows, clock.GetUtcNow());
    }

    /// <summary>
    /// The public vote of these works, counted together: valid votes, votes set aside per approved rule
    /// (<see cref="VoteAuditRules"/>, over every vote of the competition) and the position by valid votes among them —
    /// competition ranking, level works sharing it. Every id is in the answer, with zeros when it has no vote.
    /// </summary>
    public async Task<Dictionary<int, WorkVoteStats>> StatsAsync(int competitionId, IReadOnlyCollection<int> submissionIds, CancellationToken ct)
    {
        // Every vote of the competition, set aside or not (the query filter would hide the set-aside ones); the phone
        // only tells the D12 rules apart, here.
        var votes = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Category.CompetitionId == competitionId)
            .Select(v => new { v.UserId, v.SubmissionId, v.InvalidatedAt, v.InvalidationReason, Phone = v.User.PhoneNumber })
            .ToListAsync(ct);
        var ruleOf = VoteAuditRules.Classifier(votes.Select(v => (v.UserId, v.Phone)));
        var bySubmission = votes.ToLookup(v => v.SubmissionId);
        var counts = submissionIds.Distinct().ToDictionary(id => id, id =>
        {
            var mine = bySubmission[id].ToList();
            var setAside = mine.Where(v => v.InvalidatedAt is not null).Select(v => ruleOf(v.InvalidationReason, v.Phone)).ToList();
            return (Valid: mine.Count(v => v.InvalidatedAt is null), Rules: new VoteAuditBreakdownDto(
                setAside.Count(r => r == VotingAuditReason.DuplicatePhone),
                setAside.Count(r => r == VotingAuditReason.CoordinatedAccounts),
                setAside.Count(r => r == VotingAuditReason.DisposableEmail)));
        });
        return counts.ToDictionary(c => c.Key, c => new WorkVoteStats(
            counts.Values.Count(x => x.Valid > c.Value.Valid) + 1, counts.Values.Count(x => x.Valid == c.Value.Valid),
            c.Value.Valid, c.Value.Rules));
    }
}
