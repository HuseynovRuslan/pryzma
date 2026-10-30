using System.ComponentModel.DataAnnotations;
using Meydan.Api.Common;
using Meydan.Api.Features.Catalog;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meydan.Api.Features.Admin;

public enum ResultState
{
    /// <summary>No winner chosen yet.</summary>
    NoWinner,
    /// <summary>At least one winner chosen; the result is private.</summary>
    Prepared,
    /// <summary>Published by an admin.</summary>
    Published,
}

/// <summary>
/// The image an admin list shows for a work: which file, whether its small copy is ready, and — for a PDF — the page
/// that stands for it (SubmissionImages.RepresentativePage; a page after the first comes from /files/{id}/pages/{page}).
/// </summary>
public sealed record AdminPreviewDto(int FileId, bool ThumbnailReady, int Page = 1);

public sealed record AdminWinnerDto(
    int SubmissionId, string Reference, string Title, string ParticipantName, SubmissionStatus Status,
    AdminPreviewDto? Preview, DateTimeOffset SelectedAt, string? SelectedBy);

/// <summary>
/// One place a winner can be chosen for: a category (a settlement) — or, in a competition without categories, the
/// competition as a whole (Category null).
/// </summary>
public sealed record AdminWinnerSlotDto(RefDto? Category, bool CategoryActive, int EligibleCount, AdminWinnerDto? Winner);

public sealed record AdminResultDto(
    int CompetitionId,
    string CompetitionTitle,
    string CompetitionSlug,
    bool HasCategories,
    string? CategoryLabel,
    ResultState State,
    /// <summary>Categories a winner can be chosen for (0 for a competition without categories).</summary>
    int CategoryCount,
    int WinnerCount,
    IReadOnlyList<AdminWinnerSlotDto> Slots,
    DateTimeOffset? PublishedAt,
    string? PublishedBy,
    string? Announcement,
    string? CoverImage,
    /// <summary>Published and actually visible right now (the competition is public and closed for entries).</summary>
    bool IsPublicNow,
    /// <summary>Why "publish" is not possible yet; empty when it is.</summary>
    IReadOnlyList<string> PublishProblems,
    int EligibleCount,
    IReadOnlyList<string> PublicFields,
    bool HasPublicFileField,
    /// <summary>Public-field files of eligible entries whose safe derivative is still being made / could not be made.</summary>
    int AssetsPending,
    IReadOnlyList<AssetProblemDto> AssetFailures,
    /// <summary>First, second and third place of a competition without categories; empty for one with categories.</summary>
    IReadOnlyList<AdminPlaceDto> Places,
    /// <summary>Works that can take a place, most votes first; empty for a competition with categories.</summary>
    IReadOnlyList<AdminPlaceCandidateDto> PlaceCandidates,
    VotingState Voting,
    /// <summary>Places recorded on the two podiums of a two-choice result (jury, public), 0–3 each.</summary>
    int JuryAwardCount = 0,
    int PublicAwardCount = 0);

/// <summary>One place of a competition without categories (1 is the overall winner) and the work holding it.</summary>
public sealed record AdminPlaceDto(int Place, AdminWinnerDto? Winner);

/// <summary>
/// A work that can be given a place, with what the public vote said about it. <c>VoteRank</c> is shared by works
/// level on votes — the tally says who is level, it never decides between them (D3).
/// </summary>
public sealed record AdminPlaceCandidateDto(
    int SubmissionId, string Reference, string Title, string ParticipantName, AdminPreviewDto? Preview,
    int VoteCount, int VoteRank,
    /// <summary>Of its votes, how many came from accounts opened after voting began.</summary>
    int LateVoteCount,
    /// <summary>
    /// Votes an admin set aside as fraudulent (D12). Not in <c>VoteCount</c> and not in the rank: the ranking an
    /// admin fills the places from is the valid vote.
    /// </summary>
    int InvalidatedVoteCount = 0);

/// <summary>A file whose public-safe derivative failed: the gallery shows a placeholder, never the original.</summary>
public sealed record AssetProblemDto(int SubmissionId, string Reference, int FileId, string Code);

public sealed record WinnerRefDto(int SubmissionId, string? Reference, string Title, string ParticipantName);

/// <summary>Result context shown on a submission's admin page: the slot this work competes in and its winner.</summary>
public sealed record AdminSubmissionResultDto(
    /// <summary>The category the work was entered in — its winner slot. Null: the competition as a whole.</summary>
    RefDto? Category,
    bool IsWinner,
    bool CanBeWinner,
    string? IneligibleReason,
    /// <summary>The slot's current winner when it is another work.</summary>
    WinnerRefDto? CurrentWinner,
    bool ResultPublished,
    /// <summary>Published AND visible right now (not while the competition accepts entries again).</summary>
    bool ResultPublicNow,
    /// <summary>The place this work holds in a competition without categories (1–3), if any.</summary>
    int? Place);

public sealed record SelectWinnerRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "Təklifi seçin.")]
    public int SubmissionId { get; init; }

    /// <summary>Required once the result is published: the admin saw that the change goes public at once.</summary>
    public bool ConfirmPublished { get; init; }
}

/// <summary>Replacing a winner names the current one explicitly, so a stale screen cannot overwrite a newer choice.</summary>
public sealed record ChangeWinnerRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "Təklifi seçin.")]
    public int SubmissionId { get; init; }

    [Range(1, int.MaxValue, ErrorMessage = "Hazırkı qalibi göstərin.")]
    public int CurrentWinnerSubmissionId { get; init; }

    public bool ConfirmPublished { get; init; }
}

