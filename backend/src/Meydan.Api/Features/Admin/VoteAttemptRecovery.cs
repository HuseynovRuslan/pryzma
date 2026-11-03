using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// A server-recorded, authenticated voting attempt that never became a Vote row.
/// An administrator cannot supply a user ID, a work ID or an arbitrary number to recover.
/// </summary>
public sealed record UnrecordedVoteCandidate(long AttemptId, string Reference, DateTimeOffset AttemptedAt);
public sealed record UnrecordedVoteCandidates(IReadOnlyList<UnrecordedVoteCandidate> Items);
public sealed record RecoverUnrecordedVotesRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(100)]
    public long[] AttemptIds { get; init; } = [];
}
public sealed record RecoverUnrecordedVotesResult(int Recovered);

/// <summary>
/// Reconciles only previously validated vote intents stored by the server BEFORE the normal vote
/// transaction. Existing votes, including invalidated ones, always consume a voter's one vote.
/// An accepted intent alone is not a recorded vote; its validity is checked again here.
/// Nothing is changed in the public API structure or an administrative audit list.
/// </summary>
public sealed class VoteAttemptRecovery(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, IMemoryCache cache)
{
    private sealed record Candidate(long AttemptId, Guid UserId, int SubmissionId,
        int CategoryId, string Reference, DateTimeOffset AttemptedAt);

    private async Task<Competition> ClosedCompetitionAsync(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == competitionId, ct)
            ?? throw new NotFoundException("Müsabiqə tapılmadı.");

        if (competition.VotingStateAt(clock.GetUtcNow()) != VotingState.Closed)
            throw new ConflictException("Səs bərpası yalnız səsvermə bitəndən sonra mümkündür.");

        if (await db.CompetitionFinals.AsNoTracking().AnyAsync(x => x.CompetitionId == competitionId, ct) ||
            await db.CompetitionResults.AsNoTracking()
                .AnyAsync(x => x.CompetitionId == competitionId && x.PublishedAt != null, ct))
            throw new ConflictException("Finalçılar təsdiqlənib və ya nəticə dərc edilib. Bərpa bloklanıb.");

        return competition;
    }

    private async Task<List<Candidate>> CandidatesAsync(Competition competition, CancellationToken ct)
    {
        var prefix = $"competition:{competition.Id};";
        // No client-supplied evidence: these entries were written by CastAsync after authentication,
        // work ownership, email-confirmation and (if enabled) captcha checks, before its vote transaction.
        var attempts = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.VoteAttempted &&
                        a.EntityType == AuditEntities.Submission && a.UserId != null &&
                        a.Summary != null && a.Summary.StartsWith(prefix))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.UserId, a.EntityId, a.CreatedAt })
            .ToListAsync(ct);
        if (competition.VotingStartsAt is not { } from || competition.VotingEndsAt is not { } to)
            return [];

        var attemptsInWindow = attempts
            .Where(a => a.CreatedAt >= from && a.CreatedAt < to && a.UserId.HasValue &&
                        int.TryParse(a.EntityId, out _))
            .ToList();
        if (attemptsInWindow.Count == 0) return [];

        var entryIds = attemptsInWindow.Select(a => int.Parse(a.EntityId)).Distinct().ToArray();
        var works = await PublicVoting.BallotWorks(db, competition.Id)
            .Where(s => entryIds.Contains(s.Id))
            .Select(s => new { s.Id, s.UserId, s.CompetitionCategoryId, s.ReferenceNumber })
            .ToDictionaryAsync(s => s.Id, ct);
        var voted = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Category.CompetitionId == competition.Id)
            .Select(v => v.UserId).Distinct().ToListAsync(ct);
        var votedSet = voted.ToHashSet();

        // Multiple distinct choices by one person are ambiguous: do not invent their final intention.
        return attemptsInWindow
            .Where(a => works.TryGetValue(int.Parse(a.EntityId), out var w) &&
                        !votedSet.Contains(a.UserId!.Value) &&
                        w.UserId != a.UserId.Value)
            .GroupBy(a => a.UserId!.Value)
            .Where(g => g.Select(x => x.EntityId).Distinct().Count() == 1)
            .Select(g => g.First())
            .Select(a =>
            {
                var w = works[int.Parse(a.EntityId)];
                return new Candidate(a.Id, a.UserId!.Value, w.Id,
                    w.CompetitionCategoryId!.Value, w.ReferenceNumber!, a.CreatedAt);
            })
            .ToList();
    }

    public async Task<UnrecordedVoteCandidates> ListAsync(int competitionId, CancellationToken ct)
    {
        var competition = await ClosedCompetitionAsync(competitionId, ct);
        var candidates = await CandidatesAsync(competition, ct);
        return new UnrecordedVoteCandidates(candidates
            .Select(x => new UnrecordedVoteCandidate(x.AttemptId, x.Reference, x.AttemptedAt))
            .ToList());
    }

    public async Task<RecoverUnrecordedVotesResult> RecoverAsync(
        int competitionId, RecoverUnrecordedVotesRequest request, Guid adminId, CancellationToken ct)
    {
        var ids = request.AttemptIds;
        if (ids.Length is < 1 or > 100 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw ValidationFailedException.For("attemptIds", "1–100 fərqli, müsbət səs cəhdi ID-si seçin.");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // All reconciliation requests for this competition are serialized.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({-9021}, {competitionId})", ct);
            var competition = await ClosedCompetitionAsync(competitionId, ct);
            var selected = (await CandidatesAsync(competition, ct))
                .Where(x => ids.Contains(x.AttemptId))
                .ToList();
            if (selected.Count != ids.Length)
                throw new ConflictException("Seçilmiş səs cəhdlərindən biri bərpa üçün uyğun deyil. Siyahını yeniləyin.");

            foreach (var attempt in selected)
            {
                db.Votes.Add(new Vote
                {
                    UserId = attempt.UserId,
                    SubmissionId = attempt.SubmissionId,
                    CompetitionCategoryId = attempt.CategoryId,
                    CreatedAt = attempt.AttemptedAt,
                });
                audit.Record(adminId, AuditActions.VoteRecovered, AuditEntities.Submission,
                    attempt.SubmissionId, $"recorded-attempt:{attempt.AttemptId}; competition:{competitionId}");
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConflictException("Səs artıq bazaya yazılıb. Siyahını yeniləyin.");
            }
            await tx.CommitAsync(ct);
        });

        PublicVoting.ForgetLeaderboard(cache, competitionId);
        return new RecoverUnrecordedVotesResult(ids.Length);
    }
}
