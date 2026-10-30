using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meydan.Api.Common;
using Meydan.Api.Features.Admin;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Features.Final;

/// <summary>
/// The jury final (D13), step by step: freeze the ten finalists from the valid vote, enter the jury's marks, lock
/// them, settle a tie at a prize by the jury's recorded decision, show the result, and — only on the admin's explicit
/// word — write and publish the official places. Every step is checked here and audited.
/// <para>
/// What it never does: change a vote, change the voting window or a fraud rule, or write the official result before
/// the publish step. The public vote, the jury's marks and the official result stay three separate things.
/// </para>
/// </summary>
public sealed class FinalAdministration(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, ResultAdministration results, PrintItems printItems,
    Voting.DocumentPages pages)
{
    private const string Concurrent = "Final eyni anda başqa yerdən dəyişdirilib. Səhifəni yeniləyin.";
    private const string NoFinal = "Bu müsabiqənin finalçıları hələ təsdiqlənməyib.";

    /// <summary>The prizes the owner set for "Əmircanın simvolu", offered when the finalists are frozen.</summary>
    public static readonly int[] DefaultPrizes = [250, 150, 100];

    private static readonly (string Name, Func<JuryScore, decimal?> Get)[] Criteria =
    [
        ("İdeya", s => s.Idea),
        ("Vizual / icra", s => s.Execution),
        ("Mövzuya uyğunluq", s => s.Relevance),
    ];

    // ---- Reading ------------------------------------------------------------------------------------------

    public async Task<AdminFinalDto> GetAsync(int competitionId, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: false, ct);
        var votes = await VoteStateAsync(competitionId, ct);
        if (final is null)
        {
            return new AdminFinalDto(competitionId, competition.Title, null, await PreviewAsync(competition, votes, ct),
                null, null, null, null, null, false, DefaultPrizes, [], [], 0, false, null, null, null, null, null);
        }

        var works = await WorksAsync(final, ct);
        var names = await UserNamesAsync(
            [final.FrozenById, final.JuryLockedById, final.PublishedById, .. final.TieBreaks.Select(t => t.DecidedById)], ct);
        var jurors = final.Jurors.OrderBy(j => j.SortOrder).ToList();
        var marked = final.Finalists.SelectMany(f => f.Scores).Where(HasAnyMark).Select(s => s.JurorId).ToHashSet();

        var finalists = final.Finalists
            .OrderBy(f => works[f.SubmissionId].Reference, StringComparer.Ordinal)
            .Select(f =>
            {
                var marks = jurors.Select(j =>
                {
                    var s = f.Scores.FirstOrDefault(x => x.JurorId == j.Id);
                    return new JuryMarkDto(j.Id, s?.Idea, s?.Execution, s?.Relevance, s?.Total ?? 0, s?.IsComplete == true);
                }).ToList();
                var w = works[f.SubmissionId];
                return new JuryFinalistDto(f.SubmissionId, w.Reference, w.Title, w.Image, marks,
                    marks.Sum(m => m.Subtotal), marks.All(m => m.Complete), w.Author);
            })
            .ToList();
        var missing = final.Finalists.Sum(f => jurors.Sum(j => MissingMarks(f.Scores.FirstOrDefault(s => s.JurorId == j.Id))));

        return new AdminFinalDto(
            competitionId, competition.Title, final.Stage, null,
            final.FrozenAt, Name(names, final.FrozenById), final.FrozenValidVotes, final.FrozenInvalidatedVotes,
            final.FrozenVotesFingerprint, votes.Fingerprint != final.FrozenVotesFingerprint,
            [final.FirstPrizeAzn, final.SecondPrizeAzn, final.ThirdPrizeAzn],
            jurors.Select(j => new FinalJurorDto(j.Id, j.Label, marked.Contains(j.Id))).ToList(),
            finalists, missing, final.Stage == FinalStage.Frozen && marked.Count == 0,
            final.JuryLockedAt, Name(names, final.JuryLockedById),
            final.Stage == FinalStage.Frozen ? null : await CalculationAsync(final, works, names, ct),
            final.PublishedAt, Name(names, final.PublishedById));
    }

    /// <summary>
    /// What the show puts on the screen: every finalist with every number, for the presenter to reveal step by step.
    /// Only once the jury is locked. Reading it changes nothing, so the show can be run again and again.
    /// </summary>
    public async Task<FinalShowDto> ShowAsync(int competitionId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var competition = await CompetitionAsync(competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: false, ct) ?? throw new NotFoundException(NoFinal);
        if (final.Stage == FinalStage.Frozen)
            throw new ConflictException("Final şousu münsif qiymətləndirməsi kilidləndikdən sonra açılır.");
        var works = await WorksAsync(final, ct);
        var table = Table(final, works);
        var shown = table.Rows
            .Select(r =>
            {
                var w = works[r.SubmissionId];
                return new FinalShowWorkDto(r.SubmissionId, r.Reference, w.Title, w.Image, r.ValidVotes, r.PublicRank,
                    r.PublicPoints, r.JuryTotal, r.JuryRank, r.JuryPoints, r.FinalScore, r.Rank, r.Place, r.Tie,
                    PrizeOf(final, r.Place));
            })
            .OrderBy(w => w.Reference, StringComparer.Ordinal)
            .ToList();
        return new FinalShowDto(competition.Title, final.Jurors.Count, JuryMax(final), shown, table.PrizesDecided,
            [final.FirstPrizeAzn, final.SecondPrizeAzn, final.ThirdPrizeAzn]);
    }

    // ---- The public vote on paper -------------------------------------------------------------------------

    /// <summary>
    /// The public vote's result as the final froze it, for print: the ten finalists from the snapshot — never
    /// recalculated — with their valid votes, public rank and points, and the vote totals the snapshot was taken
    /// from. No voter appears; the author only when asked for (an internal document). Read-only.
    /// </summary>
    public async Task<LeaderboardPrintDto> Top10PrintAsync(int competitionId, bool includeAuthor, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: false, ct) ?? throw new ConflictException(NoFinal);
        var works = await WorksAsync(final, ct);
        var ordered = final.Finalists
            .OrderBy(f => f.PublicRank).ThenBy(f => works[f.SubmissionId].Reference, StringComparer.Ordinal)
            .ToList();
        var items = (await printItems.ForAsync([.. ordered.Select(f => f.SubmissionId)], includeAuthor, ct)).ToDictionary(i => i.Id);
        var rows = ordered
            .Select(f => new LeaderboardPrintRowDto(f.PublicRank, f.ValidVotes, f.PublicPoints, items[f.SubmissionId]))
            .ToList();
        return new LeaderboardPrintDto(LeaderboardPrintMode.Top10, competition.Title, final.FrozenAt,
            final.FrozenValidVotes, final.FrozenInvalidatedVotes, final.FrozenValidVotes + final.FrozenInvalidatedVotes,
            final.FrozenVotesFingerprint, rows, clock.GetUtcNow());
    }

    /// <summary>
    /// Every work on the ballot by valid votes, in competition ranking — the audited public ranking of the closed vote,
    /// read as it stands. Only once the window has closed; set-aside votes count nowhere. Not a list of winners. The
    /// author only when asked for (an internal document).
    /// </summary>
    public async Task<LeaderboardPrintDto> FullPrintAsync(int competitionId, bool includeAuthor, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        if (competition.VotingStateAt(clock.GetUtcNow()) != VotingState.Closed)
            throw new ConflictException("Tam səs sıralaması səsvermə bağlandıqdan sonra çap olunur.");
        var votes = await VoteStateAsync(competitionId, ct);
        var ranked = await RankedAsync(competitionId, ct);
        var items = (await printItems.ForAsync([.. ranked.Select(c => c.SubmissionId)], includeAuthor, ct)).ToDictionary(i => i.Id);
        var rows = ranked
            .Select(c => new LeaderboardPrintRowDto(c.Rank, c.ValidVotes, null, items[c.SubmissionId]))
            .ToList();
        return new LeaderboardPrintDto(LeaderboardPrintMode.Full, competition.Title, null, votes.Valid, votes.Invalidated,
            votes.Valid + votes.Invalidated, votes.Fingerprint, rows, clock.GetUtcNow());
    }

    // ---- Freezing the ten ---------------------------------------------------------------------------------

    /// <summary>
    /// Freezes the ten works with the most valid votes, with their votes, public ranks and points, and the vote state
    /// they were read from. Only after the window has closed, before any result is public, and on the admin's
    /// confirmation that the integrity audit is complete. Again only while no mark has been entered.
    /// </summary>
    public async Task<AdminFinalDto> FreezeAsync(int competitionId, FreezeFinalRequest req, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        if (!req.AuditCompleted)
            throw ValidationFailedException.For("auditCompleted", "Final auditin tamamlandığını və təsdiqləndiyini qeyd edin.");
        if (req.Prizes is not { Length: 3 } prizes || prizes.Any(p => p is < 0 or > 1_000_000))
            throw ValidationFailedException.For("prizes", "Üç yerin mükafatını manatla, mənfi olmayan tam ədəd kimi göstərin.");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var competition = await CompetitionAsync(competitionId, ct);
            var votes = await VoteStateAsync(competitionId, ct);
            var preview = await PreviewAsync(competition, votes, ct);
            if (preview.Problems.Count > 0)
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["final"] = [.. preview.Problems] });

            var final = await LoadAsync(competitionId, tracked: true, ct);
            var refrozen = final is not null;
            var now = clock.GetUtcNow();
            if (final is not null)
            {
                if (final.Stage != FinalStage.Frozen || final.Finalists.SelectMany(f => f.Scores).Any(HasAnyMark))
                    throw new ConflictException("Münsif balları artıq daxil edilib — finalçılar yenidən təsdiqlənə bilməz.");
                var why = (req.SupersedeReason ?? "").Trim();
                if (why.Length is < 10 or > 300)
                    throw ValidationFailedException.For("supersedeReason",
                        "Əvvəlki Top 10-un niyə əvəz olunduğunu yazın (10–300 simvol).");
                // The old ten never vanish silently: the audit keeps them in full — when, by which vote state, every
                // finalist with its votes, rank and points — together with why they were superseded.
                var oldIds = final.Finalists.Select(x => x.SubmissionId).ToList();
                var old = await db.Submissions.AsNoTracking()
                    .Where(s => oldIds.Contains(s.Id))
                    .ToDictionaryAsync(s => s.Id, s => s.ReferenceNumber ?? $"#{s.Id}", ct);
                // Two entries, each within the audit's 500 characters: the snapshot's state and the reason, then its ten.
                var superseded = new[]
                {
                    $"superseded snapshot frozen {final.FrozenAt:O}; valid {final.FrozenValidVotes}, invalidated {final.FrozenInvalidatedVotes}, " +
                    $"votes md5 {final.FrozenVotesFingerprint}; reason: {why}",
                    "superseded ten: " + string.Join(", ", final.Finalists
                        .OrderBy(x => x.PublicRank).ThenBy(x => old.GetValueOrDefault(x.SubmissionId), StringComparer.Ordinal)
                        .Select(x => $"{x.PublicRank} {old.GetValueOrDefault(x.SubmissionId)} {x.ValidVotes} ({x.PublicPoints} pts)")),
                };
                // The old ten go first, in their own statement (their empty score rows with them): the new ten may
                // include the same works.
                var finalId = final.Id;
                await db.CompetitionFinalists.Where(x => x.FinalId == finalId).ExecuteDeleteAsync(ct);
                db.ChangeTracker.Clear();
                final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new ConflictException(Concurrent);
                // Recorded after the tracker is cleared, in the same transaction as the new ten.
                foreach (var line in superseded)
                    audit.Record(adminId, AuditActions.FinalistsSuperseded, AuditEntities.Competition, competitionId, line);
            }
            else
            {
                final = new CompetitionFinal { CompetitionId = competitionId };
                final.Jurors.Add(new FinalJuror { Label = "Münsif 1", SortOrder = 1, CreatedAt = now });
                final.Jurors.Add(new FinalJuror { Label = "Münsif 2", SortOrder = 2, CreatedAt = now });
                db.CompetitionFinals.Add(final);
            }

            final.Stage = FinalStage.Frozen;
            final.FrozenAt = now;
            final.FrozenById = adminId;
            final.FrozenValidVotes = votes.Valid;
            final.FrozenInvalidatedVotes = votes.Invalidated;
            final.FrozenVotesFingerprint = votes.Fingerprint;
            (final.FirstPrizeAzn, final.SecondPrizeAzn, final.ThirdPrizeAzn) = (prizes[0], prizes[1], prizes[2]);
            foreach (var c in preview.Top)
            {
                final.Finalists.Add(new CompetitionFinalist
                {
                    CompetitionId = competitionId, SubmissionId = c.SubmissionId, ValidVotes = c.ValidVotes,
                    PublicRank = c.Rank, PublicPoints = FinalScoring.PointsFor(c.Rank), SnapshotAt = now,
                });
            }
            audit.Record(adminId, AuditActions.FinalistsFrozen, AuditEntities.Competition, competitionId,
                $"{(refrozen ? "re-frozen" : "frozen")}; valid {votes.Valid}, invalidated {votes.Invalidated}, "
                + $"votes md5 {votes.Fingerprint}; prizes {prizes[0]}/{prizes[1]}/{prizes[2]} AZN; "
                + string.Join(", ", preview.Top.Select(c => $"{c.Rank} {c.Reference} {c.ValidVotes}")));
            await SaveAsync(ct);
            await tx.CommitAsync(ct);
        });
        return await GetAsync(competitionId, ct);
    }

    // ---- The jury -----------------------------------------------------------------------------------------

    public async Task<AdminFinalDto> AddJurorAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        EnsureOpen(final);
        if (final.Jurors.Count >= 12) throw new ConflictException("Ən çox 12 münsif ola bilər.");
        var next = final.Jurors.Count == 0 ? 1 : final.Jurors.Max(j => j.SortOrder) + 1;
        var juror = new FinalJuror { Label = $"Münsif {next}", SortOrder = next, CreatedAt = clock.GetUtcNow() };
        final.Jurors.Add(juror);
        audit.Record(adminId, AuditActions.FinalJurorAdded, AuditEntities.Competition, competitionId, juror.Label);
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// A juror's name on the admin's screens and printed sheets — "Münsif 2" by default, the juror's own name if the admin
    /// gives it. One name per juror in the final; the marks are untouched. Possible until the result is published (a
    /// name changes no number); the public pages count the jurors and never name them. Audited.
    /// </summary>
    public async Task<AdminFinalDto> RenameJurorAsync(int competitionId, int jurorId, RenameJurorRequest req, Guid adminId, CancellationToken ct)
    {
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        if (final.Stage == FinalStage.Published)
            throw new ConflictException("Rəsmi nəticə bu finaldan dərc olunub — dəyişiklik mümkün deyil.");
        var juror = final.Jurors.FirstOrDefault(j => j.Id == jurorId) ?? throw new NotFoundException("Münsif tapılmadı.");
        var label = string.Join(' ', (req.Label ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (label.Length is 0 or > 60)
            throw ValidationFailedException.For("label", "Münsifin adı 1–60 simvol olmalıdır.");
        if (final.Jurors.Any(j => j.Id != jurorId && string.Equals(j.Label, label, StringComparison.CurrentCultureIgnoreCase)))
            throw ValidationFailedException.For("label", "Bu adda münsif artıq var.");
        if (juror.Label != label)
        {
            audit.Record(adminId, AuditActions.FinalJurorRenamed, AuditEntities.Competition, competitionId, $"{juror.Label} → {label}");
            juror.Label = label;
            await SaveAsync(ct);
        }
        return await GetAsync(competitionId, ct);
    }

    /// <summary>A juror goes only while no mark of theirs exists: marks are never thrown away by removing their author.</summary>
    public async Task<AdminFinalDto> RemoveJurorAsync(int competitionId, int jurorId, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        EnsureOpen(final);
        var juror = final.Jurors.FirstOrDefault(j => j.Id == jurorId) ?? throw new NotFoundException("Münsif tapılmadı.");
        if (final.Finalists.SelectMany(f => f.Scores).Any(s => s.JurorId == jurorId && HasAnyMark(s)))
            throw new ConflictException("Bu münsifin balları var — münsif silinə bilməz.");
        if (final.Jurors.Count <= 1) throw new ConflictException("Ən azı bir münsif qalmalıdır.");
        foreach (var f in final.Finalists)
            db.JuryScores.RemoveRange(f.Scores.Where(s => s.JurorId == jurorId));
        db.FinalJurors.Remove(juror);
        audit.Record(adminId, AuditActions.FinalJurorRemoved, AuditEntities.Competition, competitionId, juror.Label);
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// One juror's sheet, as typed in from paper. A mark may be left empty for now; every given mark is 0–10 in
    /// steps of 0.5. Each work whose marks change is audited with the old and new marks.
    /// </summary>
    public async Task<AdminFinalDto> SaveMarksAsync(int competitionId, JuryMarksRequest req, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        EnsureOpen(final);
        var juror = final.Jurors.FirstOrDefault(j => j.Id == req.JurorId)
                    ?? throw ValidationFailedException.For("jurorId", "Münsif tapılmadı.");

        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < req.Marks.Length; i++)
        {
            var m = req.Marks[i];
            if (final.Finalists.All(f => f.SubmissionId != m.SubmissionId))
                errors[$"marks[{i}].submissionId"] = ["Bu iş finalçı deyil."];
            foreach (var (field, value) in new[] { ("idea", m.Idea), ("execution", m.Execution), ("relevance", m.Relevance) })
                if (value is { } v && !JuryScore.IsValidMark(v))
                    errors[$"marks[{i}].{field}"] = ["Bal 0 ilə 10 arasında, 0,5 addımla olmalıdır."];
        }
        if (req.Marks.Select(m => m.SubmissionId).Distinct().Count() != req.Marks.Length)
            errors["marks"] = ["Bir iş üçün bal bir dəfə göstərilir."];
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var references = await ReferencesAsync(final, ct);
        var now = clock.GetUtcNow();
        foreach (var m in req.Marks)
        {
            var finalist = final.Finalists.First(f => f.SubmissionId == m.SubmissionId);
            var score = finalist.Scores.FirstOrDefault(s => s.JurorId == juror.Id);
            var after = new JuryScore { Idea = m.Idea, Execution = m.Execution, Relevance = m.Relevance };
            var changes = Criteria
                .Where(c => c.Get(after) != (score is null ? null : c.Get(score)))
                .Select(c => $"{c.Name} {Mark(score is null ? null : c.Get(score))}→{Mark(c.Get(after))}")
                .ToList();
            if (changes.Count == 0) continue;
            if (score is null)
            {
                score = new JuryScore { JurorId = juror.Id };
                finalist.Scores.Add(score);
            }
            (score.Idea, score.Execution, score.Relevance) = (m.Idea, m.Execution, m.Relevance);
            score.UpdatedAt = now;
            score.UpdatedById = adminId;
            audit.Record(adminId, AuditActions.JuryScoresChanged, AuditEntities.Competition, competitionId,
                $"{juror.Label} · {references[m.SubmissionId]}: {string.Join(", ", changes)}");
        }
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Locks the marks once every juror has marked every finalist on every criterion.</summary>
    public async Task<AdminFinalDto> LockAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        EnsureOpen(final);
        var missing = final.Finalists.Sum(f => final.Jurors.Sum(j => MissingMarks(f.Scores.FirstOrDefault(s => s.JurorId == j.Id))));
        if (missing > 0)
            throw ValidationFailedException.For("jury", $"{missing} bal hələ daxil edilməyib — bütün münsiflərin bütün balları lazımdır.");

        final.Stage = FinalStage.JuryLocked;
        final.JuryLockedAt = clock.GetUtcNow();
        final.JuryLockedById = adminId;
        var references = await ReferencesAsync(final, ct);
        audit.Record(adminId, AuditActions.JuryLocked, AuditEntities.Competition, competitionId,
            $"jurors {final.Jurors.Count}; " + string.Join(", ", final.Finalists
                .OrderBy(f => references[f.SubmissionId], StringComparer.Ordinal)
                .Select(f => $"{references[f.SubmissionId]} {Mark(FinalScoring.JuryTotal(f.Scores))}")));
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Opens the marks again — only with a reason, and never once the official result came from them.</summary>
    public async Task<AdminFinalDto> UnlockAsync(int competitionId, UnlockJuryRequest req, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        if (final.Stage == FinalStage.Published)
            throw new ConflictException("Rəsmi nəticə bu finaldan dərc olunub — qiymətləndirmə yenidən açıla bilməz.");
        if (final.Stage != FinalStage.JuryLocked) throw new ConflictException("Qiymətləndirmə kilidli deyil.");
        final.Stage = FinalStage.Frozen;
        final.JuryLockedAt = null;
        final.JuryLockedById = null;
        audit.Record(adminId, AuditActions.JuryUnlocked, AuditEntities.Competition, competitionId, $"reason: {req.Reason.Trim()}");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// Records the jury's order for works level on every rule where it touches a prize. The admin names exactly the
    /// level works, best first, and the jury's reason; it replaces an earlier decision for the same works.
    /// </summary>
    public async Task<AdminFinalDto> RecordTieBreakAsync(int competitionId, TieBreakRequest req, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
        if (final.Stage != FinalStage.JuryLocked)
            throw new ConflictException("Münsif qərarı qiymətləndirmə kilidləndikdən sonra, dərcdən əvvəl qeyd olunur.");
        var ids = req.SubmissionIds;
        if (ids.Length < 2 || ids.Distinct().Count() != ids.Length)
            throw ValidationFailedException.For("submissionIds", "Bərabər işlərin hər birini bir dəfə, sıra ilə göstərin.");

        var works = await WorksAsync(final, ct);
        var table = Table(final, works);
        var group = table.Rows
            .Where(r => r.Tie is FinalTie.NeedsJuryDecision or FinalTie.DecidedByJury)
            .GroupBy(r => r.Rank)
            .Select(g => g.Select(r => r.SubmissionId).ToHashSet())
            .FirstOrDefault(g => g.SetEquals(ids))
            ?? throw new ConflictException("Bu işlər arasında münsif qərarı tələb olunan bərabərlik yoxdur.");

        db.FinalTieBreaks.RemoveRange(final.TieBreaks.Where(t => group.SetEquals(t.SubmissionIds)));
        final.TieBreaks.Add(new FinalTieBreak
        {
            SubmissionIds = ids, Reason = req.Reason.Trim(), DecidedAt = clock.GetUtcNow(), DecidedById = adminId,
        });
        audit.Record(adminId, AuditActions.FinalTieBreakRecorded, AuditEntities.Competition, competitionId,
            $"{string.Join(" > ", ids.Select(id => works[id].Reference))}; reason: {req.Reason.Trim()}");
        await SaveAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    // ---- Publishing ---------------------------------------------------------------------------------------

    /// <summary>
    /// The one step that makes the final official: the first, second and third place are written to the competition's
    /// result and published, through the result's own rules, in one transaction; and the numbers each finalist was
    /// announced with are stored, so the published result is never recalculated from later votes.
    /// </summary>
    public async Task<AdminFinalDto> PublishAsync(int competitionId, PublishFinalRequest req, Guid adminId, CancellationToken ct)
    {
        await Jury.JuryFormatGuard.EnsureCombinedAllowedAsync(db, competitionId, ct);
        if (!req.Confirm)
            throw ValidationFailedException.For("confirm", "Rəsmi nəticələrin dərcini təsdiqləyin.");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var final = await LoadAsync(competitionId, tracked: true, ct) ?? throw new NotFoundException(NoFinal);
            var works = await WorksAsync(final, ct);
            var table = Table(final, works);
            var problems = await PublishProblemsAsync(final, table, works, ct);
            if (problems.Count > 0)
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["final"] = [.. problems] });

            var places = Enumerable.Range(1, CompetitionResult.PlaceCount).Select(p => table.AtPlace(p)?.SubmissionId).ToArray();
            var current = await db.CompetitionResults.AsNoTracking().Where(r => r.CompetitionId == competitionId)
                .Select(r => new[] { r.OverallWinnerSubmissionId, r.SecondPlaceSubmissionId, r.ThirdPlaceSubmissionId })
                .FirstOrDefaultAsync(ct) ?? [null, null, null];
            await results.SetPlacesAsync(competitionId, new SetPlacesRequest { Places = places, Current = current }, adminId, ct);
            await results.PublishAsync(competitionId, adminId, ct);

            var now = clock.GetUtcNow();
            foreach (var row in table.Rows)
            {
                var f = final.Finalists.First(x => x.SubmissionId == row.SubmissionId);
                (f.JuryTotal, f.JuryRank, f.JuryPoints, f.FinalScore, f.FinalPlace) =
                    (row.JuryTotal, row.JuryRank, row.JuryPoints, row.FinalScore, row.Place);
            }
            final.Stage = FinalStage.Published;
            final.PublishedAt = now;
            final.PublishedById = adminId;
            audit.Record(adminId, AuditActions.FinalPublished, AuditEntities.Competition, competitionId,
                "places: " + string.Join(", ", Enumerable.Range(1, CompetitionResult.PlaceCount)
                    .Select(p => table.AtPlace(p)).OfType<FinalRow>()
                    .Select(r => $"{r.Place} {r.Reference} {r.FinalScore} ({r.PublicPoints}+{r.JuryPoints})"))
                + $"; prizes {final.FirstPrizeAzn}/{final.SecondPrizeAzn}/{final.ThirdPrizeAzn} AZN");
            await SaveAsync(ct);
            await tx.CommitAsync(ct);
        });
        return await GetAsync(competitionId, ct);
    }

    // ---- The parts ----------------------------------------------------------------------------------------

    /// <param name="Author">For the admin's jury rows only — the show and the public pages never carry it.</param>
    private sealed record Work(string Reference, string? Title, AdminPrintImageDto? Image, string Author);

    private sealed record VoteState(int Valid, int Invalidated, string Fingerprint);

    /// <summary>
    /// The competition's votes, all of them, as (id | work | valid) in id order, md5'd — the fingerprint the operator's
    /// read-only audit queries compute in SQL (string_agg of concat_ws('|', id, work, valid) ordered by id), so the two
    /// can be compared character for character.
    /// </summary>
    private async Task<VoteState> VoteStateAsync(int competitionId, CancellationToken ct)
    {
        var votes = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Submission.CompetitionId == competitionId)
            .OrderBy(v => v.Id)
            .Select(v => new { v.Id, v.SubmissionId, Valid = v.InvalidatedAt == null })
            .ToListAsync(ct);
        var text = string.Join(';', votes.Select(v =>
            string.Create(CultureInfo.InvariantCulture, $"{v.Id}|{v.SubmissionId}|{(v.Valid ? 't' : 'f')}")));
        var fingerprint = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var valid = votes.Count(v => v.Valid);
        return new VoteState(valid, votes.Count - valid, fingerprint);
    }

    /// <summary>
    /// The ranking the ten are taken from: the works on the ballot — the same rule as the public ranking
    /// (PublicVoting.Voteable: final status, a reference, an active nomination) — by valid votes, competition ranking.
    /// </summary>
    private async Task<FinalFreezePreviewDto> PreviewAsync(Competition competition, VoteState votes, CancellationToken ct)
    {
        var ranked = await RankedAsync(competition.Id, ct);
        var top = ranked.Take(CompetitionFinal.FinalistCount).ToList();
        var next = ranked.Count > top.Count ? ranked[top.Count] : null;
        var boundaryTie = next is not null && top.Count > 0 && next.ValidVotes == top[^1].ValidVotes;
        var now = clock.GetUtcNow();
        var closed = competition.VotingStateAt(now) == VotingState.Closed;
        var published = await db.CompetitionResults.AnyAsync(r => r.CompetitionId == competition.Id && r.PublishedAt != null, ct);
        return Preview(competition, votes, top, next, boundaryTie, closed, published);
    }

    /// <summary>
    /// Every work on the ballot — the same rule as the public ranking (PublicVoting.Voteable: final status, a reference,
    /// an active nomination) — by valid votes, best first, in competition ranking (1, 2, 2, 4).
    /// </summary>
    private async Task<List<FinalCandidateDto>> RankedAsync(int competitionId, CancellationToken ct)
    {
        var works = await db.Submissions.AsNoTracking()
            .Where(s => s.CompetitionId == competitionId
                        && CompetitionResult.EligibleStatuses.Contains(s.Status)
                        && s.ReferenceNumber != null
                        && s.CompetitionCategoryId != null
                        && s.Category!.IsActive)
            .Select(s => new { s.Id, Reference = s.ReferenceNumber!, s.Title })
            .ToListAsync(ct);
        var counts = (await db.Votes.AsNoTracking()
                .Where(v => v.Category.CompetitionId == competitionId)
                .GroupBy(v => v.SubmissionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var ordered = works
            .Select(w => (Work: w, Votes: counts.GetValueOrDefault(w.Id)))
            .OrderByDescending(x => x.Votes).ThenBy(x => x.Work.Id)
            .ToList();
        var ranks = FinalScoring.CompetitionRanks(ordered, (a, b) => a.Votes == b.Votes);
        return ordered
            .Select((x, i) => new FinalCandidateDto(x.Work.Id, x.Work.Reference, x.Work.Title, x.Votes, ranks[i]))
            .ToList();
    }

    private static FinalFreezePreviewDto Preview(
        Competition competition, VoteState votes, List<FinalCandidateDto> top, FinalCandidateDto? next, bool boundaryTie,
        bool closed, bool published)
    {
        var problems = new List<string>();
        if (competition.HasCategories) problems.Add("Final yalnız kateqoriyasız müsabiqə üçündür (1–3-cü yerlər).");
        if (!closed) problems.Add("Səsvermə hələ bağlanmayıb.");
        if (published) problems.Add("Rəsmi nəticə artıq dərc olunub.");
        if (top.Count == 0) problems.Add("Səsvermədə iş yoxdur.");
        if (boundaryTie)
            problems.Add($"{CompetitionFinal.FinalistCount}-cu və növbəti iş bərabər səsdədir ({next!.ValidVotes}) — "
                         + "on finalçı birmənalı deyil, sahibkarın qərarı lazımdır.");
        return new FinalFreezePreviewDto(closed, published, votes.Valid, votes.Invalidated, votes.Fingerprint,
            top, next, boundaryTie, problems);
    }

    private async Task<FinalCalculationDto> CalculationAsync(
        CompetitionFinal final, Dictionary<int, Work> works, Dictionary<Guid, string> names, CancellationToken ct)
    {
        var table = Table(final, works);
        var rows = table.Rows.Select(r => new FinalRowDto(r.SubmissionId, r.Reference, works[r.SubmissionId].Title,
            r.ValidVotes, r.PublicRank, r.PublicPoints, r.JuryTotal, r.JuryRank, r.JuryPoints, r.FinalScore, r.Rank,
            r.Place, r.Tie, PrizeOf(final, r.Place))).ToList();
        var ties = table.UnresolvedTies
            .Select(ids => new FinalTieGroupDto(
                table.Rows.First(r => r.SubmissionId == ids[0]).Rank,
                ids.Select(id => new FinalTieMemberDto(id, works[id].Reference, works[id].Title)).ToList()))
            .ToList();
        var decisions = final.TieBreaks.OrderBy(t => t.DecidedAt)
            .Select(t => new FinalTieBreakDto(
                t.SubmissionIds.Select(id => works.TryGetValue(id, out var w) ? w.Reference : id.ToString(CultureInfo.InvariantCulture)).ToList(),
                t.Reason, t.DecidedAt, Name(names, t.DecidedById)))
            .ToList();
        var problems = final.Stage == FinalStage.Published ? [] : await PublishProblemsAsync(final, table, works, ct);
        return new FinalCalculationDto(final.Jurors.Count, JuryMax(final), rows, ties, decisions, problems);
    }

    private async Task<List<string>> PublishProblemsAsync(
        CompetitionFinal final, FinalTable table, Dictionary<int, Work> works, CancellationToken ct)
    {
        var problems = new List<string>();
        if (final.Stage == FinalStage.Published) problems.Add("Final artıq dərc olunub.");
        else if (final.Stage != FinalStage.JuryLocked) problems.Add("Münsif qiymətləndirməsi kilidlənməyib.");
        foreach (var ids in table.UnresolvedTies)
            problems.Add("Münsif qərarı tələb olunur: " + string.Join(", ", ids.Select(id => works[id].Reference)) + ".");
        var placed = Math.Min(CompetitionResult.PlaceCount, table.Rows.Count);
        if (table.UnresolvedTies.Count == 0)
            for (var p = 1; p <= placed; p++)
                if (table.AtPlace(p) is null) problems.Add($"{p}-ci yer müəyyən deyil.");
        if (await db.CompetitionResults.AnyAsync(r => r.CompetitionId == final.CompetitionId && r.PublishedAt != null, ct))
            problems.Add("Müsabiqənin nəticəsi artıq dərc olunub — əvvəlcə onu dərcdən çıxarın.");
        return problems;
    }

    private static FinalTable Table(CompetitionFinal final, IReadOnlyDictionary<int, Work> works) =>
        FinalScoring.Compute(
            final.Finalists.Select(f => new FinalistInput(f.SubmissionId, works[f.SubmissionId].Reference, f.ValidVotes,
                f.PublicRank, f.PublicPoints, FinalScoring.JuryTotal(f.Scores))).ToList(),
            final.TieBreaks.OrderByDescending(t => t.DecidedAt).Select(t => (IReadOnlyList<int>)t.SubmissionIds).ToList());

    private async Task<CompetitionFinal?> LoadAsync(int competitionId, bool tracked, CancellationToken ct)
    {
        var query = db.CompetitionFinals
            .Include(f => f.Finalists).ThenInclude(x => x.Scores)
            .Include(f => f.Jurors)
            .Include(f => f.TieBreaks)
            .AsSplitQuery();
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(f => f.CompetitionId == competitionId, ct);
    }

    private async Task<Dictionary<int, Work>> WorksAsync(CompetitionFinal final, CancellationToken ct)
    {
        var ids = final.Finalists.Select(f => f.SubmissionId).ToList();
        var works = db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id));
        var images = await SubmissionImages.ForAsync(works, pages, ct);
        return await works.Select(s => new { s.Id, s.ReferenceNumber, s.Title, Author = s.User.FirstName + " " + s.User.LastName })
            .ToDictionaryAsync(s => s.Id, s => new Work(s.ReferenceNumber ?? $"#{s.Id}", s.Title, images.GetValueOrDefault(s.Id),
                s.Author.Trim()), ct);
    }

    private async Task<Dictionary<int, string>> ReferencesAsync(CompetitionFinal final, CancellationToken ct)
    {
        var ids = final.Finalists.Select(f => f.SubmissionId).ToList();
        return await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ReferenceNumber ?? $"#{s.Id}", ct);
    }

    private async Task<Competition> CompetitionAsync(int competitionId, CancellationToken ct) =>
        await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
        ?? throw new NotFoundException("Müsabiqə tapılmadı.");

    private static void EnsureOpen(CompetitionFinal final)
    {
        if (final.Stage == FinalStage.Published)
            throw new ConflictException("Rəsmi nəticə bu finaldan dərc olunub — dəyişiklik mümkün deyil.");
        if (final.Stage == FinalStage.JuryLocked)
            throw new ConflictException("Münsif qiymətləndirməsi kilidlənib. Dəyişmək üçün əvvəlcə kilidi səbəb göstərərək açın.");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(Concurrent);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(Concurrent);
        }
    }

    private static bool HasAnyMark(JuryScore s) => s.Idea is not null || s.Execution is not null || s.Relevance is not null;

    private static int MissingMarks(JuryScore? s) =>
        s is null ? CompetitionFinal.CriteriaCount : Criteria.Count(c => c.Get(s) is null);

    private static decimal JuryMax(CompetitionFinal final) =>
        final.Jurors.Count * CompetitionFinal.CriteriaCount * CompetitionFinal.CriterionMax;

    private static int? PrizeOf(CompetitionFinal final, int? place) =>
        place is >= 1 and <= CompetitionResult.PlaceCount ? final.PrizeOf(place.Value) : null;

    private static string Mark(decimal? mark) =>
        mark is { } m ? m.ToString("0.#", CultureInfo.InvariantCulture) : "—";

    private async Task<Dictionary<Guid, string>> UserNamesAsync(Guid?[] ids, CancellationToken ct)
    {
        var wanted = ids.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}", ct);
    }

    private static string? Name(Dictionary<Guid, string> names, Guid? id) =>
        id is { } key && names.TryGetValue(key, out var n) ? n : null;
}
