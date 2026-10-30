using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <param name="FinalWorks">Entries with a reference and a status that can win — what the ballot is made of.</param>
/// <param name="OnBallot">Of those, the ones placed in an active nomination: what a voter sees today.</param>
/// <param name="WithoutPicture">Final entries with no file whose public-safe copy is ready: a card with a placeholder.</param>
/// <param name="CreatesNomination">
/// The competition has no active nomination, so starting the vote makes the one it needs ("Bütün işlər") and places
/// every final entry in it — the ballot of a competition that never asked its entrants to choose one.
/// </param>
/// <param name="Problems">Why the vote cannot be started here; empty when it can.</param>
/// <param name="Warnings">What the admin should see first, without standing in the way.</param>
public sealed record VotingLaunchCompetitionDto(
    int Id, string Title, string Slug, string ProjectName, VotingState VotingState,
    DateTimeOffset? VotingStartsAt, DateTimeOffset? VotingEndsAt,
    int FinalWorks, int OnBallot, int WithoutPicture, bool CreatesNomination,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

public sealed record VotingLaunchDto(DateTimeOffset ServerNow, IReadOnlyList<VotingLaunchCompetitionDto> Competitions);

public sealed record VotingLaunchRequest
{
    [Required(ErrorMessage = "Səsverməni başladılacaq müsabiqəni seçin.")]
    [MinLength(1, ErrorMessage = "Səsverməni başladılacaq müsabiqəni seçin.")]
    public int[] CompetitionIds { get; init; } = [];

    [Required(ErrorMessage = "Səsvermənin bitmə tarixini və saatını yazın.")]
    public DateTimeOffset? EndsAt { get; init; }
}

public sealed record VotingLaunchedDto(int Id, string Title, string Slug, int OnBallot, bool NominationCreated);

public sealed record VotingLaunchResultDto(DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<VotingLaunchedDto> Competitions);

/// <summary>
/// Starting the public vote from one screen, for every competition that is ready, in one step: each opens now and
/// closes at the same chosen moment. Each keeps its own ballot at its own address
/// (<c>/competitions/{slug}/voting</c>); the public side announces whatever is open, so nothing else is switched on.
/// <para>
/// The window itself is the same pair of dates the competition's settings edit — this only fills both at once and
/// makes sure the ballot is not empty when it opens: a competition with no active nomination gets one, and its
/// final entries are placed in it.
/// </para>
/// </summary>
public sealed class VotingLaunch(MeydanDbContext db, AuditTrail audit, TimeProvider clock)
{
    public const string NominationName = "Bütün işlər";

    /// <summary>The longest window one press may open; anything longer is a typo in the year.</summary>
    public static readonly TimeSpan LongestWindow = TimeSpan.FromDays(90);

    private sealed record Reading(Competition Competition, int FinalWorks, int OnBallot, int WithoutPicture, int ActiveNominations);

    public async Task<VotingLaunchDto> ReadAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var readings = await ReadAllAsync(null, ct);
        return new VotingLaunchDto(now, readings.Select(r => ToDto(r, now)).ToList());
    }

    public async Task<VotingLaunchResultDto> LaunchAsync(VotingLaunchRequest req, Guid adminId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var endsAt = req.EndsAt!.Value;
        if (endsAt <= now.AddMinutes(10))
            throw ValidationFailedException.For("endsAt", "Bitmə vaxtı indidən ən azı 10 dəqiqə sonra olmalıdır.");
        if (endsAt > now + LongestWindow)
            throw ValidationFailedException.For("endsAt", "Səsvermə ən çox 90 gün davam edə bilər. Tarixi yoxlayın.");

        var ids = req.CompetitionIds.Distinct().ToList();
        var readings = await ReadAllAsync(ids, ct);
        var missing = ids.Except(readings.Select(r => r.Competition.Id)).ToList();
        if (missing.Count > 0) throw new NotFoundException("Seçilən müsabiqələrdən biri səsverməyə hazır deyil və ya tapılmadı.");
        var refused = readings
            .Select(r => (r.Competition.Title, Problems: ProblemsOf(r, now)))
            .Where(x => x.Problems.Count > 0)
            .Select(x => $"{x.Title}: {string.Join(" ", x.Problems)}")
            .ToList();
        if (refused.Count > 0) throw new UnprocessableException(string.Join(" ", refused));

        var launched = new List<VotingLaunchedDto>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            launched.Clear();
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var r in readings)
            {
                var c = await db.Competitions.SingleAsync(x => x.Id == r.Competition.Id, ct);
                var created = false;
                if (r.ActiveNominations == 0)
                {
                    var category = new CompetitionCategory
                    {
                        CompetitionId = c.Id,
                        Name = NominationName,
                        Slug = await FreeSlugAsync(c.Id, ct),
                        SortOrder = 0,
                        IsActive = true,
                    };
                    db.CompetitionCategories.Add(category);
                    await db.SaveChangesAsync(ct);
                    // A set-based update, as the seed places its nominations: the nomination is part of the key
                    // a vote points at, so it is written, not edited through a tracked entry.
                    await db.Submissions
                        .Where(s => s.CompetitionId == c.Id && s.CompetitionCategoryId == null
                                    && CompetitionResult.EligibleStatuses.Contains(s.Status) && s.ReferenceNumber != null)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.CompetitionCategoryId, category.Id), ct);
                    created = true;
                }

                var was = c.VotingStartsAt is null || c.VotingEndsAt is null
                    ? "yoxdur" : $"{c.VotingStartsAt:yyyy-MM-dd HH:mm}–{c.VotingEndsAt:yyyy-MM-dd HH:mm}";
                c.VotingStartsAt = now;
                c.VotingEndsAt = endsAt;
                c.UpdatedAt = now;
                var onBallot = await PublicVoting.BallotWorks(db, c.Id).CountAsync(ct);
                audit.Record(adminId, AuditActions.VotingWindowChanged, AuditEntities.Competition, c.Id,
                    $"{was} → {now:yyyy-MM-dd HH:mm}–{endsAt:yyyy-MM-dd HH:mm}");
                audit.Record(adminId, AuditActions.VotingLaunched, AuditEntities.Competition, c.Id,
                    $"{now:yyyy-MM-dd HH:mm}–{endsAt:yyyy-MM-dd HH:mm}; səsvermədə {onBallot} iş"
                    + (created ? $"; “{NominationName}” nominasiyası yaradıldı" : ""));
                launched.Add(new VotingLaunchedDto(c.Id, c.Title, c.Slug, onBallot, created));
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return new VotingLaunchResultDto(now, endsAt, launched);
    }

    /// <summary>
    /// Every competition the screen can speak about: public, not a demo, without a vote that has already been held.
    /// A vote that is running is listed too, so the screen can say so rather than look as if it had vanished.
    /// </summary>
    private async Task<List<Reading>> ReadAllAsync(IReadOnlyCollection<int>? only, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = db.Competitions.AsNoTracking().Include(c => c.Project)
            .Where(c => !c.IsDemo && (c.Status == CompetitionStatus.Published || c.Status == CompetitionStatus.Closed));
        if (only is not null) query = query.Where(c => only.Contains(c.Id));
        var competitions = (await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct))
            .Where(c => c.VotingStateAt(now) != VotingState.Closed)
            .ToList();
        var ids = competitions.Select(c => c.Id).ToList();

        var final = await db.Submissions.AsNoTracking()
            .Where(s => ids.Contains(s.CompetitionId) && CompetitionResult.EligibleStatuses.Contains(s.Status) && s.ReferenceNumber != null)
            .GroupBy(s => s.CompetitionId)
            .Select(g => new
            {
                CompetitionId = g.Key,
                Count = g.Count(),
                WithoutPicture = g.Count(s => !s.Files.Any(f => !f.HiddenFromPublic && f.PublicAssetStatus == PublicAssetStatus.Ready)),
            })
            .ToDictionaryAsync(x => x.CompetitionId, ct);
        var nominations = await db.CompetitionCategories.AsNoTracking()
            .Where(k => ids.Contains(k.CompetitionId) && k.IsActive)
            .GroupBy(k => k.CompetitionId)
            .Select(g => new { CompetitionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompetitionId, x => x.Count, ct);

        var readings = new List<Reading>();
        foreach (var c in competitions)
        {
            var onBallot = await PublicVoting.BallotWorks(db, c.Id).CountAsync(ct);
            var f = final.GetValueOrDefault(c.Id);
            readings.Add(new Reading(c, f?.Count ?? 0, onBallot, f?.WithoutPicture ?? 0, nominations.GetValueOrDefault(c.Id)));
        }
        return readings;
    }

    private static VotingLaunchCompetitionDto ToDto(Reading r, DateTimeOffset now)
    {
        var c = r.Competition;
        var warnings = new List<string>();
        if (r.WithoutPicture > 0)
            warnings.Add($"{r.WithoutPicture} işin saytda göstəriləcək şəkli hazır deyil — kartında yer tutucu görünəcək.");
        if (r.ActiveNominations > 0 && r.FinalWorks > r.OnBallot)
            warnings.Add($"{r.FinalWorks - r.OnBallot} iş heç bir aktiv nominasiyada deyil — səsvermədə görünməyəcək.");
        if (c.VotingStateAt(now) == VotingState.Upcoming)
            warnings.Add($"Planlaşdırılmış səsvermə ({c.VotingStartsAt:dd.MM.yyyy HH:mm}) bu düymə ilə indi başlayacaq.");
        return new VotingLaunchCompetitionDto(
            c.Id, c.Title, c.Slug, c.Project.Name, c.VotingStateAt(now), c.VotingStartsAt, c.VotingEndsAt,
            r.FinalWorks, r.OnBallot, r.WithoutPicture, r.ActiveNominations == 0 && r.FinalWorks > 0,
            ProblemsOf(r, now), warnings);
    }

    private static List<string> ProblemsOf(Reading r, DateTimeOffset now)
    {
        var c = r.Competition;
        var problems = new List<string>();
        if (c.VotingStateAt(now) == VotingState.Open) problems.Add("Səsvermə artıq gedir.");
        if (c.Project.Status != ProjectStatus.Published) problems.Add("Layihə saytda dərc olunmayıb.");
        if (c.IsAcceptingSubmissions(now)) problems.Add("İşlərin qəbulu hələ açıqdır — əvvəlcə qəbulu bağlayın.");
        if (r.FinalWorks == 0) problems.Add("Səsverməyə çıxacaq göndərilmiş iş yoxdur.");
        else if (r.ActiveNominations > 0 && r.OnBallot == 0) problems.Add("Heç bir iş aktiv nominasiyada deyil.");
        return problems;
    }

    private async Task<string> FreeSlugAsync(int competitionId, CancellationToken ct)
    {
        var taken = await db.CompetitionCategories.Where(k => k.CompetitionId == competitionId).Select(k => k.Slug).ToListAsync(ct);
        var root = Slug.From(NominationName);
        var slug = root;
        for (var n = 2; taken.Contains(slug); n++) slug = $"{root}-{n}";
        return slug;
    }
}

/// <summary>The launch screen: what is ready to be voted on, and the one button that opens it.</summary>
[Route("api/admin/voting/launch")]
public sealed class AdminVotingLaunchController(VotingLaunch launch) : AdminControllerBase
{
    [HttpGet]
    public Task<VotingLaunchDto> Get(CancellationToken ct) => launch.ReadAsync(ct);

    [HttpPost]
    public Task<VotingLaunchResultDto> Launch(VotingLaunchRequest req, CancellationToken ct) =>
        launch.LaunchAsync(req, CurrentUserId, ct);
}
