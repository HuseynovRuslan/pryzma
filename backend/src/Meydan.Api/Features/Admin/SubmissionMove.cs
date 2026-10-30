using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Features.Admin;

/// <param name="Problems">Why the work cannot go to this competition; empty when it can.</param>
public sealed record SubmissionMoveTargetDto(int Id, string Title, string ProjectName, IReadOnlyList<string> Problems);

/// <param name="Blockers">Why the work cannot be moved anywhere (it already has votes, jury scores, a prize…).</param>
public sealed record SubmissionMoveOptionsDto(
    int SubmissionId, string? Reference, int CompetitionId, string CompetitionTitle,
    IReadOnlyList<string> Blockers, IReadOnlyList<SubmissionMoveTargetDto> Targets);

public sealed record SubmissionMoveRequest
{
    [Required(ErrorMessage = "Köçürüləcək müsabiqəni seçin.")]
    public int? CompetitionId { get; init; }
}

public sealed record SubmissionMovedDto(int SubmissionId, string? Reference, int FromCompetitionId, int ToCompetitionId, string ToCompetitionTitle);

/// <summary>
/// A work an entrant sent to the wrong competition, moved to the right one by an admin. It keeps everything it is:
/// its reference number, status, history, notes, files and answers — each answer and file goes to the field with the
/// same key in the other competition's form, so nothing is retyped and nothing is lost. Its nomination is cleared,
/// because nominations belong to a competition; starting the vote places it again (<see cref="VotingLaunch"/>).
/// <para>
/// Refused while anything already stands on the work in its own competition — a vote, a juror's roster or score, a
/// final, a prize or a result — and while either competition's vote has begun: those are records of a decision made
/// about the work where it is, and moving it would quietly rewrite them.
/// </para>
/// </summary>
public sealed class SubmissionMove(MeydanDbContext db, AuditTrail audit, TimeProvider clock)
{
    private sealed record Source(Submission Submission, Competition Competition, List<SubmissionAnswer> Answers, List<SubmissionFile> Files);

    public async Task<SubmissionMoveOptionsDto> OptionsAsync(int submissionId, CancellationToken ct)
    {
        var source = await LoadAsync(submissionId, tracked: false, ct);
        var blockers = await BlockersAsync(source, ct);
        var targets = await db.Competitions.AsNoTracking().Include(c => c.Project).Include(c => c.Fields)
            .Where(c => c.Id != source.Competition.Id && !c.IsDemo && c.Status != CompetitionStatus.Archived)
            .OrderBy(c => c.Project.SortOrder).ThenBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync(ct);
        var list = new List<SubmissionMoveTargetDto>();
        foreach (var t in targets)
            list.Add(new SubmissionMoveTargetDto(t.Id, t.Title, t.Project.Name, await ProblemsAsync(source, t, ct)));
        return new SubmissionMoveOptionsDto(
            source.Submission.Id, source.Submission.ReferenceNumber, source.Competition.Id, source.Competition.Title, blockers, list);
    }

