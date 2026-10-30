using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meydan.Api.Common;
using Meydan.Api.Features.Admin;
using Meydan.Api.Features.Final;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Features.Jury;

/// <summary>
/// The jury's evaluation of every eligible work (<see cref="JuryFormats.InternalJuryRankingV1"/>, owner, 8 October 2026)
/// and the internal ranking it leads to — for the administration only.
/// <list type="bullet">
/// <item>The works are the ballot's own (<see cref="PublicVoting.BallotWorks"/>), frozen into a roster before scoring;
/// once a score exists the roster never changes.</item>
/// <item>Every juror scores every work once, 1–10 whole. Online and from paper it is the same row (competition, juror,
/// work); paper never overwrites a different score without an explicit, reasoned reconciliation.</item>
/// <item>A juror's submission locks their scores; only an admin reopens them, with a reason.</item>
/// <item>Once every juror has submitted, every work is ranked 1 to N by its exact average; equal averages share their
/// position and are marked as tied. The ranking is internal: no public page or endpoint reads it, it awards no prize and
/// it never touches the public vote or its results.</item>
/// <item>The jury evaluates informed: the catalog, on paper and online, lists the works most valid public votes first,
/// each with its public position, valid votes and votes set aside — aggregate numbers only. The ranking never reads
/// them.</item>
/// </list>
/// Every write is audited. The earlier combined Top 10 final stays as history and is not consulted.
/// </summary>
public sealed class JuryEvaluationService(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, PrintItems printItems, JuryAccess access, FinalAdministration finals,
    VoteCatalogAnalytics publicVote)
{
    private const string NoEvaluation = "Bu müsabiqə üçün münsif qiymətləndirməsi yaradılmayıb.";
    private const string Concurrent = "Məlumat eyni anda başqa yerdən dəyişdirilib. Səhifəni yeniləyin.";

    // ---- Reading ---------------------------------------------------------------------------------------------------

    private Task<JuryEvaluation?> LoadAsync(int competitionId, bool tracked, CancellationToken ct)
    {
        var q = db.JuryEvaluations
            .Include(e => e.Competition)
            .Include(e => e.Roster)
            .Include(e => e.Assignments).ThenInclude(a => a.Juror)
            .AsSplitQuery();
        return (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(e => e.CompetitionId == competitionId, ct);
    }

    private async Task<JuryEvaluation> RequireAsync(int competitionId, bool tracked, CancellationToken ct) =>
        await LoadAsync(competitionId, tracked, ct) ?? throw new NotFoundException(NoEvaluation);

    private static List<JuryAssignment> Jurors(JuryEvaluation e) =>
        [.. e.Assignments.OrderBy(a => a.Juror.SortOrder).ThenBy(a => a.JurorId)];

    private static List<JuryRosterEntry> Roster(JuryEvaluation e) => [.. e.Roster.OrderBy(r => r.SortOrder)];

    /// <summary>
    /// The roster in catalog order — most valid public votes first, reference order within a level group — with each
    /// work's public vote as the jury sees it. The same order on paper, online and in the admin's matrix; only the
    /// order and the numbers shown, never an input of the ranking.
    /// </summary>
    private async Task<(List<JuryRosterEntry> Works, Dictionary<int, JuryPublicVoteDto> Votes)> CatalogAsync(JuryEvaluation e, CancellationToken ct)
    {
        var roster = Roster(e);
        var stats = await publicVote.StatsAsync(e.CompetitionId, [.. roster.Select(r => r.SubmissionId)], ct);
        return ([.. roster.OrderBy(r => stats[r.SubmissionId].Position).ThenBy(r => r.SortOrder)],
            stats.ToDictionary(s => s.Key, s => new JuryPublicVoteDto(s.Value.Position, s.Value.TieSize, s.Value.ValidVotes, s.Value.InvalidatedVotes)));
    }

    private Task<List<JuryWorkScore>> ScoresAsync(int evaluationId, CancellationToken ct) =>
        db.JuryWorkScores.AsNoTracking().Where(s => s.EvaluationId == evaluationId).ToListAsync(ct);

    private static string Status(JuryAssignment a, int scored) =>
        a.SubmittedAt is not null ? "Submitted" : scored == 0 ? "NotStarted" : "InProgress";

    public async Task<AdminJuryEvaluationDto> GetAsync(int competitionId, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
                          ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        var eligible = await PublicVoting.BallotWorks(db, competitionId).Select(s => s.Id).ToListAsync(ct);
        var hasFinal = await db.CompetitionFinals.AnyAsync(f => f.CompetitionId == competitionId, ct);
        var e = await LoadAsync(competitionId, tracked: false, ct);
        if (e is null)
            return new AdminJuryEvaluationDto(false, competitionId, competition.Title, null, null, null, null, eligible.Count, 0,
                false, false, 0, 0, 0, 0, [], hasFinal);

        var now = clock.GetUtcNow();
        var scores = await ScoresAsync(e.Id, ct);
        var roster = Roster(e);
        var jurors = Jurors(e);
        var byJuror = scores.GroupBy(s => s.JurorId).ToDictionary(g => g.Key, g => g.Count());
        var expected = roster.Count * jurors.Count;
        var progress = jurors.Select(a =>
        {
            var scored = byJuror.GetValueOrDefault(a.JurorId);
            return new AdminJurorProgressDto(a.JurorId, a.Juror.Label, scored, roster.Count - scored, Status(a, scored), a.SubmittedAt,
                a.SubmittedById is not null,
                new JurorInvitationStateDto(JuryAccess.InvitationState(a, now), a.InviteIssuedAt, a.InviteExpiresAt, a.LastSignInAt,
                    a.IdentityConfirmedAt, a.FailedCodeAttempts));
        }).ToList();
        var rosterIds = roster.Select(r => r.SubmissionId).ToHashSet();
        return new AdminJuryEvaluationDto(true, competitionId, competition.Title, e.Format, e.Status.ToString(), e.CreatedAt,
            e.RosterFrozenAt, eligible.Count, roster.Count,
            e.RosterFrozenAt is not null && !rosterIds.SetEquals(eligible), scores.Count > 0,
            jurors.Count, expected, scores.Count, expected - scores.Count, progress, hasFinal);
    }

    // ---- Setting up ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Switches the competition to the new format: an evaluation with every juror of its final assigned (the existing
    /// juror records — no duplicates). From here the combined Top 10 final can no longer be scored, shown or published.
    /// </summary>
    public async Task<AdminJuryEvaluationDto> CreateAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        if (await db.JuryEvaluations.AnyAsync(e => e.CompetitionId == competitionId, ct))
            throw new ConflictException("Bu müsabiqə üçün münsif qiymətləndirməsi artıq var.");
        var final = await db.CompetitionFinals.Include(f => f.Jurors).FirstOrDefaultAsync(f => f.CompetitionId == competitionId, ct)
                    ?? throw new ConflictException("Münsiflər Final bölməsində saxlanılır: əvvəlcə final yaradılmalıdır.");
        if (final.Stage == FinalStage.Published)
            throw new ConflictException("Bu müsabiqənin nəticəsi köhnə formatda dərc olunub — format dəyişmir.");
        var now = clock.GetUtcNow();
        var e = new JuryEvaluation { CompetitionId = competitionId, Status = JuryEvaluationStatus.Draft, CreatedAt = now, CreatedById = adminId };
        foreach (var j in final.Jurors.OrderBy(j => j.SortOrder))
            e.Assignments.Add(new JuryAssignment { JurorId = j.Id, CreatedAt = now });
        db.JuryEvaluations.Add(e);
        audit.Record(adminId, AuditActions.JuryEvaluationCreated, AuditEntities.Competition, competitionId,
            $"{JuryFormats.InternalJuryRankingV1}: jurors {string.Join(", ", final.Jurors.OrderBy(j => j.SortOrder).Select(j => j.Label))}");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// Freezes the list of works every juror scores: the ballot's works now, in reference order. It can be frozen again
    /// only while no score exists — after that it never changes, whatever happens to the ballot.
    /// </summary>
    public async Task<AdminJuryEvaluationDto> FreezeRosterAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: true, ct);
        if (await db.JuryWorkScores.AnyAsync(s => s.EvaluationId == e.Id, ct))
            throw new ConflictException("Ballar artıq daxil edilib — işlərin siyahısı dəyişmir.");
        var works = await PublicVoting.BallotWorks(db, competitionId)
            .Select(s => new { s.Id, Reference = s.ReferenceNumber! })
            .ToListAsync(ct);
        if (works.Count == 0) throw new ConflictException("Səsvermədə iştirak edən iş yoxdur.");
        var ordered = works.OrderBy(w => w.Reference, StringComparer.Ordinal).ToList();
        var previous = e.Roster.Count;
        db.JuryRosterEntries.RemoveRange(e.Roster);
        await SaveAsync(ct);
        var now = clock.GetUtcNow();
        for (var i = 0; i < ordered.Count; i++)
            db.JuryRosterEntries.Add(new JuryRosterEntry
            {
                EvaluationId = e.Id, CompetitionId = competitionId, SubmissionId = ordered[i].Id, Reference = ordered[i].Reference, SortOrder = i + 1,
            });
        e.RosterFrozenAt = now;
        e.RosterFrozenById = adminId;
        e.Status = JuryEvaluationStatus.Open;
        var fingerprint = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(string.Join(";", ordered.Select(w => w.Reference))))).ToLowerInvariant();
        audit.Record(adminId, AuditActions.JuryRosterFrozen, AuditEntities.Competition, competitionId,
            $"{ordered.Count} works{(previous > 0 ? $" (was {previous})" : "")}; {ordered[0].Reference} … {ordered[^1].Reference}; md5 {fingerprint}");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    private async Task EnsureNoScoresAsync(JuryEvaluation e, string message, CancellationToken ct)
    {
        if (await db.JuryWorkScores.AnyAsync(s => s.EvaluationId == e.Id, ct)) throw new ConflictException(message);
    }

    /// <summary>A new juror for the evaluation (a new juror record in the competition's final) — only before scoring begins.</summary>
    public async Task<AdminJuryEvaluationDto> AddJurorAsync(int competitionId, string label, Guid adminId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: true, ct);
        await EnsureNoScoresAsync(e, "Qiymətləndirmə başlayıb — münsif tərkibi dəyişmir.", ct);
        var final = await db.CompetitionFinals.Include(f => f.Jurors).FirstAsync(f => f.CompetitionId == competitionId, ct);
        var name = string.Join(' ', (label ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > 60) throw ValidationFailedException.For("label", "Münsifin adı 1–60 simvol olmalıdır.");
        if (final.Jurors.Any(j => string.Equals(j.Label, name, StringComparison.CurrentCultureIgnoreCase)))
            throw ValidationFailedException.For("label", "Bu adda münsif artıq var.");
        var juror = new FinalJuror
        {
            Label = name, SortOrder = final.Jurors.Count == 0 ? 1 : final.Jurors.Max(j => j.SortOrder) + 1, CreatedAt = clock.GetUtcNow(),
        };
        final.Jurors.Add(juror);
        await SaveAsync(ct);
        e.Assignments.Add(new JuryAssignment { JurorId = juror.Id, CreatedAt = clock.GetUtcNow() });
        audit.Record(adminId, AuditActions.JuryJurorAssigned, AuditEntities.Competition, competitionId, $"{name} (#{juror.Id}) added");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Takes a juror off the evaluation (their juror record stays) — only before scoring begins.</summary>
    public async Task<AdminJuryEvaluationDto> RemoveJurorAsync(int competitionId, int jurorId, Guid adminId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: true, ct);
        await EnsureNoScoresAsync(e, "Qiymətləndirmə başlayıb — münsif tərkibi dəyişmir.", ct);
        var a = e.Assignments.FirstOrDefault(x => x.JurorId == jurorId) ?? throw new NotFoundException("Münsif tapılmadı.");
        if (e.Assignments.Count <= 1) throw new ConflictException("Ən azı bir münsif qalmalıdır.");
        await db.JurySessions.Where(s => s.AssignmentId == a.Id).ExecuteDeleteAsync(ct);
        db.JuryAssignments.Remove(a);
        audit.Record(adminId, AuditActions.JuryJurorUnassigned, AuditEntities.Competition, competitionId, $"{a.Juror.Label} (#{jurorId}) removed");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>The juror's official name — the existing record's own rename, audited there.</summary>
    public async Task<AdminJuryEvaluationDto> RenameJurorAsync(int competitionId, int jurorId, string label, Guid adminId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: false, ct);
        if (e.Assignments.All(a => a.JurorId != jurorId)) throw new NotFoundException("Münsif tapılmadı.");
        await finals.RenameJurorAsync(competitionId, jurorId, new RenameJurorRequest { Label = label }, adminId, ct);
        return await GetAsync(competitionId, ct);
    }

    private async Task<(JuryEvaluation E, JuryAssignment A)> AssignmentAsync(int competitionId, int jurorId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: true, ct);
        var a = e.Assignments.FirstOrDefault(x => x.JurorId == jurorId) ?? throw new NotFoundException("Münsif tapılmadı.");
        a.Evaluation = e;
        return (e, a);
    }

    public async Task<IssuedInvitationDto> IssueInvitationAsync(int competitionId, int jurorId, int days, Guid adminId, CancellationToken ct)
    {
        var (e, a) = await AssignmentAsync(competitionId, jurorId, ct);
        var (secret, code, expires) = await access.IssueAsync(a, days, adminId, ct);
        await SaveAsync(ct);
        return new IssuedInvitationDto(jurorId, a.Juror.Label, $"/jury/start#t={secret}", code, expires);
    }

    public async Task<AdminJuryEvaluationDto> RevokeInvitationAsync(int competitionId, int jurorId, Guid adminId, CancellationToken ct)
    {
        var (_, a) = await AssignmentAsync(competitionId, jurorId, ct);
        if (a.InviteTokenHash is null) throw new ConflictException("Bu münsifə dəvət verilməyib.");
        await access.RevokeAsync(a, adminId, ct);
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    // ---- The admin's view of the scores ------------------------------------------------------------------------------

    public async Task<JuryMatrixDto> MatrixAsync(int competitionId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: false, ct);
        var jurors = Jurors(e);
        var scores = (await ScoresAsync(e.Id, ct)).ToDictionary(s => (s.JurorId, s.SubmissionId));
        var titles = await TitlesAsync(e, ct);
        var complete = Complete(e, scores.Count);
        var (works, _) = await CatalogAsync(e, ct);
        var rows = works.Select(r =>
        {
            var mine = jurors.Select(j => scores.GetValueOrDefault((j.JurorId, r.SubmissionId))).ToList();
            decimal? average = complete ? Math.Round((decimal)mine.Sum(s => s!.Score) / mine.Count, 4) : null;
            return new JuryMatrixRowDto(r.SubmissionId, r.Reference, titles.GetValueOrDefault(r.SubmissionId),
                mine.Select(s => s?.Score).ToList(), mine.Select(s => s?.Source.ToString()).ToList(),
                mine.Select(s => !string.IsNullOrWhiteSpace(s?.Comment)).ToList(), average);
        }).ToList();
        return new JuryMatrixDto(jurors.Select(j => new JuryMatrixJurorDto(j.JurorId, j.Juror.Label, j.SubmittedAt is not null)).ToList(), rows, complete);
    }

    /// <summary>Every juror has submitted and every work has every juror's score.</summary>
    private static bool Complete(JuryEvaluation e, int scoreCount) =>
        e.Assignments.Count > 0 && e.Roster.Count > 0 && e.Assignments.All(a => a.SubmittedAt is not null)
        && scoreCount == e.Roster.Count * e.Assignments.Count;

    private async Task<Dictionary<int, string?>> TitlesAsync(JuryEvaluation e, CancellationToken ct)
    {
        var ids = e.Roster.Select(r => r.SubmissionId).ToList();
        return await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => (string?)s.Title, ct);
    }

    public async Task<string> ExportCsvAsync(int competitionId, CancellationToken ct)
    {
        var m = await MatrixAsync(competitionId, ct);
        var sb = new StringBuilder();
        static string Q(string? v) => $"\"{(v ?? "").Replace("\"", "\"\"")}\"";
        sb.AppendLine(string.Join(';', new[] { "CatalogNo", "Reference", "Title" }.Concat(m.Jurors.Select(j => j.Label)).Append("Average").Select(Q)));
        var i = 0;
        foreach (var r in m.Rows)
            sb.AppendLine(string.Join(';', new[] { (++i).ToString(CultureInfo.InvariantCulture), r.Reference, r.Title }
                .Concat(r.Scores.Select(s => s?.ToString(CultureInfo.InvariantCulture)))
                .Append(r.Average?.ToString("0.0000", CultureInfo.InvariantCulture)).Select(Q)));
        return sb.ToString();
    }

    // ---- The juror's side --------------------------------------------------------------------------------------------

    public static JurorSessionDto SessionOf(JuryAssignment a) =>
        new(a.Juror.Label, a.Evaluation.CompetitionId, a.Evaluation.Competition.Title, a.IdentityConfirmedAt is not null,
            a.SubmittedAt is not null, a.Evaluation.Status.ToString());

    public async Task<JurorSessionDto> ConfirmIdentityAsync(JuryAssignment signedIn, CancellationToken ct)
    {
        var a = await db.JuryAssignments.Include(x => x.Juror).Include(x => x.Evaluation).ThenInclude(e => e.Competition)
            .FirstAsync(x => x.Id == signedIn.Id, ct);
        if (a.IdentityConfirmedAt is null)
        {
            a.IdentityConfirmedAt = clock.GetUtcNow();
            audit.Record(null, AuditActions.JurorIdentityConfirmed, AuditEntities.Competition, a.Evaluation.CompetitionId,
                $"{a.Juror.Label} (#{a.JurorId}) confirmed the name shown");
            await SaveAsync(ct);
        }
        return SessionOf(a);
    }

    public async Task<JurorDashboardDto> DashboardAsync(JuryAssignment a, CancellationToken ct)
    {
        var e = await RequireAsync(a.Evaluation.CompetitionId, tracked: false, ct);
        var (roster, votes) = await CatalogAsync(e, ct);
        var mine = await db.JuryWorkScores.AsNoTracking().Where(s => s.EvaluationId == e.Id && s.JurorId == a.JurorId)
            .ToDictionaryAsync(s => s.SubmissionId, ct);
        var items = (await printItems.ForAsync([.. roster.Select(r => r.SubmissionId)], includeAuthor: false, ct)).ToDictionary(i => i.Id);
        var works = roster.Select(r =>
        {
            var item = items.GetValueOrDefault(r.SubmissionId);
            var s = mine.GetValueOrDefault(r.SubmissionId);
            return new JurorWorkItemDto(r.SubmissionId, r.Reference, item?.Title, item?.Image, votes[r.SubmissionId], s?.Score,
                !string.IsNullOrWhiteSpace(s?.Comment));
        }).ToList();
        return new JurorDashboardDto(a.Juror.Label, e.CompetitionId, e.Competition.Title, roster.Count, mine.Count, roster.Count - mine.Count,
            a.SubmittedAt is not null, a.SubmittedAt, e.Status == JuryEvaluationStatus.Open, works);
    }

    public async Task<JurorWorkDto> WorkAsync(JuryAssignment a, int submissionId, CancellationToken ct)
    {
        var e = await RequireAsync(a.Evaluation.CompetitionId, tracked: false, ct);
        var (roster, votes) = await CatalogAsync(e, ct);
        var at = roster.FindIndex(r => r.SubmissionId == submissionId);
        if (at < 0) throw new NotFoundException("Bu iş qiymətləndirmə siyahısında deyil.");
        var item = (await printItems.ForAsync([submissionId], includeAuthor: false, ct)).FirstOrDefault()
                   ?? throw new NotFoundException("Bu iş qiymətləndirmə siyahısında deyil.");
        var s = await db.JuryWorkScores.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EvaluationId == e.Id && x.JurorId == a.JurorId && x.SubmissionId == submissionId, ct);
        return new JurorWorkDto(submissionId, roster[at].Reference, item.Title, item.Category, item.Facts, item.ConceptLabel, item.Concept,
            item.Image, item.PdfPages, item.Material, votes[submissionId], at + 1, roster.Count,
            at > 0 ? roster[at - 1].SubmissionId : null, at < roster.Count - 1 ? roster[at + 1].SubmissionId : null,
            s?.Score, s?.Comment, s?.Version, a.SubmittedAt is not null || e.Status != JuryEvaluationStatus.Open);
    }

    /// <summary>
    /// The juror's own score for one work, saved at once. A save based on an older version than the one stored is
    /// refused (another device saved meanwhile) — never silently overwritten. The same score twice changes nothing.
    /// </summary>
    public async Task<JurorScoreDto> SaveScoreAsync(JuryAssignment signedIn, int submissionId, JurorScoreRequest req, CancellationToken ct)
    {
        if (!JuryEvaluation.IsValidScore(req.Score))
            throw ValidationFailedException.For("score", "Bal 1 ilə 10 arasında tam ədəd olmalıdır.");
        var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        var e = await RequireAsync(signedIn.Evaluation.CompetitionId, tracked: false, ct);
        if (e.Status != JuryEvaluationStatus.Open) throw new ConflictException("Qiymətləndirmə açıq deyil.");
        var a = e.Assignments.First(x => x.Id == signedIn.Id);
        if (a.SubmittedAt is not null) throw new ConflictException("Qiymətləndirməniz yekunlaşdırılıb — ballar kilidlidir.");
        var roster = e.Roster.FirstOrDefault(r => r.SubmissionId == submissionId)
                     ?? throw new NotFoundException("Bu iş qiymətləndirmə siyahısında deyil.");

        var row = await db.JuryWorkScores.FirstOrDefaultAsync(x => x.CompetitionId == e.CompetitionId && x.JurorId == a.JurorId && x.SubmissionId == submissionId, ct);
        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new JuryWorkScore
            {
                EvaluationId = e.Id, CompetitionId = e.CompetitionId, JurorId = a.JurorId, SubmissionId = submissionId, Score = req.Score,
                Comment = comment, Source = JuryScoreSource.Online, CreatedAt = now, UpdatedAt = now,
            };
            db.JuryWorkScores.Add(row);
            audit.Record(null, AuditActions.JuryWorkScored, AuditEntities.Competition, e.CompetitionId,
                $"{a.Juror.Label} (#{a.JurorId}) · {roster.Reference}: — → {req.Score} (online)");
        }
        else
        {
            var changes = row.Score != req.Score || row.Comment != comment;
            // Saving what is already stored changes nothing. Anything else must be based on the version stored now:
            // a save made before another device (or a paper sheet) saved is refused, never silently written over.
            if (changes && req.Version != row.Version)
                throw new ConflictException("Bu iş üçün bal başqa yerdən dəyişdirilib. Səhifəni yeniləyin.");
            if (changes)
            {
                audit.Record(null, AuditActions.JuryWorkScored, AuditEntities.Competition, e.CompetitionId,
                    $"{a.Juror.Label} (#{a.JurorId}) · {roster.Reference}: {row.Score} → {req.Score} (online)");
                row.Score = req.Score;
                row.Comment = comment;
                row.Source = JuryScoreSource.Online;
                row.UpdatedAt = now;
                row.UpdatedById = null;
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A second save of a first score raced this one: the database kept one; the juror reloads it.
            throw new ConflictException("Bu iş üçün bal artıq saxlanıb. Səhifəni yeniləyin.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Bu iş üçün bal başqa yerdən dəyişdirilib. Səhifəni yeniləyin.");
        }
        var scored = await db.JuryWorkScores.CountAsync(x => x.EvaluationId == e.Id && x.JurorId == a.JurorId, ct);
        return new JurorScoreDto(submissionId, row.Score, row.Comment, row.Version, scored, e.Roster.Count);
    }

    /// <summary>The juror finishes: allowed only when every work has their score; their scores lock.</summary>
    public async Task<JurorDashboardDto> SubmitAsync(JuryAssignment signedIn, CancellationToken ct)
    {
        var e = await RequireAsync(signedIn.Evaluation.CompetitionId, tracked: true, ct);
        if (e.Status != JuryEvaluationStatus.Open) throw new ConflictException("Qiymətləndirmə açıq deyil.");
        var a = e.Assignments.First(x => x.Id == signedIn.Id);
        if (a.SubmittedAt is null)
        {
            var scored = await db.JuryWorkScores.CountAsync(x => x.EvaluationId == e.Id && x.JurorId == a.JurorId, ct);
            if (scored < e.Roster.Count)
                throw new ConflictException($"{e.Roster.Count} işdən {scored}-u qiymətləndirilib. Qalan {e.Roster.Count - scored} işi də qiymətləndirin.");
            a.SubmittedAt = clock.GetUtcNow();
            a.SubmittedById = null;
            audit.Record(null, AuditActions.JuryEvaluationSubmitted, AuditEntities.Competition, e.CompetitionId,
                $"{a.Juror.Label} (#{a.JurorId}) submitted {scored} scores online");
            await SaveAsync(ct);
        }
        a.Evaluation = e;
        return await DashboardAsync(a, ct);
    }

    // ---- Paper sheets (entered by an admin on the juror's behalf) ----------------------------------------------------

    private sealed record PaperRow(JuryRosterEntry Work, int? Paper, JuryWorkScore? Existing, string Status);

    private async Task<(JuryEvaluation E, JuryAssignment A, List<PaperRow> Rows, List<string> Invalid)> ReadPaperAsync(
        int competitionId, PaperScoresRequest req, CancellationToken ct)
    {
        var (e, a) = await AssignmentAsync(competitionId, req.JurorId, ct);
        var roster = Roster(e);
        var byRef = roster.ToDictionary(r => r.Reference.ToUpperInvariant());
        var byId = roster.ToDictionary(r => r.SubmissionId);
        var existing = await db.JuryWorkScores.Where(s => s.EvaluationId == e.Id && s.JurorId == a.JurorId).ToDictionaryAsync(s => s.SubmissionId, ct);
        var paper = new Dictionary<int, int>();
        var invalid = new List<string>();
        foreach (var line in req.Lines)
        {
            JuryRosterEntry? work = null;
            if (line.SubmissionId is { } sid) byId.TryGetValue(sid, out work);
            else if (!string.IsNullOrWhiteSpace(line.Reference)) byRef.TryGetValue(line.Reference.Trim().ToUpperInvariant(), out work);
            var label = line.Reference ?? line.SubmissionId?.ToString(CultureInfo.InvariantCulture) ?? "?";
            if (work is null) { invalid.Add($"{label}: siyahıda belə iş yoxdur"); continue; }
            if (string.IsNullOrWhiteSpace(line.Score)) continue;
            if (!int.TryParse(line.Score.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var score) || !JuryEvaluation.IsValidScore(score))
            { invalid.Add($"{work.Reference}: \"{line.Score.Trim()}\" — 1–10 tam ədəd deyil"); continue; }
            if (paper.TryGetValue(work.SubmissionId, out var before) && before != score)
            { invalid.Add($"{work.Reference}: vərəqdə iki fərqli bal ({before}, {score})"); continue; }
            paper[work.SubmissionId] = score;
        }
        var rows = roster.Select(r =>
        {
            var p = paper.TryGetValue(r.SubmissionId, out var v) ? v : (int?)null;
            var x = existing.GetValueOrDefault(r.SubmissionId);
            var status = p is null ? (x is null ? "Missing" : "Existing") : x is null ? "New" : x.Score == p ? "Same" : "Conflict";
            return new PaperRow(r, p, x, status);
        }).ToList();
        return (e, a, rows, invalid);
    }

    public async Task<PaperPreviewDto> PreviewPaperAsync(int competitionId, PaperScoresRequest req, CancellationToken ct)
    {
        var (_, a, rows, invalid) = await ReadPaperAsync(competitionId, req, ct);
        return new PaperPreviewDto(a.JurorId, a.Juror.Label,
            rows.Select(r => new PaperPreviewRowDto(r.Work.SubmissionId, r.Work.Reference, r.Paper, r.Existing?.Score, r.Existing?.Source.ToString(), r.Status)).ToList(),
            rows.Count(r => r.Status == "New"), rows.Count(r => r.Status == "Same"), rows.Count(r => r.Status == "Conflict"),
            rows.Count(r => r.Status == "Missing"), invalid, a.SubmittedAt is not null);
    }

    /// <summary>
    /// Saves the paper sheet into the same score rows the juror uses online. New scores are written; equal ones change
    /// nothing; a different score already saved stays unless the admin explicitly resolves that conflict, with a reason.
    /// Anything invalid on the sheet saves nothing at all.
    /// </summary>
    public async Task<PaperCommitDto> CommitPaperAsync(int competitionId, PaperScoresRequest req, Guid adminId, CancellationToken ct)
    {
        var (e, a, rows, invalid) = await ReadPaperAsync(competitionId, req, ct);
        if (e.Status != JuryEvaluationStatus.Open) throw new ConflictException("Qiymətləndirmə açıq deyil.");
        if (a.SubmittedAt is not null) throw new ConflictException("Bu münsifin qiymətləndirməsi kilidlidir. Əvvəlcə səbəb göstərərək açın.");
        if (invalid.Count > 0) throw new ValidationFailedException(new Dictionary<string, string[]> { ["lines"] = [.. invalid] });
        var conflicts = rows.Where(r => r.Status == "Conflict").ToList();
        var choices = req.Resolutions.GroupBy(r => r.SubmissionId).ToDictionary(g => g.Key, g => g.Last());
        var unresolved = conflicts.Where(c => choices.GetValueOrDefault(c.Work.SubmissionId)?.Keep is not ("paper" or "existing")).ToList();
        if (unresolved.Count > 0)
            throw new ConflictException($"Kağız vərəq artıq saxlanmış fərqli ballarla ziddiyyət təşkil edir ({string.Join(", ", unresolved.Select(c => c.Work.Reference))}). Hər biri üçün hansının qalacağını seçin.");
        // Each choice was made against the score the admin saw; if it has changed since, nothing is saved.
        var moved = conflicts.Where(c => choices[c.Work.SubmissionId].Seen != c.Existing!.Score).ToList();
        if (moved.Count > 0)
            throw new ConflictException($"Önizləmədən sonra bu işlərin balı dəyişib ({string.Join(", ", moved.Select(c => c.Work.Reference))}). Önizləməni yeniləyin.");
        var resolutions = choices.ToDictionary(x => x.Key, x => x.Value.Keep);
        var reason = (req.Reason ?? "").Trim();
        if (conflicts.Count > 0 && reason.Length < 10)
            throw ValidationFailedException.For("reason", "Ziddiyyətlərin həlli üçün səbəbi yazın (ən azı 10 simvol).");

        var now = clock.GetUtcNow();
        var saved = 0;
        var resolved = 0;
        var changes = new List<string>();
        foreach (var r in rows.Where(r => r.Paper is not null))
        {
            if (r.Status == "New")
            {
                db.JuryWorkScores.Add(new JuryWorkScore
                {
                    EvaluationId = e.Id, CompetitionId = e.CompetitionId, JurorId = a.JurorId, SubmissionId = r.Work.SubmissionId,
                    Score = r.Paper!.Value, Source = JuryScoreSource.Paper, CreatedAt = now, UpdatedAt = now, UpdatedById = adminId,
                });
                saved++;
                changes.Add($"{r.Work.Reference} {r.Paper}");
            }
            else if (r.Status == "Conflict" && resolutions[r.Work.SubmissionId] == "paper")
            {
                var x = await db.JuryWorkScores.FirstAsync(s => s.Id == r.Existing!.Id, ct);
                changes.Add($"{r.Work.Reference} {x.Score}→{r.Paper} (conflict, paper kept)");
                x.Score = r.Paper!.Value;
                x.Source = JuryScoreSource.Paper;
                x.UpdatedAt = now;
                x.UpdatedById = adminId;
                saved++;
                resolved++;
            }
            else if (r.Status == "Conflict")
            {
                changes.Add($"{r.Work.Reference} {r.Existing!.Score} kept over paper {r.Paper}");
                resolved++;
            }
        }
        if (changes.Count > 0)
        {
            // Within the audit's 500 characters per entry: the decision, then the scores in chunks.
            audit.Record(adminId, AuditActions.JuryPaperScoresEntered, AuditEntities.Competition, e.CompetitionId,
                $"on behalf of {a.Juror.Label} (#{a.JurorId}): {saved} saved, {resolved} conflicts resolved{(reason.Length > 0 ? $"; reason: {reason}" : "")}");
            foreach (var chunk in Chunks(changes, 420))
                audit.Record(adminId, AuditActions.JuryPaperScoresEntered, AuditEntities.Competition, e.CompetitionId,
                    $"{a.Juror.Label} (#{a.JurorId}) paper: {chunk}");
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("Bu arada münsif onlayn bal saxlayıb. Önizləməni yeniləyin.");
        }
        var scored = await db.JuryWorkScores.CountAsync(x => x.EvaluationId == e.Id && x.JurorId == a.JurorId, ct);
        return new PaperCommitDto(saved, rows.Count(r => r.Status == "Same"), resolved, scored, rows.Count);
    }

    private static IEnumerable<string> Chunks(IEnumerable<string> parts, int max)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0 && sb.Length + p.Length + 2 > max) { yield return sb.ToString(); sb.Clear(); }
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(p);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>The admin completes a juror's evaluation for them (a paper sheet fully entered): every work must be scored.</summary>
    public async Task<AdminJuryEvaluationDto> LockOnBehalfAsync(int competitionId, int jurorId, Guid adminId, CancellationToken ct)
    {
        var (e, a) = await AssignmentAsync(competitionId, jurorId, ct);
        if (e.Status != JuryEvaluationStatus.Open) throw new ConflictException("Qiymətləndirmə açıq deyil.");
        if (a.SubmittedAt is not null) throw new ConflictException("Bu münsifin qiymətləndirməsi artıq kilidlidir.");
        var scored = await db.JuryWorkScores.CountAsync(x => x.EvaluationId == e.Id && x.JurorId == jurorId, ct);
        if (scored < e.Roster.Count)
            throw new ConflictException($"{e.Roster.Count} işdən {scored}-u qiymətləndirilib — kilidləmək olmaz.");
        a.SubmittedAt = clock.GetUtcNow();
        a.SubmittedById = adminId;
        audit.Record(adminId, AuditActions.JuryEvaluationSubmitted, AuditEntities.Competition, competitionId,
            $"{a.Juror.Label} (#{jurorId}) completed by an admin on the juror's behalf ({scored} scores)");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Unlocks one juror's scores for changes — never anyone else's — with a mandatory reason.</summary>
    public async Task<AdminJuryEvaluationDto> ReopenAsync(int competitionId, int jurorId, string reason, Guid adminId, CancellationToken ct)
    {
        var (e, a) = await AssignmentAsync(competitionId, jurorId, ct);
        if (a.SubmittedAt is null) throw new ConflictException("Bu münsifin qiymətləndirməsi kilidli deyil.");
        a.SubmittedAt = null;
        a.SubmittedById = null;
        audit.Record(adminId, AuditActions.JuryEvaluationReopened, AuditEntities.Competition, competitionId,
            $"{a.Juror.Label} (#{jurorId}) reopened; reason: {reason.Trim()}");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    // ---- The internal ranking ----------------------------------------------------------------------------------------

    private const string NotComplete =
        "Reytinq bütün münsiflər bütün işləri qiymətləndirib yekunlaşdırdıqdan sonra hazır olur.";

    /// <summary>
    /// Every work by the jury's exact average, 1 to N, once every juror has submitted every score — for the
    /// administration only: no public page or endpoint reads it. Works with the same average share their position and
    /// are marked as tied; nothing decides between them. Before that: the progress, and no position at all.
    /// </summary>
    public async Task<JuryRankingDto> RankingAsync(int competitionId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: false, ct);
        var jurorIds = e.Assignments.Select(a => a.JurorId).ToHashSet();
        var scores = (await ScoresAsync(e.Id, ct)).Where(s => jurorIds.Contains(s.JurorId)).ToList();
        var complete = Complete(e, scores.Count);
        var submitted = e.Assignments.Count(a => a.SubmittedAt is not null);
        var expected = e.Roster.Count * e.Assignments.Count;
        if (!complete)
            return new JuryRankingDto(e.Competition.Title, false, e.Roster.Count, e.Assignments.Count, submitted, expected,
                scores.Count, [], 0, clock.GetUtcNow());

        var titles = await TitlesAsync(e, ct);
        var sums = scores.GroupBy(s => s.SubmissionId).ToDictionary(g => g.Key, g => (Sum: g.Sum(s => (long)s.Score), Count: g.Count()));
        var rows = JuryRanking.Rank(Roster(e).Select(r =>
        {
            var (sum, count) = sums.GetValueOrDefault(r.SubmissionId);
            return new JuryRankingInput(r.SubmissionId, r.Reference, titles.GetValueOrDefault(r.SubmissionId), sum, count);
        }));
        return new JuryRankingDto(e.Competition.Title, true, e.Roster.Count, e.Assignments.Count, submitted, expected, scores.Count,
            rows.Select(x => new JuryRankingRowDto(x.Position, x.Work.SubmissionId, x.Work.Reference, x.Work.Title,
                Math.Round(x.Work.Average, 4), x.Work.ScoreSum, x.Work.ScoreCount, x.TieSize > 1, x.TieSize)).ToList(),
            rows.Where(x => x.TieSize > 1).Select(x => x.Position).Distinct().Count(), clock.GetUtcNow());
    }

    /// <summary>The ranking as CSV: position, reference, title, average (4 places), score sum, scores, tie size.</summary>
    public async Task<string> RankingCsvAsync(int competitionId, CancellationToken ct)
    {
        var r = await RankingAsync(competitionId, ct);
        if (!r.Complete) throw new ConflictException(NotComplete);
        var sb = new StringBuilder();
        static string Q(string? v) => $"\"{(v ?? "").Replace("\"", "\"\"")}\"";
        sb.AppendLine(string.Join(';', new[] { "Position", "Reference", "Title", "Average", "ScoreSum", "Scores", "TiedWorks" }.Select(Q)));
        foreach (var x in r.Rows)
            sb.AppendLine(string.Join(';', new[]
            {
                x.Position.ToString(CultureInfo.InvariantCulture), x.Reference, x.Title,
                x.Average.ToString("0.0000", CultureInfo.InvariantCulture), x.ScoreSum.ToString(CultureInfo.InvariantCulture),
                x.ScoreCount.ToString(CultureInfo.InvariantCulture), x.Tied ? x.TieSize.ToString(CultureInfo.InvariantCulture) : "",
            }.Select(Q)));
        return sb.ToString();
    }

    // ---- Print --------------------------------------------------------------------------------------------------

    public async Task<JuryPrintDto> PrintAsync(int competitionId, CancellationToken ct)
    {
        var e = await RequireAsync(competitionId, tracked: false, ct);
        if (e.Roster.Count == 0) throw new ConflictException("İşlərin siyahısı hələ dondurulmayıb.");
        var (roster, votes) = await CatalogAsync(e, ct);
        var items = (await printItems.ForAsync([.. roster.Select(r => r.SubmissionId)], includeAuthor: false, ct)).ToDictionary(i => i.Id);
        return new JuryPrintDto(e.Competition.Title,
            roster.Select(r =>
            {
                var item = items.GetValueOrDefault(r.SubmissionId);
                return new JuryPrintWorkDto(r.SubmissionId, r.Reference, item?.Title ?? "", item?.Image, item?.PdfPages ?? 0,
                    item?.Material ?? [], votes[r.SubmissionId]);
            }).ToList(),
            Jurors(e).Select(j => new JuryMatrixJurorDto(j.JurorId, j.Juror.Label, j.SubmittedAt is not null)).ToList());
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(Concurrent);
        }
    }
}