/// <summary>
/// All three places at once. <c>Places</c> are the works for 1st, 2nd and 3rd place in that order (null: empty);
/// <c>Current</c> is what the admin's screen showed, so a stale screen cannot overwrite a newer choice.
/// </summary>
public sealed record SetPlacesRequest
{
    [Required(ErrorMessage = "Yerləri göstərin.")]
    public int?[] Places { get; init; } = [];

    [Required(ErrorMessage = "Hazırkı yerləri göstərin.")]
    public int?[] Current { get; init; } = [];

    public bool ConfirmPublished { get; init; }
}

public sealed record AnnouncementRequest
{
    [StringLength(2000, ErrorMessage = "Mətn 2000 simvoldan uzun ola bilməz.")]
    public string? Announcement { get; init; }
}

/// <summary>
/// Choosing winners — one per category (settlement) — and publishing a competition's result. Every rule is checked
/// here on the server; the database adds the last line of defence (one winner per category, a winner bound to its
/// own competition and category, xmin row versions).
/// </summary>
public sealed class ResultAdministration(
    MeydanDbContext db, AuditTrail audit, TimeProvider clock, IFileStorage storage, PublicDerivatives derivatives,
    Voting.DocumentPages pages)
{
    public const string ConfirmNeeded =
        "Nəticələr artıq dərc olunub: bu dəyişiklik saytda dərhal görünəcək. Dəyişikliyi təsdiqləyin.";

    private sealed record Slot(int? CategoryId, string? CategoryName);

    public async Task<AdminResultDto> GetAsync(int competitionId, CancellationToken ct)
    {
        var c = await db.Competitions.AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Result)
            .FirstOrDefaultAsync(x => x.Id == competitionId, ct)
            ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        var r = c.Result;
        var now = clock.GetUtcNow();

        var categories = await db.CompetitionCategories.AsNoTracking()
            .Where(k => k.CompetitionId == competitionId)
            .OrderBy(k => k.SortOrder).ThenBy(k => k.Name)
            .Select(k => new
            {
                k.Id, k.Name, k.IsActive,
                Eligible = k.Submissions.Count(s => CompetitionResult.EligibleStatuses.Contains(s.Status)),
            })
            .ToListAsync(ct);
        var categoryWinners = await db.CompetitionCategoryWinners.AsNoTracking()
            .Where(w => w.CompetitionId == competitionId)
            .Select(w => new { w.CompetitionCategoryId, w.SubmissionId, w.SelectedAt, w.SelectedById })
            .ToListAsync(ct);

        var winnerIds = categoryWinners.Select(w => w.SubmissionId).ToList();
        if (r is not null)
            for (var p = 1; p <= CompetitionResult.PlaceCount; p++)
                if (r.HolderOf(p) is { } held) winnerIds.Add(held);
        var winners = await WinnerCardsAsync(winnerIds, ct);
        var names = await UserNamesAsync(
            [.. categoryWinners.Select(w => w.SelectedById), r?.OverallWinnerSelectedById, r?.SecondPlaceSelectedById,
                r?.ThirdPlaceSelectedById, r?.PublishedById], ct);

        var slots = new List<AdminWinnerSlotDto>();
        foreach (var k in categories)
        {
            var w = categoryWinners.FirstOrDefault(x => x.CompetitionCategoryId == k.Id);
            // A switched-off category with nothing in it is not a place anyone can win; neither is any category of a
            // competition that no longer uses categories (unless it already holds a winner).
            if (w is null && (!c.HasCategories || (!k.IsActive && k.Eligible == 0))) continue;
            slots.Add(new AdminWinnerSlotDto(new RefDto(k.Id, k.Name), k.IsActive, k.Eligible,
                w is null ? null : winners[w.SubmissionId] with { SelectedAt = w.SelectedAt, SelectedBy = Name(names, w.SelectedById) }));
        }
        var categoryCount = slots.Count;
        if (!c.HasCategories || r?.OverallWinnerSubmissionId is not null)
        {
            // Without categories every final work competes for the prize, whatever nomination the vote filed it under.
            var anyCategory = !c.HasCategories;
            var eligibleWithout = await db.Submissions.CountAsync(s =>
                s.CompetitionId == competitionId && (anyCategory || s.CompetitionCategoryId == null)
                && CompetitionResult.EligibleStatuses.Contains(s.Status), ct);
            var overall = r?.OverallWinnerSubmissionId is { } id
                ? winners[id] with { SelectedAt = r.OverallWinnerSelectedAt ?? now, SelectedBy = Name(names, r.OverallWinnerSelectedById) }
                : null;
            slots.Add(new AdminWinnerSlotDto(null, true, eligibleWithout, overall));
        }
        var winnerCount = slots.Count(s => s.Winner is not null);

        var places = new List<AdminPlaceDto>();
        var candidates = new List<AdminPlaceCandidateDto>();
        if (!c.HasCategories)
        {
            for (var p = 1; p <= CompetitionResult.PlaceCount; p++)
            {
                AdminWinnerDto? holder = null;
                if (r?.HolderOf(p) is { } held)
                {
                    var (by, at) = r.ChosenOf(p);
                    holder = winners[held] with { SelectedAt = at ?? now, SelectedBy = Name(names, by) };
                }
                places.Add(new AdminPlaceDto(p, holder));
            }
            candidates = await PlaceCandidatesAsync(c, ct);
        }

        var fields = await db.CompetitionFields.AsNoTracking()
            .Where(f => f.CompetitionId == competitionId && f.IsPublicAfterResults)
            .OrderBy(f => f.SortOrder)
            .Select(f => new { f.Label, f.FieldType })
            .ToListAsync(ct);
        var eligible = await db.Submissions.CountAsync(
            s => s.CompetitionId == competitionId && CompetitionResult.EligibleStatuses.Contains(s.Status), ct);
        var publicFiles = db.SubmissionFiles.AsNoTracking().Where(f =>
            f.Submission.CompetitionId == competitionId
            && CompetitionResult.EligibleStatuses.Contains(f.Submission.Status)
            && f.Field != null && f.Field.IsPublicAfterResults);
        var pending = await publicFiles.CountAsync(f => f.PublicAssetStatus == PublicAssetStatus.Pending, ct);
        var failures = await publicFiles.Where(f => f.PublicAssetStatus == PublicAssetStatus.Failed)
            .OrderBy(f => f.SubmissionId).Take(50)
            .Select(f => new AssetProblemDto(f.SubmissionId, f.Submission.ReferenceNumber ?? "", f.Id, f.PublicAssetError ?? "error"))
            .ToListAsync(ct);

        var awards = await db.CompetitionAwards.AsNoTracking().Where(a => a.CompetitionId == competitionId)
            .Select(a => new AwardRow(a.Category, a.Place, a.SubmissionId, a.Points, a.Submission.ReferenceNumber, a.Submission.Status))
            .ToListAsync(ct);
        var publicIds = awards.Where(a => a.Category == AwardCategory.Public).Select(a => a.SubmissionId).ToList();
        var publicVotes = await db.Votes.AsNoTracking()
            .Where(v => publicIds.Contains(v.SubmissionId))
            .GroupBy(v => v.SubmissionId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var published = r?.IsPublished == true;
        return new AdminResultDto(
            c.Id, c.Title, c.Slug, c.HasCategories, c.CategoryLabel,
            published ? ResultState.Published : winnerCount > 0 || awards.Count > 0 ? ResultState.Prepared : ResultState.NoWinner,
            categoryCount,
            winnerCount,
            slots,
            r?.PublishedAt, Name(names, r?.PublishedById),
            r?.Announcement,
            r?.CoverImage,
            CompetitionPhases.AreResultsPublic(c, r?.PublishedAt, now) && c.Project.Status == ProjectStatus.Published,
            published ? [] : PublishProblems(c, slots, places, awards, publicVotes, now),
            eligible,
            fields.Select(f => f.Label).ToList(),
            fields.Any(f => f.FieldType == CompetitionFieldType.File),
            pending,
            failures,
            places,
            candidates,
            c.VotingStateAt(now),
            awards.Count(a => a.Category == AwardCategory.Jury),
            awards.Count(a => a.Category == AwardCategory.Public));
    }

    /// <summary>One place of a two-choice podium as the publication checks it.</summary>
    private sealed record AwardRow(AwardCategory Category, int Place, int SubmissionId, int Points, string? Reference, SubmissionStatus Status);

    /// <summary>
    /// First choice of a winner for the slot the submission competes in (its category, or the competition as a
    /// whole when it has none). Never overwrites: an existing winner is replaced only through ChangeAsync.
    /// </summary>
    public async Task<AdminResultDto> SelectAsync(int competitionId, SelectWinnerRequest req, Guid adminId, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        var s = await EligibleAsync(competition, req.SubmissionId, ct);
        var result = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        EnsureConfirmed(result, req.ConfirmPublished);
        var slot = await SlotAsync(competition, s, ct);
        var now = clock.GetUtcNow();

        if (slot.CategoryId is { } categoryId)
        {
            if (await db.CompetitionCategoryWinners.AnyAsync(w => w.CompetitionCategoryId == categoryId, ct))
                throw new ConflictException(WinnerExists(slot));
            db.CompetitionCategoryWinners.Add(new CompetitionCategoryWinner
            {
                CompetitionId = competitionId,
                CompetitionCategoryId = categoryId,
                SubmissionId = s.Id,
                SelectedById = adminId,
                SelectedAt = now,
            });
            audit.Record(adminId, AuditActions.CategoryWinnerSelected, AuditEntities.Competition, competitionId,
                $"{Describe(slot)}: submission {s.Id} ({s.ReferenceNumber}){PublishedNote(result)}");
        }
        else
        {
            if (result is null)
            {
                result = NewResult(competitionId);
                db.CompetitionResults.Add(result);
            }
            if (result.OverallWinnerSubmissionId is not null)
                throw new ConflictException(WinnerExists(slot));
            EnsureHoldsNoOtherPlace(result, s.Id, 1);
            result.OverallWinnerSubmissionId = s.Id;
            result.OverallWinnerSelectedById = adminId;
            result.OverallWinnerSelectedAt = now;
            audit.Record(adminId, AuditActions.WinnerSelected, AuditEntities.Competition, competitionId,
                $"winner: submission {s.Id} ({s.ReferenceNumber}){PublishedNote(result)}");
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request chose this category's winner (or created the result) first; the unique index stopped this one.
            throw new ConflictException(WinnerExists(slot));
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(Concurrent);
        }
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Explicit replacement of a slot's winner. Once published, only with the admin's explicit confirmation.</summary>
    public async Task<AdminResultDto> ChangeAsync(int competitionId, ChangeWinnerRequest req, Guid adminId, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        var next = await EligibleAsync(competition, req.SubmissionId, ct);
        var result = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        EnsureConfirmed(result, req.ConfirmPublished);
        var slot = await SlotAsync(competition, next, ct);
        var now = clock.GetUtcNow();

        if (slot.CategoryId is { } categoryId)
        {
            var w = await db.CompetitionCategoryWinners.FirstOrDefaultAsync(x => x.CompetitionCategoryId == categoryId, ct)
                    ?? throw new ConflictException($"{Describe(slot)} üzrə hələ qalib seçilməyib.");
            if (w.SubmissionId != req.CurrentWinnerSubmissionId)
                throw new ConflictException("Qalib artıq dəyişdirilib. Səhifəni yeniləyin.");
            if (w.SubmissionId == next.Id)
                throw ValidationFailedException.For("submissionId", "Bu iş artıq qalibdir.");
            var previousRef = await ReferenceAsync(w.SubmissionId, ct);
            audit.Record(adminId, AuditActions.CategoryWinnerChanged, AuditEntities.Competition, competitionId,
                $"{Describe(slot)}: submission {w.SubmissionId} ({previousRef}) → {next.Id} ({next.ReferenceNumber}){PublishedNote(result)}");
            w.SubmissionId = next.Id;
            w.SelectedById = adminId;
            w.SelectedAt = now;
        }
        else
        {
            if (result?.OverallWinnerSubmissionId is not { } current)
                throw new ConflictException("Bu müsabiqə üçün hələ qalib seçilməyib.");
            if (current != req.CurrentWinnerSubmissionId)
                throw new ConflictException("Qalib artıq dəyişdirilib. Səhifəni yeniləyin.");
            if (current == next.Id)
                throw ValidationFailedException.For("submissionId", "Bu iş artıq qalibdir.");
            EnsureHoldsNoOtherPlace(result, next.Id, 1);
            var previousRef = await ReferenceAsync(current, ct);
            audit.Record(adminId, AuditActions.WinnerChanged, AuditEntities.Competition, competitionId,
                $"winner: submission {current} ({previousRef}) → {next.Id} ({next.ReferenceNumber}){PublishedNote(result)}");
            result.OverallWinnerSubmissionId = next.Id;
            result.OverallWinnerSelectedById = adminId;
            result.OverallWinnerSelectedAt = now;
        }
        await SaveGuardedAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// Takes a work's win away (its slot has no winner again). A published result must keep at least one winner —
    /// withdraw the result first to empty it.
    /// </summary>
    public async Task<AdminResultDto> RemoveAsync(int competitionId, int submissionId, bool confirmPublished, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        var result = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        EnsureConfirmed(result, confirmPublished);
        var w = await db.CompetitionCategoryWinners.Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.CompetitionId == competitionId && x.SubmissionId == submissionId, ct);
        var place = w is null ? result?.PlaceOf(submissionId) : null;
        if (w is null && place is null)
            throw new NotFoundException("Bu iş bu müsabiqədə qalib deyil.");
        if (place is { } held && held < CompetitionResult.PlaceCount && result!.HolderOf(held + 1) is not null)
            throw new ConflictException(
                $"{Ordinal(held)} yeri boşaltmaq üçün əvvəlcə {Ordinal(held + 1)} yeri boşaldın — yerlər yuxarıdan doldurulur.");

        if (result?.IsPublished == true)
        {
            var total = await db.CompetitionCategoryWinners.CountAsync(x => x.CompetitionId == competitionId, ct)
                        + PlacesHeld(result);
            if (total <= 1)
                throw new ConflictException(
                    "Dərc olunmuş nəticədə ən azı bir qalib qalmalıdır. Bu qalibi ləğv etmək üçün əvvəlcə nəticəni dərcdən çıxarın.");
        }

        var reference = await ReferenceAsync(submissionId, ct);
        if (w is not null)
        {
            db.CompetitionCategoryWinners.Remove(w);
            audit.Record(adminId, AuditActions.CategoryWinnerRemoved, AuditEntities.Competition, competitionId,
                $"{w.Category.Name} (#{w.CompetitionCategoryId}): submission {submissionId} ({reference}){PublishedNote(result)}");
        }
        else
        {
            result!.SetPlace(place!.Value, null, null, null);
            audit.Record(adminId, AuditActions.WinnerRemoved, AuditEntities.Competition, competitionId,
                $"{PlaceLabel(place.Value)}: submission {submissionId} ({reference}){PublishedNote(result)}");
        }
        await SaveGuardedAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// First, second and third place of a competition without categories, saved together. The admin chooses every
    /// place; the vote only informs the choice. The screen names what it showed (<c>Current</c>), so a stale screen
    /// is refused; places are filled from the top; a work holds one place; and a published result keeps its winner.
    /// Each place that changes is audited on its own.
    /// </summary>
    public async Task<AdminResultDto> SetPlacesAsync(int competitionId, SetPlacesRequest req, Guid adminId, CancellationToken ct)
    {
        var competition = await CompetitionAsync(competitionId, ct);
        if (competition.HasCategories)
            throw ValidationFailedException.For("places", "Kateqoriyalı müsabiqədə qalib hər kateqoriya üzrə ayrıca seçilir.");
        if (req.Places.Length != CompetitionResult.PlaceCount || req.Current.Length != CompetitionResult.PlaceCount)
            throw ValidationFailedException.For("places", "Üç yerin hər birini göstərin (boş yer üçün null).");

        var result = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        EnsureConfirmed(result, req.ConfirmPublished);
        for (var p = 1; p <= CompetitionResult.PlaceCount; p++)
            if (result?.HolderOf(p) != req.Current[p - 1])
                throw new ConflictException("Yerlər artıq dəyişdirilib. Səhifəni yeniləyin.");

        var chosen = req.Places.OfType<int>().ToList();
        if (chosen.Distinct().Count() != chosen.Count)
            throw ValidationFailedException.For("places", "Bir iş yalnız bir yer tuta bilər.");
        for (var p = 2; p <= CompetitionResult.PlaceCount; p++)
            if (req.Places[p - 1] is not null && req.Places[p - 2] is null)
                throw ValidationFailedException.For("places", $"{Ordinal(p - 1)} yer boş qalarkən {Ordinal(p)} yer seçilə bilməz.");
        if (result?.IsPublished == true && req.Places[0] is null)
            throw new ConflictException(
                "Dərc olunmuş nəticədə ən azı bir qalib qalmalıdır. Bu qalibi ləğv etmək üçün əvvəlcə nəticəni dərcdən çıxarın.");

        var changed = Enumerable.Range(1, CompetitionResult.PlaceCount)
            .Where(p => result?.HolderOf(p) != req.Places[p - 1]).ToList();
        if (changed.Count == 0) return await GetAsync(competitionId, ct);

        var works = new Dictionary<int, Submission>();
        foreach (var p in changed)
            if (req.Places[p - 1] is { } id)
                works[id] = await EligibleAsync(competition, id, ct);

        if (result is null)
        {
            result = NewResult(competitionId);
            db.CompetitionResults.Add(result);
        }
        var now = clock.GetUtcNow();
        foreach (var p in changed)
        {
            var before = result.HolderOf(p);
            var after = req.Places[p - 1];
            var label = PlaceLabel(p);
            if (before is { } old && after is { } next)
                audit.Record(adminId, AuditActions.WinnerChanged, AuditEntities.Competition, competitionId,
                    $"{label}: submission {old} ({await ReferenceAsync(old, ct)}) → {next} ({works[next].ReferenceNumber}){PublishedNote(result)}");
            else if (after is { } added)
                audit.Record(adminId, AuditActions.WinnerSelected, AuditEntities.Competition, competitionId,
                    $"{label}: submission {added} ({works[added].ReferenceNumber}){PublishedNote(result)}");
            else
                audit.Record(adminId, AuditActions.WinnerRemoved, AuditEntities.Competition, competitionId,
                    $"{label}: submission {before} ({await ReferenceAsync(before!.Value, ct)}){PublishedNote(result)}");
            result.SetPlace(p, after, adminId, now);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request created the result first.
            throw new ConflictException(Concurrent);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(Concurrent);
        }
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// Publishes whatever winners exist — not every category needs one (the admin sees how many do before
    /// confirming) — but at least one must.
    /// </summary>
    public async Task<AdminResultDto> PublishAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        var current = await GetAsync(competitionId, ct);
        if (current.State == ResultState.Published) return current;
        if (current.PublishProblems.Count > 0)
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["result"] = current.PublishProblems.ToArray() });

        var result = await EnsureResultAsync(competitionId, ct);
        result.PublishedAt = clock.GetUtcNow();
        result.PublishedById = adminId;
        var podiums = current.JuryAwardCount + current.PublicAwardCount > 0
            ? $"; podiums: jury {current.JuryAwardCount}, public {current.PublicAwardCount}"
            : "";
        audit.Record(adminId, AuditActions.ResultsPublished, AuditEntities.Competition, competitionId,
            (current.HasCategories
                ? $"winners: {current.WinnerCount} of {current.CategoryCount} categories"
                : current.Places.Count(x => x.Winner is not null) > 1
                    ? "places: " + string.Join(", ", current.Places.Where(x => x.Winner is not null).Select(x => $"{x.Place} {x.Winner!.Reference}"))
                    : $"winner: {Winners(current).FirstOrDefault()?.Reference}") + podiums);
        await SaveGuardedAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Takes the result and the gallery off the site again (e.g. to correct a mistake). Winners are kept. Audited.</summary>
    public async Task<AdminResultDto> UnpublishAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        var r = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        if (r?.IsPublished == true)
        {
            r.PublishedAt = null;
            r.PublishedById = null;
            audit.Record(adminId, AuditActions.ResultsUnpublished, AuditEntities.Competition, competitionId);
            await SaveGuardedAsync(ct);
        }
        return await GetAsync(competitionId, ct);
    }

    public async Task<AdminResultDto> SetAnnouncementAsync(int competitionId, string? text, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        var r = await EnsureResultAsync(competitionId, ct);
        r.Announcement = AdminSlugs.NullIfBlank(text);
        audit.Record(adminId, AuditActions.ResultUpdated, AuditEntities.Competition, competitionId, "announcement");
        await SaveGuardedAsync(ct);
        return await GetAsync(competitionId, ct);
    }

    /// <summary>
    /// The results banner: any raster image an admin uploads (a poster, a celebration artwork), re-encoded as a
    /// metadata-free WEBP in the public covers area — the same pipeline as project covers.
    /// </summary>
    public async Task<AdminResultDto> SetCoverAsync(int competitionId, IFormFile? file, UploadOptions uploads, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        var url = await CoverImages.SaveAsync(file, uploads, storage, derivatives, ct);
        string? previous;
        try
        {
            var r = await EnsureResultAsync(competitionId, ct);
            previous = r.CoverImage;
            r.CoverImage = url;
            r.CoverIsDemo = false; // an admin's own cover: demo removal never touches it
            audit.Record(adminId, AuditActions.ResultCoverChanged, AuditEntities.Competition, competitionId);
            await SaveGuardedAsync(ct);
        }
        catch
        {
            CoverImages.DeleteIfOwned(url, storage); // no orphan when the row was not written
            throw;
        }
        CoverImages.DeleteIfOwned(previous, storage);
        return await GetAsync(competitionId, ct);
    }

    public async Task<AdminResultDto> RemoveCoverAsync(int competitionId, Guid adminId, CancellationToken ct)
    {
        await CompetitionAsync(competitionId, ct);
        var r = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        if (r?.CoverImage is { } previous)
        {
            r.CoverImage = null;
            r.CoverIsDemo = false;
            audit.Record(adminId, AuditActions.ResultCoverChanged, AuditEntities.Competition, competitionId, "removed");
            await SaveGuardedAsync(ct);
            CoverImages.DeleteIfOwned(previous, storage);
        }
        return await GetAsync(competitionId, ct);
    }

    /// <summary>Winner context for one submission's admin page: its slot and who holds it.</summary>
    public async Task<AdminSubmissionResultDto> ForSubmissionAsync(Submission s, CancellationToken ct)
    {
        var result = await db.CompetitionResults.AsNoTracking().FirstOrDefaultAsync(x => x.CompetitionId == s.CompetitionId, ct);
        var competition = await db.Competitions.AsNoTracking().Include(c => c.Project).FirstAsync(c => c.Id == s.CompetitionId, ct);
        var publicNow = CompetitionPhases.AreResultsPublic(competition, result?.PublishedAt, clock.GetUtcNow())
                        && competition.Project.Status == ProjectStatus.Published;
        int? holder;
        RefDto? category = null;
        if (s.CompetitionCategoryId is { } categoryId && competition.HasCategories)
        {
            category = await db.CompetitionCategories.AsNoTracking().Where(k => k.Id == categoryId)
                .Select(k => new RefDto(k.Id, k.Name)).FirstOrDefaultAsync(ct);
            holder = await db.CompetitionCategoryWinners.AsNoTracking().Where(w => w.CompetitionCategoryId == categoryId)
                .Select(w => (int?)w.SubmissionId).FirstOrDefaultAsync(ct);
        }
        else
        {
            holder = result?.OverallWinnerSubmissionId;
        }

        WinnerRefDto? current = null;
        if (holder is { } h && h != s.Id)
        {
            current = await db.Submissions.AsNoTracking().Where(x => x.Id == h)
                .Select(x => new WinnerRefDto(x.Id, x.ReferenceNumber, x.Title, x.User.FirstName + " " + x.User.LastName))
                .FirstAsync(ct);
        }
        var reason = IneligibleReason(s.Status);
        if (reason is null && s.CompetitionCategoryId is null && competition.HasCategories)
            reason = NoCategory;
        var place = competition.HasCategories ? null : result?.PlaceOf(s.Id);
        return new AdminSubmissionResultDto(category, holder == s.Id, reason is null, reason, current, result?.IsPublished == true, publicNow, place);
    }

    /// <summary>Whether a work currently holds any win — a podium place included (moderation must not make a winner ineligible).</summary>
    public async Task<bool> IsWinnerAsync(int submissionId, CancellationToken ct) =>
        await db.CompetitionCategoryWinners.AnyAsync(w => w.SubmissionId == submissionId, ct)
        || await db.CompetitionResults.AnyAsync(r => r.OverallWinnerSubmissionId == submissionId
                                                      || r.SecondPlaceSubmissionId == submissionId
                                                      || r.ThirdPlaceSubmissionId == submissionId, ct)
        || await db.CompetitionAwards.AnyAsync(a => a.SubmissionId == submissionId, ct);

    public static string? IneligibleReason(SubmissionStatus status) => status switch
    {
        _ when CompetitionResult.IsEligible(status) => null,
        SubmissionStatus.Draft => "Qaralama qalib seçilə bilməz.",
        SubmissionStatus.Rejected => "Rədd edilmiş təklif qalib seçilə bilməz.",
        SubmissionStatus.Withdrawn => "Geri çəkilmiş təklif qalib seçilə bilməz.",
        _ => "Bu təklif qalib seçilə bilməz.",
    };

    // ---- Helpers ----------------------------------------------------------------------------------

    private const string Concurrent = "Nəticə eyni anda başqa yerdə dəyişdirildi. Səhifəni yeniləyin.";
    private const string NoCategory = "Bu təklif heç bir kateqoriyaya aid deyil — kateqoriyalı müsabiqədə qalib seçilə bilməz.";

    private static string Describe(Slot slot) => slot.CategoryName ?? "Müsabiqə";

    private static string WinnerExists(Slot slot) =>
        slot.CategoryId is null ? "Bu müsabiqə üçün artıq qalib seçilib." : $"{slot.CategoryName} üzrə qalib artıq seçilib.";

    private static string PublishedNote(CompetitionResult? r) => r?.IsPublished == true ? " (published result)" : "";

    /// <summary>"1-ci", "2-ci", "3-cü" — a place as Azerbaijani writes it.</summary>
    private static string Ordinal(int place) => place == 3 ? "3-cü" : $"{place}-ci";

    /// <summary>How the audit trail names a place: the first is the competition's winner, as it always was.</summary>
    private static string PlaceLabel(int place) => place == 1 ? "winner" : $"place {place}";

    private static int PlacesHeld(CompetitionResult r) =>
        Enumerable.Range(1, CompetitionResult.PlaceCount).Count(p => r.HolderOf(p) is not null);

    /// <summary>One work, one place: moving a work between places is done with all three places at once.</summary>
    private static void EnsureHoldsNoOtherPlace(CompetitionResult? r, int submissionId, int place)
    {
        if (r?.PlaceOf(submissionId) is { } held && held != place)
            throw ValidationFailedException.For("submissionId",
                $"Bu iş artıq {Ordinal(held)} yeri tutur. Yerləri “Nəticələr” bölməsində birlikdə dəyişin.");
    }

    private static IEnumerable<AdminWinnerDto> Winners(AdminResultDto r) =>
        r.Slots.Where(s => s.Winner is not null).Select(s => s.Winner!);

    /// <summary>Once the result is public, a winner change needs the admin's explicit confirmation — never silent.</summary>
    private static void EnsureConfirmed(CompetitionResult? r, bool confirmed)
    {
        if (r?.IsPublished == true && !confirmed)
            throw new ConflictException(ConfirmNeeded);
    }

    private static List<string> PublishProblems(
        Competition c, IReadOnlyList<AdminWinnerSlotDto> slots, IReadOnlyList<AdminPlaceDto> places,
        IReadOnlyList<AwardRow> awards, IReadOnlyDictionary<int, int> publicVotes, DateTimeOffset now)
    {
        var problems = new List<string>();
        var winners = slots.Where(s => s.Winner is not null).ToList();
        // A two-choice result publishes on its podiums: each one recorded is whole, every work still eligible, and the
        // public's numbers still the valid vote's own.
        var podiums = awards.GroupBy(a => a.Category).ToList();
        if (winners.Count == 0 && !podiums.Any(g => g.Count() == CompetitionAward.PlaceCount)) problems.Add("Heç bir qalib seçilməyib.");
        foreach (var g in podiums.Where(g => g.Count() != CompetitionAward.PlaceCount))
            problems.Add($"{AwardAdministration.Name(g.Key)}: üç yerin hamısı qeyd olunmayıb.");
        foreach (var a in awards.Where(a => !CompetitionResult.IsEligible(a.Status)))
            problems.Add($"{AwardAdministration.Name(a.Category)}: {Ordinal(a.Place)} yerdəki iş ({a.Reference}) artıq uyğun deyil — podiumu yeniləyin.");
        foreach (var a in awards.Where(a => a.Category == AwardCategory.Public && publicVotes.GetValueOrDefault(a.SubmissionId) != a.Points))
            problems.Add($"Xalqın seçimi: {a.Reference} indi {publicVotes.GetValueOrDefault(a.SubmissionId)} etibarlı səsdir, podiumda {a.Points} — podiumu yeniləyin.");
        foreach (var s in winners.Where(s => !CompetitionResult.IsEligible(s.Winner!.Status)))
            problems.Add($"{s.Category?.Name ?? "Müsabiqə"} üzrə qalib artıq uyğun deyil — qalibi dəyişin.");
        // Places are filled from the top; only a permanently deleted work can leave a gap behind it.
        for (var i = 1; i < places.Count; i++)
            if (places[i].Winner is not null && places[i - 1].Winner is null)
                problems.Add($"{Ordinal(places[i - 1].Place)} yer boşdur, amma {Ordinal(places[i].Place)} yer seçilib.");
        foreach (var p in places.Skip(1).Where(p => p.Winner is { } w && !CompetitionResult.IsEligible(w.Status)))
            problems.Add($"{Ordinal(p.Place)} yerdəki iş artıq uyğun deyil — onu dəyişin.");
        if (!Competition.IsPubliclyVisible(c.Status)) problems.Add("Müsabiqə saytda görünmür: əvvəlcə onu dərc edin.");
        else if (c.IsAcceptingSubmissions(now)) problems.Add("Qəbul hələ açıqdır: nəticəni dərc etmək üçün əvvəlcə qəbulu bağlayın.");
        if (c.Project.Status != ProjectStatus.Published) problems.Add("Layihə dərc olunmayıb.");
        return problems;
    }

    private async Task<Competition> CompetitionAsync(int competitionId, CancellationToken ct) =>
        await db.Competitions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == competitionId, ct)
        ?? throw new NotFoundException("Müsabiqə tapılmadı.");

    /// <summary>The submission must exist, belong to this competition and be a final, non-rejected entry.</summary>
    private async Task<Submission> EligibleAsync(Competition competition, int submissionId, CancellationToken ct)
    {
        var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, ct)
                ?? throw ValidationFailedException.For("submissionId", "Təklif tapılmadı.");
        if (s.CompetitionId != competition.Id)
            throw ValidationFailedException.For("submissionId", "Bu təklif bu müsabiqəyə aid deyil.");
        if (IneligibleReason(s.Status) is { } reason)
            throw ValidationFailedException.For("submissionId", reason);
        if (s.CompetitionCategoryId is null && competition.HasCategories)
            throw ValidationFailedException.For("submissionId", NoCategory);
        return s;
    }

    /// <summary>
    /// A work can only ever win the category it was entered in. Without categories there is one prize for the whole
    /// competition, whatever nomination the vote filed the work under — a single "Ümumi" list is still the whole.
    /// </summary>
    private async Task<Slot> SlotAsync(Competition competition, Submission s, CancellationToken ct)
    {
        if (!competition.HasCategories || s.CompetitionCategoryId is not { } categoryId) return new Slot(null, null);
        var name = await db.CompetitionCategories.Where(k => k.Id == categoryId && k.CompetitionId == s.CompetitionId)
            .Select(k => k.Name).FirstOrDefaultAsync(ct)
            ?? throw ValidationFailedException.For("submissionId", "Təklifin kateqoriyası tapılmadı.");
        return new Slot(categoryId, name);
    }

    private static CompetitionResult NewResult(int competitionId) => new() { CompetitionId = competitionId };

    /// <summary>The competition's result row, created on first use (a parallel creation is caught by the unique index).</summary>
    private async Task<CompetitionResult> EnsureResultAsync(int competitionId, CancellationToken ct)
    {
        var r = await db.CompetitionResults.FirstOrDefaultAsync(x => x.CompetitionId == competitionId, ct);
        if (r is not null) return r;
        r = NewResult(competitionId);
        db.CompetitionResults.Add(r);
        try
        {
            await db.SaveChangesAsync(ct);
            return r;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(r).State = EntityState.Detached;
            return await db.CompetitionResults.FirstAsync(x => x.CompetitionId == competitionId, ct);
        }
    }

    private Task<string?> ReferenceAsync(int submissionId, CancellationToken ct) =>
        db.Submissions.Where(s => s.Id == submissionId).Select(s => s.ReferenceNumber).FirstOrDefaultAsync(ct);

    /// <summary>
    /// What the admin sees of each winner: reference, title, participant (admins may see names) and which file to
    /// show — the admin's gallery choice, else the first image, else the first file (a PDF shows its first page).
    /// </summary>
    internal async Task<Dictionary<int, AdminWinnerDto>> WinnerCardsAsync(IReadOnlyCollection<int> submissionIds, CancellationToken ct)
    {
        if (submissionIds.Count == 0) return [];
        var rows = await db.Submissions.AsNoTracking()
            .Where(s => submissionIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id, s.ReferenceNumber, s.Title, Name = s.User.FirstName + " " + s.User.LastName, s.Status, s.PreviewFileId,
                Files = s.Files.OrderBy(f => f.SortOrder).ThenBy(f => f.Id)
                    .Select(f => new { f.Id, f.ContentType, f.ThumbnailStatus }).ToList(),
            })
            .ToListAsync(ct);
        var chosen = rows.ToDictionary(s => s.Id, s =>
            s.Files.FirstOrDefault(f => f.Id == s.PreviewFileId)
            ?? s.Files.FirstOrDefault(f => f.ContentType.StartsWith("image/", StringComparison.Ordinal))
            ?? s.Files.FirstOrDefault());
        var previewPages = await SubmissionImages.ListPreviewPagesAsync(
            db, pages, chosen.Values.Where(f => f is not null).Select(f => f!.Id), ct);
        return rows.ToDictionary(s => s.Id, s =>
        {
            var file = chosen[s.Id];
            return new AdminWinnerDto(s.Id, s.ReferenceNumber ?? "", s.Title, s.Name.Trim(), s.Status,
                SubmissionImages.WithPage(
                    file is null ? null : new AdminPreviewDto(file.Id, file.ThumbnailStatus == PublicAssetStatus.Ready),
                    previewPages),
                default, null);
        });
    }

    /// <summary>
    /// Every final work of a competition without categories with its public vote, most votes first. Works level on
    /// votes share a rank and keep a stable order by reference — the same reading as the voting tab.
    /// </summary>
    internal async Task<List<AdminPlaceCandidateDto>> PlaceCandidatesAsync(Competition c, CancellationToken ct)
    {
        var ids = await db.Submissions.AsNoTracking()
            .Where(s => s.CompetitionId == c.Id && CompetitionResult.EligibleStatuses.Contains(s.Status))
            .Select(s => s.Id)
            .ToListAsync(ct);
        var cards = await WinnerCardsAsync(ids, ct);
        var votes = await db.Votes.AsNoTracking()
            .Where(v => ids.Contains(v.SubmissionId))
            .GroupBy(v => v.SubmissionId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var opened = c.VotingStartsAt;
        var late = await db.Votes.AsNoTracking()
            .Where(v => ids.Contains(v.SubmissionId) && opened != null && v.User.CreatedAt >= opened)
            .GroupBy(v => v.SubmissionId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var setAside = await db.Votes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => ids.Contains(v.SubmissionId) && v.InvalidatedAt != null)
            .GroupBy(v => v.SubmissionId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var ordered = cards.Values
            .OrderByDescending(w => votes.GetValueOrDefault(w.SubmissionId))
            .ThenBy(w => w.Reference, StringComparer.Ordinal)
            .ToList();
        var list = new List<AdminPlaceCandidateDto>(ordered.Count);
        var rank = 0;
        var previous = int.MinValue;
        for (var i = 0; i < ordered.Count; i++)
        {
            var w = ordered[i];
            var count = votes.GetValueOrDefault(w.SubmissionId);
            if (count != previous) (rank, previous) = (i + 1, count);
            list.Add(new AdminPlaceCandidateDto(w.SubmissionId, w.Reference, w.Title, w.ParticipantName, w.Preview, count, rank,
                late.GetValueOrDefault(w.SubmissionId), setAside.GetValueOrDefault(w.SubmissionId)));
        }
        return list;
    }

    private async Task SaveGuardedAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(Concurrent);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(Concurrent);
        }
    }

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