    public async Task<SubmissionMovedDto> MoveAsync(int submissionId, SubmissionMoveRequest req, Guid adminId, CancellationToken ct)
    {
        var targetId = req.CompetitionId!.Value;
        SubmissionMovedDto? moved = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var source = await LoadAsync(submissionId, tracked: true, ct);
            if (source.Competition.Id == targetId)
                throw ValidationFailedException.For("competitionId", "Təklif artıq bu müsabiqədədir.");
            var target = await db.Competitions.Include(c => c.Fields).SingleOrDefaultAsync(c => c.Id == targetId && !c.IsDemo, ct)
                         ?? throw new NotFoundException("Müsabiqə tapılmadı.");

            var refused = (await BlockersAsync(source, ct)).Concat(await ProblemsAsync(source, target, ct)).ToList();
            if (refused.Count > 0) throw new ConflictException(string.Join(" ", refused));

            var byKey = target.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
            foreach (var answer in source.Answers)
            {
                // A field that has no counterpart and was left blank holds nothing to carry over.
                if (!byKey.TryGetValue(answer.Field.Key, out var to)) db.SubmissionAnswers.Remove(answer);
                else answer.CompetitionFieldId = to.Id;
            }
            foreach (var file in source.Files.Where(f => f.CompetitionFieldId is not null))
                file.CompetitionFieldId = byKey[source.Competition.Fields.Single(f => f.Id == file.CompetitionFieldId).Key].Id;
            await db.SaveChangesAsync(ct);

            // The competition and the nomination are part of the keys that votes, results and the jury point at, so
            // they are written in one statement rather than edited through a tracked entry; nothing points at them
            // here (checked above, and the database would refuse the update if anything did).
            var now = clock.GetUtcNow();
            var from = source.Competition.Id;
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""UPDATE "Submissions" SET "CompetitionId" = {targetId}, "CompetitionCategoryId" = NULL, "UpdatedAt" = {now}, "ConcurrencyStamp" = {Guid.NewGuid()} WHERE "Id" = {submissionId} AND "CompetitionId" = {from}""",
                    ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ConflictException("Bu iş artıq öz müsabiqəsində qərarlarda (səs, münsif, nəticə) istifadə olunur — köçürmək olmaz.");
            }

            var reference = source.Submission.ReferenceNumber ?? $"#{submissionId}";
            audit.Record(adminId, AuditActions.SubmissionMoved, AuditEntities.Submission, submissionId,
                $"{reference}: {source.Competition.Slug} → {target.Slug}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            moved = new SubmissionMovedDto(submissionId, source.Submission.ReferenceNumber, from, targetId, target.Title);
        });
        return moved!;
    }

    private async Task<Source> LoadAsync(int submissionId, bool tracked, CancellationToken ct)
    {
        var submissions = tracked ? db.Submissions : db.Submissions.AsNoTracking();
        var s = await submissions.SingleOrDefaultAsync(x => x.Id == submissionId && !x.IsDemo, ct)
                ?? throw new NotFoundException("Təklif tapılmadı.");
        var competition = await db.Competitions.AsNoTracking().Include(c => c.Fields).SingleAsync(c => c.Id == s.CompetitionId, ct);
        var answers = tracked ? db.SubmissionAnswers : db.SubmissionAnswers.AsNoTracking();
        var files = tracked ? db.SubmissionFiles : db.SubmissionFiles.AsNoTracking();
        return new Source(s, competition,
            await answers.Include(a => a.Field).Where(a => a.SubmissionId == submissionId).ToListAsync(ct),
            await files.Where(f => f.SubmissionId == submissionId).ToListAsync(ct));
    }

    /// <summary>What already stands on the work where it is.</summary>
    private async Task<List<string>> BlockersAsync(Source source, CancellationToken ct)
    {
        var id = source.Submission.Id;
        var now = clock.GetUtcNow();
        var blockers = new List<string>();
        if (source.Competition.VotingStateAt(now) is VotingState.Open or VotingState.Closed)
            blockers.Add("Bu müsabiqədə səsvermə başlayıb — işi köçürmək olmaz.");
        if (await db.Votes.IgnoreQueryFilters().AnyAsync(v => v.SubmissionId == id, ct))
            blockers.Add("İşə səs verilib.");
        if (await db.JuryRosterEntries.AnyAsync(r => r.SubmissionId == id, ct) || await db.JuryWorkScores.AnyAsync(w => w.SubmissionId == id, ct))
            blockers.Add("İş münsiflərin siyahısındadır.");
        if (await db.CompetitionFinalists.AnyAsync(f => f.SubmissionId == id, ct))
            blockers.Add("İş finaldadır.");
        if (await db.CompetitionAwards.AnyAsync(a => a.SubmissionId == id, ct)
            || await db.CompetitionCategoryWinners.AnyAsync(w => w.SubmissionId == id, ct)
            || await db.CompetitionResults.AnyAsync(r => r.OverallWinnerSubmissionId == id || r.SecondPlaceSubmissionId == id || r.ThirdPlaceSubmissionId == id, ct))
            blockers.Add("İş nəticədə və ya mükafatda qeyd olunub.");
        return blockers;
    }

    /// <summary>Why this one competition cannot take the work: its vote, its form, its limit.</summary>
    private async Task<List<string>> ProblemsAsync(Source source, Competition target, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var problems = new List<string>();
        if (target.Status == CompetitionStatus.Archived) problems.Add("Müsabiqə arxivdədir.");
        if (target.VotingStateAt(now) is VotingState.Open or VotingState.Closed)
            problems.Add("Bu müsabiqədə səsvermə başlayıb.");

        var keys = target.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var used = source.Answers.Where(a => !string.IsNullOrWhiteSpace(a.Value)).Select(a => a.Field)
            .Concat(source.Files.Where(f => f.CompetitionFieldId is not null)
                .Select(f => source.Competition.Fields.Single(x => x.Id == f.CompetitionFieldId)))
            .DistinctBy(f => f.Id);
        foreach (var field in used)
        {
            if (!keys.TryGetValue(field.Key, out var to))
                problems.Add($"Formada “{field.Label}” ({field.Key}) sahəsi yoxdur.");
            else if (to.FieldType != field.FieldType)
                problems.Add($"Formada “{to.Label}” sahəsinin növü fərqlidir.");
        }

        if (target.MaxSubmissionsPerUser is { } max && Submission.CountsTowardsLimit(source.Submission.Status))
        {
            var userId = source.Submission.UserId;
            var held = await db.Submissions.Where(s => s.CompetitionId == target.Id && s.UserId == userId).Select(s => s.Status).ToListAsync(ct);
            if (held.Count(Submission.CountsTowardsLimit) >= max)
                problems.Add($"İştirakçının bu müsabiqədə artıq {max} işi var (limit).");
        }
        return problems;
    }
}

/// <summary>"Başqa müsabiqəyə köçür" on the admin's submission page.</summary>
[Route("api/admin/submissions/{id:int}/move")]
public sealed class AdminSubmissionMoveController(SubmissionMove move) : AdminControllerBase
{
    [HttpGet]
    public Task<SubmissionMoveOptionsDto> Options(int id, CancellationToken ct) => move.OptionsAsync(id, ct);

    [HttpPost]
    public Task<SubmissionMovedDto> Move(int id, SubmissionMoveRequest req, CancellationToken ct) =>
        move.MoveAsync(id, req, CurrentUserId, ct);
}
