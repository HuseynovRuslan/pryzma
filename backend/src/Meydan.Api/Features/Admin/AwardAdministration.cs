using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Voting;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>One place of a podium as the admin sees it: the work, the name it is announced with, and its number.</summary>
/// <param name="AccountName">The participant's account name, beside the announced name, so the admin can compare.</param>
/// <param name="ValidVotes">The work's valid votes now — for the public podium, what its number must be.</param>
public sealed record AdminAwardPlaceDto(
    int Place, int SubmissionId, string Reference, string Title, string ParticipantName, string AccountName, int Points,
    SubmissionStatus Status, AdminPreviewDto? Preview, int ValidVotes);

/// <param name="Source">Where the numbers come from (never public).</param>
public sealed record AdminAwardPodiumDto(
    AwardCategory Category, IReadOnlyList<AdminAwardPlaceDto> Places, string? Source, DateTimeOffset? RecordedAt, string? RecordedBy);

/// <param name="Candidates">Every work that can stand on a podium, most valid votes first (works level on votes share a rank).</param>
public sealed record AdminAwardsDto(
    int CompetitionId, bool ResultPublished, VotingState Voting, AdminAwardPodiumDto Jury, AdminAwardPodiumDto Public,
    IReadOnlyList<AdminPlaceCandidateDto> Candidates);

public sealed record AwardPlaceInput
{
    [Range(1, int.MaxValue, ErrorMessage = "İşi seçin.")]
    public int SubmissionId { get; init; }

    public string ParticipantName { get; init; } = "";

    public int Points { get; init; }
}

/// <summary>
/// All three places of one podium at once, in order. <c>Current</c> is what the admin's screen showed (the work on each
/// place, null for none), so a stale screen is refused rather than overwriting a newer podium.
/// </summary>
public sealed record SetAwardsRequest
{
    [Required(ErrorMessage = "Yerləri göstərin.")]
    public AwardPlaceInput[] Places { get; init; } = [];

    [Required(ErrorMessage = "Hazırkı yerləri göstərin.")]
    public int?[] Current { get; init; } = [];

    /// <summary>Required for the jury's podium: where its points come from ("Kağız protokol, …").</summary>
    public string? Source { get; init; }

    public bool ConfirmPublished { get; init; }
}

/// <summary>
/// The two independent podiums of a competition's result (owner, 16 October 2026): the jury's choice, with the points
/// of the jury's own protocol, and the public's choice, with the works' valid votes. Each is saved whole — three places
/// — and every change is audited. Nothing here is public until the result is published by the existing publication,
/// and nothing here combines the two, ranks one by the other or picks an overall winner.
/// <para>
/// The numbers are never accepted silently: the public's choice must be exactly the top three of the valid vote, each
/// with its count as the server counts it; the jury's points are recorded with their source, strictly descending.
/// </para>
/// </summary>
public sealed class AwardAdministration(MeydanDbContext db, AuditTrail audit, TimeProvider clock, ResultAdministration results)
{
    public const string PublicSource = "Etibarlı səslər (audit sonrası)";

    private const string Stale = "Nəticə artıq dəyişdirilib. Səhifəni yeniləyin.";

    public static string Name(AwardCategory category) => category == AwardCategory.Jury ? "Münsiflərin seçimi" : "Xalqın seçimi";

    public async Task<AdminAwardsDto> GetAsync(int competitionId, CancellationToken ct)
    {
        var c = await CompetitionAsync(competitionId, ct);
        var rows = await db.CompetitionAwards.AsNoTracking().Where(a => a.CompetitionId == competitionId).ToListAsync(ct);
        var cards = await results.WinnerCardsAsync([.. rows.Select(a => a.SubmissionId).Distinct()], ct);
        var valid = await ValidVotesAsync([.. rows.Select(a => a.SubmissionId).Distinct()], ct);
        var recorderIds = rows.Where(a => a.RecordedById != null).Select(a => a.RecordedById!.Value).Distinct().ToList();
        var recorders = await db.Users.AsNoTracking()
            .Where(u => recorderIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}", ct);
        var published = await db.CompetitionResults.AnyAsync(r => r.CompetitionId == competitionId && r.PublishedAt != null, ct);

        AdminAwardPodiumDto Podium(AwardCategory category)
        {
            var mine = rows.Where(a => a.Category == category).OrderBy(a => a.Place).ToList();
            var places = mine.Select(a =>
            {
                var w = cards[a.SubmissionId];
                return new AdminAwardPlaceDto(a.Place, a.SubmissionId, w.Reference, w.Title, a.ParticipantName, w.ParticipantName,
                    a.Points, w.Status, w.Preview, valid.GetValueOrDefault(a.SubmissionId));
            }).ToList();
            var last = mine.OrderByDescending(a => a.RecordedAt).FirstOrDefault();
            return new AdminAwardPodiumDto(category, places, last?.Source, last?.RecordedAt,
                last?.RecordedById is { } by && recorders.TryGetValue(by, out var n) ? n : null);
        }

        return new AdminAwardsDto(competitionId, published, c.VotingStateAt(clock.GetUtcNow()),
            Podium(AwardCategory.Jury), Podium(AwardCategory.Public), await results.PlaceCandidatesAsync(c, ct));
    }

