using Meydan.Api.Common;
using Meydan.Domain;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Admin;

/// <summary>
/// What an admin is told before a permanent deletion, counted by the server. The dialog shows these numbers;
/// it never works them out from what happens to be loaded in the browser.
/// </summary>
/// <param name="Title">The entity as the admin knows it: a project's name, a competition's title, a reference number.</param>
/// <param name="Context">Where it sits — the project a competition belongs to, the competition and participant of an entry.</param>
/// <param name="RequiresTypedConfirmation">True once anything else would go with it: the dialog then asks for the word SİL.</param>
public sealed record DeleteImpactDto(
    string Title,
    string? Context,
    int Competitions,
    int Categories,
    int Fields,
    int Submissions,
    int Answers,
    int Files,
    int Results,
    int Winners,
    int Notes,
    bool RequiresTypedConfirmation);

/// <summary>What actually went, reported back to the admin (and to the log) after the deletion.</summary>
public sealed record DeleteReport(DeleteImpactDto Removed, int StoredFilesDeleted, int StoredFilesFailed);

/// <summary>
/// Permanent deletion of admin-owned content — a project, a competition, a single entry — with everything that
/// belongs to it and nothing that does not.
///
/// Three rules shape this class:
///
/// 1. The tree is walked explicitly. Several of these relationships are <c>Restrict</c> on purpose (a submission
///    cannot lose its competition, a winner cannot lose its category), so a bare <c>Remove</c> on the root would
///    simply fail. The order below — wins, then results, then entries, then categories and fields, then the
///    competition — is the order the foreign keys allow, and it means the code states what it destroys.
/// 2. Nothing outside the chosen tree is touched. Participants keep their accounts, other projects and
///    competitions keep their rows, and the audit trail keeps every entry it already had.
/// 3. The database is the decision, the file store follows. Rows are removed in one transaction; only after it
///    commits are the stored files deleted, and each one only if no surviving row still points at it. A file that
///    refuses to go is reported, never allowed to undo the deletion.
/// </summary>
public sealed class PermanentDeletion(
    MeydanDbContext db,
    IFileStorage storage,
    AuditTrail audit,
    ILogger<PermanentDeletion> logger)
{
    /// <summary>The refusal for an account that has voted — the admin's screen says the same words.</summary>
    public const string VotedAccount = "Bu hesab səsvermədə iştirak etdiyi üçün həmişəlik silinə bilməz.";

    // ---- Impact ---------------------------------------------------------------------------------------------

    public async Task<DeleteImpactDto> ProjectImpactAsync(int id, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException("Layihə tapılmadı.");
        var competitions = await db.Competitions.Where(c => c.ProjectId == id).Select(c => c.Id).ToListAsync(ct);
        return await CompetitionsImpactAsync(project.Name, null, competitions, competitions.Count, ct);
    }

    public async Task<DeleteImpactDto> CompetitionImpactAsync(int id, CancellationToken ct)
    {
        var competition = await db.Competitions.AsNoTracking().Include(c => c.Project)
                              .FirstOrDefaultAsync(c => c.Id == id, ct)
                          ?? throw new NotFoundException("Müsabiqə tapılmadı.");
        return await CompetitionsImpactAsync(competition.Title, competition.Project.Name, [id], 0, ct);
    }

    public async Task<DeleteImpactDto> SubmissionImpactAsync(int id, CancellationToken ct)
    {
        var submission = await db.Submissions.AsNoTracking()
                             .Include(s => s.Competition).Include(s => s.User)
                             .FirstOrDefaultAsync(s => s.Id == id, ct)
                         ?? throw new NotFoundException("Təklif tapılmadı.");
        var files = await db.SubmissionFiles.CountAsync(f => f.SubmissionId == id, ct);
        var answers = await db.SubmissionAnswers.CountAsync(a => a.SubmissionId == id, ct);
        var notes = await db.SubmissionNotes.CountAsync(n => n.SubmissionId == id, ct);
        var wins = await db.CompetitionCategoryWinners.CountAsync(w => w.SubmissionId == id, ct)
                   + await db.CompetitionResults.CountAsync(r => r.OverallWinnerSubmissionId == id
                                                                 || r.SecondPlaceSubmissionId == id
                                                                 || r.ThirdPlaceSubmissionId == id, ct);

        var title = submission.ReferenceNumber is { Length: > 0 } reference ? reference : submission.Title;
        var who = $"{submission.User.FirstName} {submission.User.LastName}".Trim();
        return new DeleteImpactDto(
            title, $"{submission.Competition.Title} — {who}",
            Competitions: 0, Categories: 0, Fields: 0, Submissions: 1,
            Answers: answers, Files: files, Results: 0, Winners: wins, Notes: notes,
            RequiresTypedConfirmation: files > 0 || wins > 0 || submission.Status != SubmissionStatus.Draft);
    }

    /// <summary>
    /// An account is only ever removed when it holds nothing: no vote, no entry, no draft, not an admin's, and not
    /// the admin's own. Work is deleted through the entry it belongs to, where what goes is spelled out — never
    /// sideways through a name in a list. A vote is never deleted at all, so an account that cast one stays.
    /// </summary>
    public async Task<DeleteImpactDto> ParticipantImpactAsync(Guid id, Guid adminId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new NotFoundException("İştirakçı tapılmadı.");
        await EnsureRemovableAsync(user, adminId, ct);

        var name = $"{user.FirstName} {user.LastName}".Trim();
        return new DeleteImpactDto(
            name.Length > 0 ? name : user.Email ?? id.ToString(),
            user.Email,
            Competitions: 0, Categories: 0, Fields: 0, Submissions: 0, Answers: 0, Files: 0,
            Results: 0, Winners: 0, Notes: 0,
            RequiresTypedConfirmation: true); // an account is a person: always ask for the word
    }

    public async Task<DeleteReport> DeleteParticipantAsync(Guid id, Guid adminId, CancellationToken ct)
    {
        var impact = await ParticipantImpactAsync(id, adminId, ct);
        return await RunAsync(impact, async _ =>
        {
            // The account's row is locked before it is checked: a vote being written for it holds a key-share lock
            // on the same row (its foreign key), so it either commits first and is seen below, or waits and then
            // fails against the deleted account — a vote can never slip in between the check and the delete.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE", ct);
            var user = await db.Users.FirstAsync(u => u.Id == id, ct);
            await EnsureRemovableAsync(user, adminId, ct); // checked again inside the transaction

            // Sign-ins, sessions and pending mail go with the account (all cascades). The audit trail does not:
            // it has no foreign key to the user, so what they did stays recorded under an id that is now gone.
            db.Users.Remove(user);
            audit.Record(adminId, AuditActions.ParticipantPermanentlyDeleted, AuditEntities.Account, id,
                $"{impact.Title}{(impact.Context is null ? "" : $"; {impact.Context}")}");
        }, ct);
    }

    private async Task EnsureRemovableAsync(ApplicationUser user, Guid adminId, CancellationToken ct)
    {
        if (user.Id == adminId)
            throw new ConflictException("Öz hesabınızı silə bilməzsiniz.");

        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (await db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == adminRoleId, ct))
            throw new ConflictException("İdarəçi hesabı bu səhifədən silinmir.");

        // A vote — counted or set aside — is part of the competition's record and of any frozen result, and the
        // database would take it with the account (Votes.UserId still cascades on delete). Checked before the entries
        // so an admin is never sent to delete work for an account that would still be refused. The query filter hides
        // set-aside votes, hence IgnoreQueryFilters. Temporary, application-level protection only: it stops this
        // path, not a direct database delete — the D1 migration (Phase22) makes the foreign key RESTRICT
        // (docs/voting/D1-PHASE21-PLAN.md).
        if (await db.Votes.IgnoreQueryFilters().AnyAsync(v => v.UserId == user.Id, ct))
            throw new ConflictException(VotedAccount);

        if (await db.Submissions.AnyAsync(s => s.UserId == user.Id, ct))
            throw new ConflictException(
                "Bu iştirakçının təklifi var. Əvvəlcə təklifləri silin — orada nələrin gedəcəyi göstərilir.");

        // An admin's note names its author and must not lose them; participants never write notes.
        if (await db.SubmissionNotes.AnyAsync(n => n.AuthorId == user.Id, ct))
            throw new ConflictException("Bu hesabın yazdığı admin qeydləri var — silinmir.");
    }

    private async Task<DeleteImpactDto> CompetitionsImpactAsync(
        string title, string? context, IReadOnlyCollection<int> competitionIds, int competitionCount, CancellationToken ct)
    {
        var submissions = await db.Submissions.Where(s => competitionIds.Contains(s.CompetitionId)).Select(s => s.Id).ToListAsync(ct);
        var impact = new DeleteImpactDto(
            title, context,
            Competitions: competitionCount,
            Categories: await db.CompetitionCategories.CountAsync(c => competitionIds.Contains(c.CompetitionId), ct),
            Fields: await db.CompetitionFields.CountAsync(f => competitionIds.Contains(f.CompetitionId), ct),
            Submissions: submissions.Count,
            Answers: await db.SubmissionAnswers.CountAsync(a => submissions.Contains(a.SubmissionId), ct),
            Files: await db.SubmissionFiles.CountAsync(f => submissions.Contains(f.SubmissionId), ct),
            Results: await db.CompetitionResults.CountAsync(r => competitionIds.Contains(r.CompetitionId), ct),
            Winners: await db.CompetitionCategoryWinners.CountAsync(w => competitionIds.Contains(w.CompetitionId), ct)
                     + await db.CompetitionResults.CountAsync(r => competitionIds.Contains(r.CompetitionId) && r.OverallWinnerSubmissionId != null, ct)
                     + await db.CompetitionResults.CountAsync(r => competitionIds.Contains(r.CompetitionId) && r.SecondPlaceSubmissionId != null, ct)
                     + await db.CompetitionResults.CountAsync(r => competitionIds.Contains(r.CompetitionId) && r.ThirdPlaceSubmissionId != null, ct),
            Notes: await db.SubmissionNotes.CountAsync(n => submissions.Contains(n.SubmissionId), ct),
            RequiresTypedConfirmation: false);
        return impact with { RequiresTypedConfirmation = HasDependents(impact) };
    }

    private static bool HasDependents(DeleteImpactDto i) =>
        i.Competitions + i.Categories + i.Fields + i.Submissions + i.Files + i.Results + i.Winners > 0;

    // ---- Deletion -------------------------------------------------------------------------------------------

    public async Task<DeleteReport> DeleteProjectAsync(int id, Guid adminId, CancellationToken ct)
    {
        var impact = await ProjectImpactAsync(id, ct);
        return await RunAsync(impact, async keys =>
        {
            var project = await db.Projects.FirstAsync(p => p.Id == id, ct);
            foreach (var competitionId in await db.Competitions.Where(c => c.ProjectId == id).Select(c => c.Id).ToListAsync(ct))
                await RemoveCompetitionTreeAsync(competitionId, keys, ct);

            Cover(keys, project.CoverImage);
            db.Projects.Remove(project);
            audit.Record(adminId, AuditActions.ProjectPermanentlyDeleted, AuditEntities.Project, id,
                $"{project.Slug}; {Summary(impact)}");
        }, ct);
    }

    public async Task<DeleteReport> DeleteCompetitionAsync(int id, Guid adminId, CancellationToken ct)
    {
        var impact = await CompetitionImpactAsync(id, ct);
        return await RunAsync(impact, async keys =>
        {
            var competition = await db.Competitions.FirstAsync(c => c.Id == id, ct);
            var slug = competition.Slug;
            var projectId = competition.ProjectId;
            await RemoveCompetitionTreeAsync(id, keys, ct);
            // The project the competition hung from is deliberately left alone.
            audit.Record(adminId, AuditActions.CompetitionPermanentlyDeleted, AuditEntities.Competition, id,
                $"{slug}; layihə {projectId}; {Summary(impact)}");
        }, ct);
    }

    public async Task<DeleteReport> DeleteSubmissionAsync(int id, Guid adminId, CancellationToken ct)
    {
        var impact = await SubmissionImpactAsync(id, ct);
        // A frozen finalist is part of the jury final's record (D13): it is not taken out of it one work at a time.
        if (await db.CompetitionFinalists.AnyAsync(f => f.SubmissionId == id, ct))
            throw new ConflictException("Bu iş müsabiqənin finalçısıdır — silinmir.");
        // A work every juror is to score (or has scored) belongs to the jury's record: it is not taken out of it.
        if (await db.JuryRosterEntries.AnyAsync(r => r.SubmissionId == id, ct))
            throw new ConflictException("Bu iş münsif qiymətləndirməsinin siyahısındadır — silinmir.");
        // A work on a podium of the result is part of what the result announces (or will announce).
        if (await db.CompetitionAwards.AnyAsync(a => a.SubmissionId == id, ct))
            throw new ConflictException("Bu iş nəticənin podiumundadır — silinmir. Əvvəlcə podiumu dəyişin.");
        return await RunAsync(impact, async keys =>
        {
            var submission = await db.Submissions.FirstAsync(s => s.Id == id, ct);
            var reference = submission.ReferenceNumber ?? $"qaralama {id}";
            var competitionId = submission.CompetitionId;
            await RemoveSubmissionsAsync([id], keys, ct);
            // The participant's account is not ours to delete: only their entry goes.
            audit.Record(adminId, AuditActions.SubmissionPermanentlyDeleted, AuditEntities.Submission, id,
                $"{reference}; müsabiqə {competitionId}; {Summary(impact)}");
        }, ct);
    }

    /// <summary>
    /// One competition and everything under it. The caller has already decided this competition is going; this
    /// method never looks upwards at the project.
    /// </summary>
    private async Task RemoveCompetitionTreeAsync(int competitionId, List<string> keys, CancellationToken ct)
    {
        // The jury's evaluation of every work (IndependentPublicAndJuryAwardsV1) is a record of its own: a competition
        // that has one is not deleted.
        if (await db.JuryEvaluations.AnyAsync(e => e.CompetitionId == competitionId, ct))
            throw new ConflictException("Bu müsabiqənin münsif qiymətləndirməsi var — müsabiqə silinmir.");
        var submissionIds = await db.Submissions.Where(s => s.CompetitionId == competitionId).Select(s => s.Id).ToListAsync(ct);
        // The result's podiums point at its works: they go first.
        db.CompetitionAwards.RemoveRange(await db.CompetitionAwards.Where(a => a.CompetitionId == competitionId).ToListAsync(ct));
        // The jury final goes with its competition, its finalists before the works they point at (their marks, jurors
        // and tie decisions follow by cascade).
        db.CompetitionFinalists.RemoveRange(await db.CompetitionFinalists.Where(f => f.CompetitionId == competitionId).ToListAsync(ct));
        db.CompetitionFinals.RemoveRange(await db.CompetitionFinals.Where(f => f.CompetitionId == competitionId).ToListAsync(ct));
        await RemoveSubmissionsAsync(submissionIds, keys, ct);

        // Wins that name no submission of ours (there should be none left) and the competition's result row.
        db.CompetitionCategoryWinners.RemoveRange(
            await db.CompetitionCategoryWinners.Where(w => w.CompetitionId == competitionId).ToListAsync(ct));
        var result = await db.CompetitionResults.FirstOrDefaultAsync(r => r.CompetitionId == competitionId, ct);
        if (result is not null)
        {
            Cover(keys, result.CoverImage);
            db.CompetitionResults.Remove(result);
        }

        var categories = await db.CompetitionCategories.Where(c => c.CompetitionId == competitionId).ToListAsync(ct);
        foreach (var category in categories)
            Cover(keys, category.Image);
        db.CompetitionCategories.RemoveRange(categories);
        db.CompetitionFields.RemoveRange(await db.CompetitionFields.Where(f => f.CompetitionId == competitionId).ToListAsync(ct));

        var competition = await db.Competitions.FirstAsync(c => c.Id == competitionId, ct);
        Cover(keys, competition.CoverImage);
        db.Competitions.Remove(competition);
    }

    /// <summary>
    /// Entries with their answers, files, notes and history. Anything that points at them from outside — a
    /// category win, a competition's overall winner — is released first, because those keys are Restrict.
    /// </summary>
    private async Task RemoveSubmissionsAsync(IReadOnlyCollection<int> ids, List<string> keys, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;

        db.CompetitionCategoryWinners.RemoveRange(
            await db.CompetitionCategoryWinners.Where(w => ids.Contains(w.SubmissionId)).ToListAsync(ct));
        // A place is emptied, never handed to the next work down: who wins is an admin's decision, and the results
        // screen then shows the gap.
        foreach (var result in await db.CompetitionResults
                     .Where(r => (r.OverallWinnerSubmissionId != null && ids.Contains(r.OverallWinnerSubmissionId.Value))
                                 || (r.SecondPlaceSubmissionId != null && ids.Contains(r.SecondPlaceSubmissionId.Value))
                                 || (r.ThirdPlaceSubmissionId != null && ids.Contains(r.ThirdPlaceSubmissionId.Value)))
                     .ToListAsync(ct))
        {
            for (var place = 1; place <= CompetitionResult.PlaceCount; place++)
                if (result.HolderOf(place) is { } held && ids.Contains(held))
                    result.SetPlace(place, null, null, null);
        }

        var submissions = await db.Submissions.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        foreach (var submission in submissions)
            submission.PreviewFileId = null; // released before its file row goes

        var files = await db.SubmissionFiles.Where(f => ids.Contains(f.SubmissionId)).ToListAsync(ct);
        foreach (var file in files)
            keys.AddRange(new[] { file.StoragePath, file.PublicAssetKey, file.ThumbnailKey }.OfType<string>());

        db.SubmissionFiles.RemoveRange(files);
        db.SubmissionAnswers.RemoveRange(await db.SubmissionAnswers.Where(a => ids.Contains(a.SubmissionId)).ToListAsync(ct));
        db.SubmissionNotes.RemoveRange(await db.SubmissionNotes.Where(n => ids.Contains(n.SubmissionId)).ToListAsync(ct));
        db.SubmissionStatusChanges.RemoveRange(await db.SubmissionStatusChanges.Where(h => ids.Contains(h.SubmissionId)).ToListAsync(ct));
        db.Submissions.RemoveRange(submissions);
    }

    // ---- Plumbing -------------------------------------------------------------------------------------------

    /// <summary>
    /// The rows go in one transaction, the audit entry with them. The stored files follow only once that has
    /// committed, so a file that cannot be deleted leaves an orphan on disk rather than a half-deleted database.
    /// </summary>
    private async Task<DeleteReport> RunAsync(DeleteImpactDto impact, Func<List<string>, Task> work, CancellationToken ct)
    {
        var keys = new List<string>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            keys.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await work(keys);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        var deleted = 0;
        var failed = 0;
        foreach (var key in keys.Distinct())
        {
            if (await StillReferencedAsync(key, ct))
            {
                logger.LogInformation("Stored file {Key} is still referenced and was kept", key);
                continue;
            }
            if (TryDelete(key)) deleted++;
            else failed++;
        }

        logger.LogInformation(
            "Permanently deleted {Title}: {Summary}; {Deleted} stored files removed, {Failed} failed",
            impact.Title, Summary(impact), deleted, failed);
        return new DeleteReport(impact, deleted, failed);
    }

    /// <summary>A stored file goes only when nothing left in the database points at it.</summary>
    private async Task<bool> StillReferencedAsync(string key, CancellationToken ct)
    {
        var url = CoverImages.UrlPrefix + key;
        return await db.SubmissionFiles.AnyAsync(
                   f => f.StoragePath == key || f.PublicAssetKey == key || f.ThumbnailKey == key, ct)
               || await db.Projects.AnyAsync(p => p.CoverImage == url, ct)
               || await db.Competitions.AnyAsync(c => c.CoverImage == url, ct)
               || await db.CompetitionResults.AnyAsync(r => r.CoverImage == url, ct)
               || await db.CompetitionCategories.AnyAsync(c => c.Image == url, ct);
    }

    private bool TryDelete(string key)
    {
        try
        {
            storage.Delete(key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning("Stored file {Key} could not be deleted: {Error}", key, ex.GetType().Name);
            return false;
        }
    }

    private static void Cover(List<string> keys, string? url)
    {
        if (url is not null && url.StartsWith(CoverImages.UrlPrefix, StringComparison.Ordinal))
            keys.Add(url[CoverImages.UrlPrefix.Length..]);
    }

    private static string Summary(DeleteImpactDto i) =>
        $"müsabiqə {i.Competitions}, təklif {i.Submissions}, kateqoriya {i.Categories}, sahə {i.Fields}, " +
        $"cavab {i.Answers}, fayl {i.Files}, nəticə {i.Results}, qalib {i.Winners}, qeyd {i.Notes}";
}
