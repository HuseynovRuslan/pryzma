using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>
/// Permanent deletion from the admin: a project, a competition or a single entry goes with everything that
/// belongs to it — and nothing that does not. Participants keep their accounts, neighbouring content keeps its
/// rows, stored files go only when no surviving row points at them, and the audit entry outlives its subject.
/// </summary>
public sealed class PermanentDeletionTests : IDisposable
{
    private const string Chevron = "baki-kendleri-sevron-dizayni";
    private static readonly byte[] Png = TestImages.Png();

    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private async Task<HttpClient> ParticipantAsync(string email)
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync(email));
        return client;
    }

    /// <summary>A complete chevron entry for the given settlement; returns its id.</summary>
    private static async Task<int> SubmitAsync(HttpClient client, string settlement, string title = "Şevron")
    {
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = Chevron, categorySlug = settlement }));
        var id = draft.GetProperty("id").GetInt32();
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}",
            new { answers = new { title, concept = "Konsept", symbols = "Simvollar" } }));

        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "eskiz.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        return id;
    }

    /// <summary>A second project with its own competition, so "everything else survives" has something to prove.</summary>
    private async Task<(int ProjectId, int CompetitionId)> NeighbourAsync(HttpClient admin)
    {
        var project = await Json(await admin.PostAsJsonAsync("/api/admin/projects", new
        {
            name = "Qonşu layihə", slug = "qonsu-layihe", shortDescription = "Toxunulmamalıdır.",
            description = "", accentColor = "#204060", sortOrder = 9,
        }));
        var competition = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId = project.GetProperty("id").GetInt32(),
            title = "Qonşu müsabiqə", slug = "qonsu-musabiqe", shortDescription = "Toxunulmamalıdır.",
            description = "", rules = "", requirements = "", prize = "",
            hasCategories = false, categoryLabel = "", maxSubmissionsPerUser = (int?)null, sortOrder = 1,
        }));
        return (project.GetProperty("id").GetInt32(), competition.GetProperty("id").GetInt32());
    }

    private Task<Guid> IdOfAsync(string email) =>
        _factory.QueryDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());

    /// <summary>
    /// A vote by the account with this e-mail for the entry, written as the ballot writes one; set aside by the admin
    /// when <paramref name="setAside"/> — the query filter then hides it from ordinary reads.
    /// </summary>
    private Task VoteAsync(string voterEmail, int submissionId, bool setAside = false) =>
        _factory.WithDbAsync(async db =>
        {
            var voter = await db.Users.Where(u => u.Email == voterEmail).Select(u => u.Id).FirstAsync();
            var admin = await db.Users.Where(u => u.Email == MeydanFactory.AdminEmail).Select(u => u.Id).FirstAsync();
            var category = await db.Submissions.Where(s => s.Id == submissionId).Select(s => s.CompetitionCategoryId).FirstAsync();
            var now = _factory.Clock.Now;
            db.Votes.Add(new Vote
            {
                UserId = voter, SubmissionId = submissionId, CompetitionCategoryId = category!.Value, CreatedAt = now.AddHours(-2),
                InvalidatedAt = setAside ? now.AddHours(-1) : null, InvalidatedById = setAside ? admin : null,
                InvalidationReason = setAside ? "D12: sınaq üçün təkrarlanan telefon" : null,
            });
            await db.SaveChangesAsync();
        });

    /// <summary>Every vote and every audit entry, as text: what a refused deletion must leave exactly as it was.</summary>
    private Task<string> VotesAndAuditAsync() =>
        _factory.QueryDbAsync(async db =>
        {
            var votes = await db.Votes.IgnoreQueryFilters().OrderBy(v => v.Id)
                .Select(v => new { v.Id, v.UserId, v.SubmissionId, v.CompetitionCategoryId, v.CreatedAt, v.InvalidatedAt, v.InvalidatedById, v.InvalidationReason })
                .ToListAsync();
            var audit = await db.AuditLogs.OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.Summary, a.CreatedAt })
                .ToListAsync();
            return string.Join("\n", votes.Select(v =>
                       $"{v.Id}|{v.UserId}|{v.SubmissionId}|{v.CompetitionCategoryId}|{v.CreatedAt:O}|{v.InvalidatedAt:O}|{v.InvalidatedById}|{v.InvalidationReason}"))
                   + "\n--\n"
                   + string.Join("\n", audit.Select(a => $"{a.Id}|{a.Action}|{a.EntityType}|{a.EntityId}|{a.Summary}|{a.CreatedAt:O}"));
        });

    /// <summary>The dialog's question and the deletion itself are both refused, in the admin's own words.</summary>
    private static async Task AssertVoterRefusedAsync(HttpClient admin, Guid id)
    {
        foreach (var res in new[]
                 {
                     await admin.GetAsync($"/api/admin/participants/{id}/delete-impact"),
                     await admin.DeleteAsync($"/api/admin/participants/{id}/permanent"),
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(PermanentDeletion.VotedAccount, problem.GetProperty("title").GetString());
        }
    }

    private string PathOf(string key) =>
        Path.Combine(_factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar));

    // ---- Impact -------------------------------------------------------------------------------

    [Fact]
    public async Task The_numbers_the_dialog_shows_are_counted_on_the_server()
    {
        var participant = await ParticipantAsync("impact@example.com");
        await SubmitAsync(participant, "balaxani");
        await SubmitAsync(participant, "ramana");
        var admin = await AdminAsync();
        var competitionId = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).FirstAsync());
        var projectId = await _factory.QueryDbAsync(db => db.Projects.Where(p => p.Slug == DbSeeder.BakiProjectSlug).Select(p => p.Id).FirstAsync());

        var project = await Json(await admin.GetAsync($"/api/admin/projects/{projectId}/delete-impact"));
        Assert.Equal("Bakı kəndləri", project.GetProperty("title").GetString());
        Assert.Equal(1, project.GetProperty("competitions").GetInt32());
        Assert.Equal(2, project.GetProperty("submissions").GetInt32());
        Assert.Equal(2, project.GetProperty("files").GetInt32());
        Assert.True(project.GetProperty("categories").GetInt32() > 0);
        Assert.True(project.GetProperty("fields").GetInt32() > 0);
        Assert.True(project.GetProperty("requiresTypedConfirmation").GetBoolean());

        var competition = await Json(await admin.GetAsync($"/api/admin/competitions/{competitionId}/delete-impact"));
        Assert.Equal("Bakı kəndləri", competition.GetProperty("context").GetString()); // the project it belongs to
        Assert.Equal(0, competition.GetProperty("competitions").GetInt32()); // the competition itself is not counted
        Assert.Equal(2, competition.GetProperty("submissions").GetInt32());

        // An untouched, empty project asks for no typed word.
        var (neighbourId, _) = await NeighbourAsync(admin);
        var neighbour = await Json(await admin.GetAsync($"/api/admin/projects/{neighbourId}/delete-impact"));
        Assert.Equal(1, neighbour.GetProperty("competitions").GetInt32());
        Assert.True(neighbour.GetProperty("requiresTypedConfirmation").GetBoolean());
    }

    // ---- Project ------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_project_takes_its_competitions_and_entries_and_nothing_else()
    {
        var participant = await ParticipantAsync("project-delete@example.com");
        var kept = await SubmitAsync(participant, "balaxani");
        var admin = await AdminAsync();
        var (neighbourProject, neighbourCompetition) = await NeighbourAsync(admin);
        var projectId = await _factory.QueryDbAsync(db => db.Projects.Where(p => p.Slug == DbSeeder.BakiProjectSlug).Select(p => p.Id).FirstAsync());
        var storedKeys = await _factory.QueryDbAsync(db => db.SubmissionFiles.Select(f => f.StoragePath).ToListAsync());

        var report = await Json(await admin.DeleteAsync($"/api/admin/projects/{projectId}/permanent"));
        Assert.Equal(1, report.GetProperty("removed").GetProperty("competitions").GetInt32());
        Assert.Equal(1, report.GetProperty("removed").GetProperty("submissions").GetInt32());

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Projects.AnyAsync(p => p.Id == projectId));
            Assert.False(await db.Competitions.AnyAsync(c => c.Slug == Chevron));
            Assert.False(await db.Submissions.AnyAsync(s => s.Id == kept));
            Assert.Empty(await db.SubmissionAnswers.Where(a => a.SubmissionId == kept).ToListAsync());
            Assert.Empty(await db.SubmissionFiles.Where(f => f.SubmissionId == kept).ToListAsync());
            Assert.Empty(await db.CompetitionCategories.ToListAsync());
            Assert.Empty(await db.CompetitionFields.Where(f => f.CompetitionId != neighbourCompetition).ToListAsync());

            // Everything outside the tree is where it was.
            Assert.True(await db.Projects.AnyAsync(p => p.Id == neighbourProject));
            Assert.True(await db.Competitions.AnyAsync(c => c.Id == neighbourCompetition));
            Assert.True(await db.Users.AnyAsync(u => u.Email == "project-delete@example.com"));
        });
        foreach (var key in storedKeys)
            Assert.False(File.Exists(PathOf(key)), $"{key} is still on disk");
    }

    // ---- Competition --------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_competition_leaves_its_project_and_its_neighbours_standing()
    {
        var participant = await ParticipantAsync("competition-delete@example.com");
        var entry = await SubmitAsync(participant, "balaxani");
        var admin = await AdminAsync();
        var (_, neighbourCompetition) = await NeighbourAsync(admin);
        var competitionId = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).FirstAsync());
        var projectId = await _factory.QueryDbAsync(db => db.Projects.Where(p => p.Slug == DbSeeder.BakiProjectSlug).Select(p => p.Id).FirstAsync());

        await Json(await admin.DeleteAsync($"/api/admin/competitions/{competitionId}/permanent"));

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Competitions.AnyAsync(c => c.Id == competitionId));
            Assert.False(await db.Submissions.AnyAsync(s => s.Id == entry));
            Assert.Empty(await db.CompetitionCategories.Where(c => c.CompetitionId == competitionId).ToListAsync());
            Assert.Empty(await db.CompetitionFields.Where(f => f.CompetitionId == competitionId).ToListAsync());

            Assert.True(await db.Projects.AnyAsync(p => p.Id == projectId)); // the parent is not ours to delete
            Assert.True(await db.Competitions.AnyAsync(c => c.Id == neighbourCompetition));
            Assert.True(await db.Users.AnyAsync(u => u.Email == "competition-delete@example.com"));
        });
    }

    [Fact]
    public async Task A_competition_goes_even_when_it_has_a_published_result_and_winners()
    {
        var participant = await ParticipantAsync("winner-delete@example.com");
        var winner = await SubmitAsync(participant, "balaxani");
        var admin = await AdminAsync();
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{winner}/status",
            new { status = "Approved", comment = "" }));
        var competitionId = await _factory.QueryDbAsync(db => db.Competitions.Where(c => c.Slug == Chevron).Select(c => c.Id).FirstAsync());
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/result/winners", new { submissionId = winner }));
        Assert.True(await _factory.QueryDbAsync(db => db.CompetitionCategoryWinners.AnyAsync()));

        await Json(await admin.DeleteAsync($"/api/admin/competitions/{competitionId}/permanent"));

        await _factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.CompetitionCategoryWinners.ToListAsync());
            Assert.Empty(await db.CompetitionResults.Where(r => r.CompetitionId == competitionId).ToListAsync());
            Assert.False(await db.Competitions.AnyAsync(c => c.Id == competitionId));
        });
    }

    // ---- Submission ---------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_entry_leaves_the_participant_and_their_other_work()
    {
        var participant = await ParticipantAsync("entry-delete@example.com");
        var going = await SubmitAsync(participant, "balaxani", "Gedən");
        var staying = await SubmitAsync(participant, "ramana", "Qalan");
        var admin = await AdminAsync();
        await Json(await admin.PostAsJsonAsync($"/api/admin/submissions/{going}/notes", new { text = "Qeyd" }));
        var key = await _factory.QueryDbAsync(db => db.SubmissionFiles.Where(f => f.SubmissionId == going).Select(f => f.StoragePath).FirstAsync());

        var impact = await Json(await admin.GetAsync($"/api/admin/submissions/{going}/delete-impact"));
        Assert.StartsWith("SUB-", impact.GetProperty("title").GetString());
        Assert.Equal(1, impact.GetProperty("files").GetInt32());
        Assert.Equal(1, impact.GetProperty("notes").GetInt32());

        await Json(await admin.DeleteAsync($"/api/admin/submissions/{going}/permanent"));

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Submissions.AnyAsync(s => s.Id == going));
            Assert.Empty(await db.SubmissionAnswers.Where(a => a.SubmissionId == going).ToListAsync());
            Assert.Empty(await db.SubmissionFiles.Where(f => f.SubmissionId == going).ToListAsync());
            Assert.Empty(await db.SubmissionNotes.Where(n => n.SubmissionId == going).ToListAsync());
            Assert.Empty(await db.SubmissionStatusChanges.Where(h => h.SubmissionId == going).ToListAsync());

            Assert.True(await db.Submissions.AnyAsync(s => s.Id == staying));
            Assert.True(await db.Users.AnyAsync(u => u.Email == "entry-delete@example.com"));
            Assert.True(await db.Competitions.AnyAsync(c => c.Slug == Chevron));
        });
        Assert.False(File.Exists(PathOf(key)));
    }

    // ---- Accounts -----------------------------------------------------------------------------

    [Fact]
    public async Task An_account_that_holds_nothing_can_be_removed_and_the_rest_stay()
    {
        var going = await ParticipantAsync("empty-account@example.com");
        var staying = await ParticipantAsync("has-work@example.com");
        await SubmitAsync(staying, "balaxani");
        var admin = await AdminAsync();
        var (goingId, stayingId) = await _factory.QueryDbAsync(async db => (
            await db.Users.Where(u => u.Email == "empty-account@example.com").Select(u => u.Id).FirstAsync(),
            await db.Users.Where(u => u.Email == "has-work@example.com").Select(u => u.Id).FirstAsync()));

        var impact = await Json(await admin.GetAsync($"/api/admin/participants/{goingId}/delete-impact"));
        Assert.Equal("empty-account@example.com", impact.GetProperty("context").GetString());
        Assert.Equal(0, impact.GetProperty("submissions").GetInt32());
        Assert.True(impact.GetProperty("requiresTypedConfirmation").GetBoolean()); // an account is a person

        await Json(await admin.DeleteAsync($"/api/admin/participants/{goingId}/permanent"));

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == goingId));
            Assert.Empty(await db.RefreshTokens.Where(t => t.UserId == goingId).ToListAsync()); // sessions go too
            Assert.True(await db.Users.AnyAsync(u => u.Id == stayingId)); // the neighbour is untouched
            var entry = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.ParticipantPermanentlyDeleted);
            Assert.Equal(AuditEntities.Account, entry.EntityType);
            Assert.Contains("empty-account@example.com", entry.Summary); // still says who, after they are gone
        });
    }

    [Fact]
    public async Task An_account_with_work_an_admin_and_the_admins_own_are_all_refused()
    {
        var participant = await ParticipantAsync("keeps-work@example.com");
        await SubmitAsync(participant, "balaxani");
        var admin = await AdminAsync();
        var (withWork, adminId) = await _factory.QueryDbAsync(async db => (
            await db.Users.Where(u => u.Email == "keeps-work@example.com").Select(u => u.Id).FirstAsync(),
            await db.Users.Where(u => u.Email == MeydanFactory.AdminEmail).Select(u => u.Id).FirstAsync()));

        // Work is deleted through the entry it belongs to, never sideways through a name in a list.
        var hasWork = await admin.DeleteAsync($"/api/admin/participants/{withWork}/permanent");
        Assert.Equal(HttpStatusCode.Conflict, hasWork.StatusCode);
        Assert.Contains("təklif", await hasWork.Content.ReadAsStringAsync());

        var self = await admin.DeleteAsync($"/api/admin/participants/{adminId}/permanent");
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Contains("Öz hesabınızı", await self.Content.ReadAsStringAsync());

        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Id == withWork));
            Assert.True(await db.Users.AnyAsync(u => u.Id == adminId));
            Assert.Empty(await db.AuditLogs.Where(a => a.Action == AuditActions.ParticipantPermanentlyDeleted).ToListAsync());
        });
    }

    // ---- Accounts that voted: never removed (temporary application guard until Phase21) ----------

    [Fact]
    public async Task An_account_with_a_counted_vote_cannot_be_deleted()
    {
        var entry = await SubmitAsync(await ParticipantAsync("author@example.com"), "balaxani");
        await ParticipantAsync("voter@example.com");
        await VoteAsync("voter@example.com", entry);
        var voter = await IdOfAsync("voter@example.com");
        var admin = await AdminAsync();

        await AssertVoterRefusedAsync(admin, voter);

        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Id == voter));
            var vote = await db.Votes.SingleAsync(v => v.UserId == voter);
            Assert.Equal(entry, vote.SubmissionId);
            Assert.Null(vote.InvalidatedAt);
            Assert.False(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.ParticipantPermanentlyDeleted));
        });
    }

    [Fact]
    public async Task An_account_whose_only_vote_was_set_aside_cannot_be_deleted_either()
    {
        var entry = await SubmitAsync(await ParticipantAsync("author@example.com"), "balaxani");
        await ParticipantAsync("set-aside@example.com");
        await VoteAsync("set-aside@example.com", entry, setAside: true);
        var voter = await IdOfAsync("set-aside@example.com");
        var admin = await AdminAsync();
        // Hidden from ordinary reads, still a vote.
        Assert.False(await _factory.QueryDbAsync(db => db.Votes.AnyAsync(v => v.UserId == voter)));

        await AssertVoterRefusedAsync(admin, voter);

        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Id == voter));
            var vote = await db.Votes.IgnoreQueryFilters().SingleAsync(v => v.UserId == voter);
            Assert.NotNull(vote.InvalidatedAt);
            Assert.Equal("D12: sınaq üçün təkrarlanan telefon", vote.InvalidationReason);
        });
    }

    [Fact]
    public async Task A_refused_deletion_leaves_every_vote_and_audit_entry_exactly_as_it_was()
    {
        var entry = await SubmitAsync(await ParticipantAsync("author@example.com"), "balaxani");
        await ParticipantAsync("counted@example.com");
        await ParticipantAsync("set-aside@example.com");
        await VoteAsync("counted@example.com", entry);
        await VoteAsync("set-aside@example.com", entry, setAside: true);
        var admin = await AdminAsync();
        var before = await VotesAndAuditAsync();

        foreach (var email in new[] { "counted@example.com", "set-aside@example.com" })
            await AssertVoterRefusedAsync(admin, await IdOfAsync(email));

        Assert.Equal(before, await VotesAndAuditAsync());
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.Votes.IgnoreQueryFilters().CountAsync()));

        // The list never offers the button for either, and says why.
        var page = await Json(await admin.GetAsync("/api/admin/participants?pageSize=100"));
        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("email").GetString() ?? "", x => x);
        foreach (var email in new[] { "counted@example.com", "set-aside@example.com" })
        {
            Assert.True(rows[email].GetProperty("hasVotes").GetBoolean());
            Assert.False(rows[email].GetProperty("canDelete").GetBoolean());
        }
    }

    [Fact]
    public async Task An_account_without_a_vote_follows_the_existing_rules()
    {
        // The guard is about votes only: an account that holds nothing still goes...
        var entry = await SubmitAsync(await ParticipantAsync("author@example.com"), "balaxani");
        await ParticipantAsync("never-voted@example.com");
        await ParticipantAsync("voter@example.com");
        await VoteAsync("voter@example.com", entry);
        var admin = await AdminAsync();
        var page = await Json(await admin.GetAsync("/api/admin/participants?pageSize=100"));
        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("email").GetString() ?? "", x => x);
        Assert.False(rows["never-voted@example.com"].GetProperty("hasVotes").GetBoolean());
        Assert.True(rows["never-voted@example.com"].GetProperty("canDelete").GetBoolean());

        var clean = await IdOfAsync("never-voted@example.com");
        await Json(await admin.GetAsync($"/api/admin/participants/{clean}/delete-impact"));
        await Json(await admin.DeleteAsync($"/api/admin/participants/{clean}/permanent"));

        // ...and the earlier refusals stand, with their own words: work, an admin, the admin's own account.
        var author = await IdOfAsync("author@example.com");
        var adminId = await IdOfAsync(MeydanFactory.AdminEmail);
        var hasWork = await admin.DeleteAsync($"/api/admin/participants/{author}/permanent");
        Assert.Equal(HttpStatusCode.Conflict, hasWork.StatusCode);
        Assert.Contains("təklif", await hasWork.Content.ReadAsStringAsync());
        var self = await admin.DeleteAsync($"/api/admin/participants/{adminId}/permanent");
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        Assert.Contains("Öz hesabınızı", await self.Content.ReadAsStringAsync());

        // An entrant who also voted is told about the vote: deleting the work would not let the account go anyway.
        await VoteAsync("author@example.com", entry);
        await AssertVoterRefusedAsync(admin, author);

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == clean));
            Assert.True(await db.Users.AnyAsync(u => u.Id == author));
            Assert.Equal(2, await db.Votes.CountAsync());
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditActions.ParticipantPermanentlyDeleted));
        });
    }

    [Fact]
    public async Task The_listing_says_which_accounts_may_be_removed()
    {
        await ParticipantAsync("clean@example.com");
        var busy = await ParticipantAsync("busy@example.com");
        await SubmitAsync(busy, "balaxani");
        var admin = await AdminAsync();

        var page = await Json(await admin.GetAsync("/api/admin/participants?pageSize=100"));
        var rows = page.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("email").GetString() ?? "", x => x);

        Assert.True(rows["clean@example.com"].GetProperty("canDelete").GetBoolean());
        Assert.False(rows["busy@example.com"].GetProperty("canDelete").GetBoolean());
        Assert.False(rows[MeydanFactory.AdminEmail].GetProperty("canDelete").GetBoolean());
    }

    // ---- Stored files -------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_file_another_row_still_points_at_is_left_on_disk()
    {
        var participant = await ParticipantAsync("shared-file@example.com");
        var going = await SubmitAsync(participant, "balaxani");
        var staying = await SubmitAsync(participant, "ramana");
        var admin = await AdminAsync();

        // The two entries end up sharing one stored object — the case the cleanup has to notice.
        var shared = await _factory.QueryDbAsync(db => db.SubmissionFiles.Where(f => f.SubmissionId == going).Select(f => f.StoragePath).FirstAsync());
        var ownKey = await _factory.QueryDbAsync(db => db.SubmissionFiles.Where(f => f.SubmissionId == staying).Select(f => f.ThumbnailKey ?? f.StoragePath).FirstAsync());
        await _factory.WithDbAsync(async db =>
        {
            var other = await db.SubmissionFiles.FirstAsync(f => f.SubmissionId == staying);
            other.StoragePath = shared; // now two rows name one stored object
            await db.SaveChangesAsync();
        });

        // Its own derivatives are nobody else's and go with it.
        var derivatives = await _factory.QueryDbAsync(db => db.SubmissionFiles
            .Where(f => f.SubmissionId == going)
            .Select(f => new { f.PublicAssetKey, f.ThumbnailKey }).ToListAsync());
        var own = derivatives.SelectMany(d => new[] { d.PublicAssetKey, d.ThumbnailKey }).OfType<string>().Distinct().ToList();

        var report = await Json(await admin.DeleteAsync($"/api/admin/submissions/{going}/permanent"));

        Assert.Equal(0, report.GetProperty("storedFilesFailed").GetInt32());
        Assert.True(File.Exists(PathOf(shared)), "a file the surviving entry still points at was deleted");
        Assert.True(File.Exists(PathOf(ownKey)), "the surviving entry lost its own file");
        foreach (var key in own)
            Assert.False(File.Exists(PathOf(key)), $"{key} belonged only to the deleted entry");
    }

    // ---- Audit --------------------------------------------------------------------------------

    [Fact]
    public async Task The_audit_entry_outlives_what_it_describes()
    {
        var participant = await ParticipantAsync("audit-delete@example.com");
        await SubmitAsync(participant, "balaxani");
        var admin = await AdminAsync();
        var projectId = await _factory.QueryDbAsync(db => db.Projects.Where(p => p.Slug == DbSeeder.BakiProjectSlug).Select(p => p.Id).FirstAsync());
        var before = await _factory.QueryDbAsync(db => db.AuditLogs.CountAsync());

        await Json(await admin.DeleteAsync($"/api/admin/projects/{projectId}/permanent"));

        await _factory.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditLogs.CountAsync() > before); // history is added to, never cleared
            var entry = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.ProjectPermanentlyDeleted);
            Assert.Equal(AuditEntities.Project, entry.EntityType);
            Assert.Equal(projectId.ToString(), entry.EntityId);
            Assert.NotNull(entry.UserId); // the admin who did it
            Assert.Contains(DbSeeder.BakiProjectSlug, entry.Summary);
            Assert.Contains("təklif 1", entry.Summary); // the dependency counts, kept for after the rows are gone
            Assert.False(await db.Projects.AnyAsync(p => p.Id == projectId));
        });
    }

    // ---- The seeder ---------------------------------------------------------------------------

    [Fact]
    public async Task Deleted_seeded_content_does_not_come_back_on_the_next_start()
    {
        var admin = await AdminAsync();
        var projectId = await _factory.QueryDbAsync(db => db.Projects.Where(p => p.Slug == DbSeeder.BakiProjectSlug).Select(p => p.Id).FirstAsync());
        await Json(await admin.DeleteAsync($"/api/admin/projects/{projectId}/permanent"));

        // Exactly what a restart or a deploy does.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();

        await _factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Projects.AnyAsync(p => p.Slug == DbSeeder.BakiProjectSlug));
            Assert.False(await db.Competitions.AnyAsync(c => c.Slug == Chevron));
            // The seed history is what holds it back, and it is left alone.
            Assert.True(await db.SeedHistory.AnyAsync(h => h.Key == "2026-09-18-baki-project"));
        });
    }

    // ---- Authorisation ------------------------------------------------------------------------

    public static TheoryData<string, string> DeleteEndpoints => new()
    {
        { "GET", "/api/admin/projects/1/delete-impact" },
        { "DELETE", "/api/admin/projects/1/permanent" },
        { "GET", "/api/admin/competitions/1/delete-impact" },
        { "DELETE", "/api/admin/competitions/1/permanent" },
        { "GET", "/api/admin/submissions/1/delete-impact" },
        { "DELETE", "/api/admin/submissions/1/permanent" },
        { "GET", "/api/admin/participants/00000000-0000-0000-0000-000000000001/delete-impact" },
        { "DELETE", "/api/admin/participants/00000000-0000-0000-0000-000000000001/permanent" },
    };

    [Theory]
    [MemberData(nameof(DeleteEndpoints))]
    public async Task Only_an_admin_may_reach_these(string method, string url)
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode);

        var participant = await ParticipantAsync($"forbidden-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await participant.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode);
    }

    [Fact]
    public async Task What_is_not_there_is_404()
    {
        var admin = await AdminAsync();
        foreach (var url in new[]
                 {
                     "/api/admin/projects/987654/delete-impact", "/api/admin/projects/987654/permanent",
                     "/api/admin/competitions/987654/delete-impact", "/api/admin/competitions/987654/permanent",
                     "/api/admin/submissions/987654/delete-impact", "/api/admin/submissions/987654/permanent",
                 })
        {
            var response = url.EndsWith("permanent", StringComparison.Ordinal)
                ? await admin.DeleteAsync(url)
                : await admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