    /// <summary>Sets one podium — all three places — after checking every number; audited.</summary>
    public async Task<AdminAwardsDto> SetAsync(int competitionId, AwardCategory category, SetAwardsRequest req, Guid adminId, CancellationToken ct)
    {
        var c = await CompetitionAsync(competitionId, ct);
        if (req.Places.Length != CompetitionAward.PlaceCount || req.Current.Length != CompetitionAward.PlaceCount)
            throw ValidationFailedException.For("places", "Üç yerin hər birini göstərin.");

        var result = await db.CompetitionResults.AsNoTracking().FirstOrDefaultAsync(r => r.CompetitionId == competitionId, ct);
        if (result?.IsPublished == true && !req.ConfirmPublished)
            throw new ConflictException(ResultAdministration.ConfirmNeeded);
        await EnsureCurrentAsync(competitionId, category, req.Current, ct);

        var places = req.Places.Select((p, i) => (Place: i + 1, p.SubmissionId, Name: Clean(p.ParticipantName), p.Points)).ToList();
        if (places.Select(p => p.SubmissionId).Distinct().Count() != places.Count)
            throw ValidationFailedException.For("places", "Bir iş podiumda yalnız bir yer tuta bilər.");
        foreach (var p in places)
        {
            if (p.Name.Length is < 2 or > CompetitionAward.NameMax)
                throw ValidationFailedException.For("places", $"{Ordinal(p.Place)} yer: iştirakçının adını yazın (2–{CompetitionAward.NameMax} simvol).");
            if (p.Points is < 0 or > 1_000_000)
                throw ValidationFailedException.For("places", $"{Ordinal(p.Place)} yer: {Noun(category)} 0 və ya müsbət tam ədəd olmalıdır.");
        }
        for (var i = 1; i < places.Count; i++)
            if (places[i].Points >= places[i - 1].Points)
                throw ValidationFailedException.For("places",
                    $"{Ordinal(places[i].Place)} yerin {Possessive(category)} {Ordinal(places[i - 1].Place)} yerinkindən az olmalıdır — bu podiumda bərabər yer yoxdur.");

        var works = await db.Submissions.AsNoTracking()
            .Where(s => places.Select(p => p.SubmissionId).Contains(s.Id))
            .Select(s => new { s.Id, s.CompetitionId, s.Status, s.ReferenceNumber })
            .ToDictionaryAsync(s => s.Id, ct);
        foreach (var p in places)
        {
            if (!works.TryGetValue(p.SubmissionId, out var w) || w.CompetitionId != competitionId)
                throw ValidationFailedException.For("places", $"{Ordinal(p.Place)} yer: bu iş bu müsabiqəyə aid deyil.");
            if (w.ReferenceNumber is null || ResultAdministration.IneligibleReason(w.Status) is not null)
                throw ValidationFailedException.For("places",
                    $"{Ordinal(p.Place)} yer: {w.ReferenceNumber ?? "bu iş"} — {ResultAdministration.IneligibleReason(w.Status) ?? "təqdim edilməmiş iş"}");
        }

        string source;
        if (category == AwardCategory.Public)
        {
            await EnsurePublicVoteAsync(c, places.Select(p => (p.Place, p.SubmissionId, p.Points, works[p.SubmissionId].ReferenceNumber!)).ToList(), ct);
            source = PublicSource;
        }
        else
        {
            source = Clean(req.Source);
            if (source.Length is < 3 or > CompetitionAward.SourceMax)
                throw ValidationFailedException.For("source",
                    $"Münsif ballarının mənbəyini yazın (məsələn, “Kağız protokol”, 3–{CompetitionAward.SourceMax} simvol).");
        }

        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The podium the screen saw, checked again inside the transaction; then replaced whole.
            await EnsureCurrentAsync(competitionId, category, req.Current, ct);
            await db.CompetitionAwards.Where(a => a.CompetitionId == competitionId && a.Category == category).ExecuteDeleteAsync(ct);
            foreach (var p in places)
                db.CompetitionAwards.Add(new CompetitionAward
                {
                    CompetitionId = competitionId, Category = category, Place = p.Place, SubmissionId = p.SubmissionId,
                    ParticipantName = p.Name, Points = p.Points, Source = source, RecordedById = adminId, RecordedAt = now,
                });
            audit.Record(adminId, AuditActions.AwardsSet, AuditEntities.Competition, competitionId,
                $"{Name(category)}: " + string.Join("; ", places.Select(p => $"{p.Place} {works[p.SubmissionId].ReferenceNumber} {Short(p.Name)} {p.Points}"))
                + $"; mənbə: {source}{(result?.IsPublished == true ? " (published result)" : "")}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Takes one podium off the result — only while the result is private; audited.</summary>
    public async Task<AdminAwardsDto> ClearAsync(int competitionId, AwardCategory category, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        if (await db.CompetitionResults.AnyAsync(r => r.CompetitionId == competitionId && r.PublishedAt != null, ct))
            throw new ConflictException("Dərc olunmuş nəticədən podium silinmir — əvvəlcə nəticəni dərcdən çıxarın.");
        var gone = await db.CompetitionAwards.Where(a => a.CompetitionId == competitionId && a.Category == category).ToListAsync(ct);
        if (gone.Count > 0)
        {
            db.CompetitionAwards.RemoveRange(gone);
            audit.Record(adminId, AuditActions.AwardsCleared, AuditEntities.Competition, competitionId,
                $"{Name(category)}: " + string.Join("; ", gone.OrderBy(a => a.Place).Select(a => $"{a.Place} submission {a.SubmissionId} {a.Points}")));
            await db.SaveChangesAsync(ct);
        }
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// The public's choice is the valid vote's own top three, each with the count the server counts — not a number an
    /// admin types. Only once the vote is over, and never across a tie the vote did not decide.
    /// </summary>
    private async Task EnsurePublicVoteAsync(Competition c, List<(int Place, int SubmissionId, int Points, string Reference)> places, CancellationToken ct)
    {
        if (c.VotingStateAt(clock.GetUtcNow()) != VotingState.Closed)
            throw new ConflictException("Xalqın seçimi səsvermə başa çatdıqdan sonra qeyd olunur.");
        var ballot = await PublicVoting.BallotWorks(db, c.Id).Select(s => s.Id).ToListAsync(ct);
        var valid = await ValidVotesAsync(ballot, ct);
        foreach (var p in places)
        {
            if (!ballot.Contains(p.SubmissionId))
                throw ValidationFailedException.For("places", $"{Ordinal(p.Place)} yer: {p.Reference} səsvermədə iştirak etməyib.");
            var count = valid.GetValueOrDefault(p.SubmissionId);
            if (count != p.Points)
                throw ValidationFailedException.For("places",
                    $"{Ordinal(p.Place)} yer: {p.Reference} — {count} etibarlı səs, siz {p.Points} yazmısınız. Səs sayı serverin hesabından götürülür.");
            var above = ballot.Count(id => valid.GetValueOrDefault(id) > count);
            var level = ballot.Count(id => id != p.SubmissionId && valid.GetValueOrDefault(id) == count);
            if (above + 1 != p.Place || level > 0)
                throw ValidationFailedException.For("places",
                    $"{Ordinal(p.Place)} yer: {p.Reference} etibarlı səslərə görə {(level > 0 ? $"{above + 1}-ci yeri {level + 1} işlə bölüşür" : $"{above + 1}-ci yerdədir")} — xalqın seçimi səs sıralamasının özüdür.");
        }
    }

    private async Task EnsureCurrentAsync(int competitionId, AwardCategory category, int?[] current, CancellationToken ct)
    {
        var stored = await db.CompetitionAwards.AsNoTracking()
            .Where(a => a.CompetitionId == competitionId && a.Category == category)
            .ToDictionaryAsync(a => a.Place, a => a.SubmissionId, ct);
        for (var place = 1; place <= CompetitionAward.PlaceCount; place++)
            if ((stored.TryGetValue(place, out var held) ? held : (int?)null) != current[place - 1])
                throw new ConflictException(Stale);
    }

    /// <summary>Valid votes per work (the query filter leaves set-aside votes out).</summary>
    private Task<Dictionary<int, int>> ValidVotesAsync(IReadOnlyCollection<int> submissionIds, CancellationToken ct) =>
        db.Votes.AsNoTracking()
            .Where(v => submissionIds.Contains(v.SubmissionId))
            .GroupBy(v => v.SubmissionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task<Competition> CompetitionAsync(int competitionId, CancellationToken ct) =>
        await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
        ?? throw new NotFoundException("Müsabiqə tapılmadı.");

    private static string Clean(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Short(string name) => name.Length > 40 ? name[..40] : name;

    private static string Noun(AwardCategory category) => category == AwardCategory.Jury ? "bal" : "səs sayı";

    private static string Possessive(AwardCategory category) => category == AwardCategory.Jury ? "balı" : "səs sayı";

    private static string Ordinal(int place) => place == 3 ? "3-cü" : $"{place}-ci";
}
