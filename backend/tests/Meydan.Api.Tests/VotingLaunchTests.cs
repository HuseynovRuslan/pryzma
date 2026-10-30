using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Features.Admin;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// Starting the public vote from the admin's launch screen, for several competitions at once, each on its own ballot;
/// and moving a work an entrant sent to the wrong competition. The competitions here are built like the live
/// settlement ones: a closed entry, no nomination, the same form keys.
/// </summary>
public sealed class VotingLaunchTests : IDisposable
{
    private readonly MeydanFactory _factory = new() { Settings = { ["Email:OutboxWorkerEnabled"] = "false" } };

    public void Dispose() => _factory.Dispose();

    private sealed record Built(int Id, string Slug, Dictionary<string, int> Fields, int[] Final, int Draft, int Rejected);

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> Refused(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"expected {(int)expected}, got {(int)res.StatusCode}: {body}");
        return body;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    /// <summary>
    /// A published competition whose entry closed yesterday, with no nomination: three final works (one of them with
    /// a ready picture), a draft and a rejected one. <paramref name="keys"/> is its form; "nomination" is a switched-off
    /// Select, as on the live Novxanı form.
    /// </summary>
    private async Task<Built> CompetitionAsync(HttpClient admin, string slug, string[] keys, bool entryOpen = false)
    {
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var id = (await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = $"“{slug}” müsabiqəsi", slug, shortDescription = "Sınaq", hasCategories = false,
        }))).GetProperty("id").GetInt32();

        var now = _factory.Clock.Now;
        var fields = new Dictionary<string, int>();
        var final = new List<int>();
        int draft = 0, rejected = 0;
        await _factory.WithDbAsync(async db =>
        {
            var order = 0;
            foreach (var key in keys)
            {
                var f = new CompetitionField
                {
                    CompetitionId = id, Key = key, Label = key, SortOrder = order++,
                    FieldType = key switch { "files" => CompetitionFieldType.File, "nomination" => CompetitionFieldType.Select, _ => CompetitionFieldType.ShortText },
                    IsRequired = key == "title", IsSubmissionTitle = key == "title", IsActive = key != "nomination",
                    OptionsJson = key == "nomination" ? """{"options":["Loqo","Simvol"]}""" : null,
                };
                db.CompetitionFields.Add(f);
                await db.SaveChangesAsync();
                fields[key] = f.Id;
            }
            var c = await db.Competitions.SingleAsync(x => x.Id == id);
            c.Status = CompetitionStatus.Published;
            c.EndDate = entryOpen ? now.AddDays(3) : now.AddDays(-1);
            await db.SaveChangesAsync();

            Submission Add(int n, SubmissionStatus status)
            {
                var email = $"u-{Guid.NewGuid():N}@example.com";
                var user = new ApplicationUser
                {
                    Id = Guid.NewGuid(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
                    NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, FirstName = "Müəllif", LastName = $"{slug}{n}",
                    SecurityStamp = Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10),
                    TermsAcceptedAt = now.AddDays(-10),
                };
                db.Users.Add(user);
                return db.Submissions.Add(new Submission
                {
                    UserId = user.Id, CompetitionId = id, Title = $"{slug} işi {n}", Status = status,
                    ReferenceNumber = status == SubmissionStatus.Draft ? null : $"SUB-2098-{id:000}{n:00}",
                    SubmittedAt = status == SubmissionStatus.Draft ? null : now.AddDays(-2), CreatedAt = now.AddDays(-3), UpdatedAt = now.AddDays(-2),
                }).Entity;
            }

            var works = new[] { Add(1, SubmissionStatus.Submitted), Add(2, SubmissionStatus.Submitted), Add(3, SubmissionStatus.Approved) };
            var d = Add(4, SubmissionStatus.Draft);
            var r = Add(5, SubmissionStatus.Rejected);
            await db.SaveChangesAsync();
            foreach (var w in works)
            {
                db.SubmissionAnswers.Add(new SubmissionAnswer { SubmissionId = w.Id, CompetitionFieldId = fields["title"], Value = w.Title, FieldLabel = "İşin adı" });
                if (fields.TryGetValue("concept", out var concept))
                    db.SubmissionAnswers.Add(new SubmissionAnswer { SubmissionId = w.Id, CompetitionFieldId = concept, Value = "Konsept", FieldLabel = "Konsept" });
                if (fields.TryGetValue("nomination", out var nomination))
                    db.SubmissionAnswers.Add(new SubmissionAnswer { SubmissionId = w.Id, CompetitionFieldId = nomination, Value = null, FieldLabel = "Nominasiya" });
                if (fields.TryGetValue("files", out var files))
                    db.SubmissionFiles.Add(new SubmissionFile
                    {
                        SubmissionId = w.Id, CompetitionFieldId = files, OriginalFileName = "eskiz.png", StoragePath = $"2026/10/{Guid.NewGuid():N}.png",
                        ContentType = "image/png", SizeBytes = 100, UploadedAt = now.AddDays(-2),
                        PublicAssetStatus = w == works[0] ? PublicAssetStatus.Ready : PublicAssetStatus.Failed,
                        PublicAssetKey = w == works[0] ? $"derived/{Guid.NewGuid():N}.webp" : null,
                    });
            }
            await db.SaveChangesAsync();
            final.AddRange(works.Select(w => w.Id));
            draft = d.Id;
            rejected = r.Id;
        });
        return new Built(id, slug, fields, final.ToArray(), draft, rejected);
    }

    private static readonly string[] NovxaniForm = ["nomination", "title", "concept", "files", "notes"];
    private static readonly string[] MastagaForm = ["title", "concept", "files", "notes"];

    private static JsonElement Of(JsonElement launch, int id) =>
        launch.GetProperty("competitions").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == id);

    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    // ---- Starting the vote ---------------------------------------------------------------------

    [Fact]
    public async Task The_launch_screen_says_what_is_ready_and_why_the_rest_is_not()
    {
        var admin = await AdminAsync();
        var novxani = await CompetitionAsync(admin, "novxani-sinaq", NovxaniForm);
        var open = await CompetitionAsync(admin, "aciq-sinaq", MastagaForm, entryOpen: true);

        var screen = await Json(await admin.GetAsync("/api/admin/voting/launch"));
        var ready = Of(screen, novxani.Id);
        Assert.Empty(ready.GetProperty("problems").EnumerateArray());
        Assert.Equal("NotConfigured", ready.GetProperty("votingState").GetString());
        Assert.Equal(3, ready.GetProperty("finalWorks").GetInt32());
        Assert.Equal(0, ready.GetProperty("onBallot").GetInt32());
        Assert.Equal(2, ready.GetProperty("withoutPicture").GetInt32());
        Assert.True(ready.GetProperty("createsNomination").GetBoolean());
        Assert.Contains(Strings(ready.GetProperty("warnings")), w => w.Contains("2 işin saytda göstəriləcək şəkli hazır deyil"));

        Assert.Contains(Strings(Of(screen, open.Id).GetProperty("problems")), p => p.Contains("qəbulu hələ açıqdır"));

        await Refused(await _factory.CreateClient().GetAsync("/api/admin/voting/launch"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Starting_opens_every_chosen_vote_at_once_each_on_its_own_ballot()
    {
        var admin = await AdminAsync();
        var novxani = await CompetitionAsync(admin, "novxani-sinaq", NovxaniForm);
        var mastaga = await CompetitionAsync(admin, "mastaga-sinaq", MastagaForm);
        var now = _factory.Clock.Now;
        var endsAt = now.AddDays(3);

        var result = await Json(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { novxani.Id, mastaga.Id }, endsAt }));
        Assert.All(result.GetProperty("competitions").EnumerateArray(), c =>
        {
            Assert.Equal(3, c.GetProperty("onBallot").GetInt32());
            Assert.True(c.GetProperty("nominationCreated").GetBoolean());
        });

        await _factory.WithDbAsync(async db =>
        {
            foreach (var built in new[] { novxani, mastaga })
            {
                var c = await db.Competitions.SingleAsync(x => x.Id == built.Id);
                Assert.Equal(now, c.VotingStartsAt);
                Assert.Equal(endsAt, c.VotingEndsAt);
                var nomination = await db.CompetitionCategories.SingleAsync(k => k.CompetitionId == built.Id);
                Assert.Equal(VotingLaunch.NominationName, nomination.Name);
                Assert.True(nomination.IsActive);
                var placed = await db.Submissions.Where(s => s.CompetitionCategoryId == nomination.Id).Select(s => s.Id).OrderBy(x => x).ToListAsync();
                Assert.Equal(built.Final.Order(), placed);
                Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityId == built.Id.ToString()
                    && (a.Action == AuditActions.VotingLaunched || a.Action == AuditActions.VotingWindowChanged)));
            }
        });

        // Two ballots, two addresses, and the public list says both are open — what the site's banner reads.
        var visitor = _factory.CreateClient();
        foreach (var built in new[] { novxani, mastaga })
        {
            var summary = await Json(await visitor.GetAsync($"/api/competitions/{built.Slug}/voting"));
            Assert.Equal("Open", summary.GetProperty("state").GetString());
            var works = await Json(await visitor.GetAsync($"/api/competitions/{built.Slug}/voting/works"));
            Assert.Equal(3, works.GetProperty("total").GetInt32());
        }
        var list = (await Json(await visitor.GetAsync("/api/competitions"))).EnumerateArray()
            .Where(c => c.GetProperty("slug").GetString() is "novxani-sinaq" or "mastaga-sinaq")
            .Select(c => c.GetProperty("voting").GetString());
        Assert.All(list, v => Assert.Equal("Open", v));
    }

    [Fact]
    public async Task A_vote_is_not_started_twice_nor_with_entries_open_nor_to_end_in_the_past()
    {
        var admin = await AdminAsync();
        var novxani = await CompetitionAsync(admin, "novxani-sinaq", NovxaniForm);
        var open = await CompetitionAsync(admin, "aciq-sinaq", MastagaForm, entryOpen: true);
        var now = _factory.Clock.Now;

        Assert.Contains("\"endsAt\"", await Refused(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { novxani.Id }, endsAt = now.AddMinutes(-5) }), HttpStatusCode.BadRequest));
        Assert.Contains("qəbulu hələ açıqdır", await Refused(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { novxani.Id, open.Id }, endsAt = now.AddDays(2) }), HttpStatusCode.UnprocessableEntity));
        // All or nothing: the ready one did not open beside the refused one.
        Assert.Null(_factory.WithDb(db => db.Competitions.Single(c => c.Id == novxani.Id).VotingStartsAt));

        await Json(await admin.PostAsJsonAsync("/api/admin/voting/launch", new { competitionIds = new[] { novxani.Id }, endsAt = now.AddDays(2) }));
        Assert.Contains("artıq gedir", await Refused(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { novxani.Id }, endsAt = now.AddDays(4) }), HttpStatusCode.UnprocessableEntity));
        Assert.Equal(1, _factory.WithDb(db => db.CompetitionCategories.Count(k => k.CompetitionId == novxani.Id)));
    }

    // ---- Moving a work -------------------------------------------------------------------------

    [Fact]
    public async Task A_work_moves_with_its_answers_and_files_to_the_fields_of_the_same_keys()
    {
        var admin = await AdminAsync();
        var novxani = await CompetitionAsync(admin, "novxani-sinaq", NovxaniForm);
        var mastaga = await CompetitionAsync(admin, "mastaga-sinaq", MastagaForm);
        var work = novxani.Final[0];

        var options = await Json(await admin.GetAsync($"/api/admin/submissions/{work}/move"));
        Assert.Empty(options.GetProperty("blockers").EnumerateArray());
        var target = options.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == mastaga.Id);
        Assert.Empty(target.GetProperty("problems").EnumerateArray());

        var moved = await Json(await admin.PostAsJsonAsync($"/api/admin/submissions/{work}/move", new { competitionId = mastaga.Id }));
        Assert.Equal(mastaga.Id, moved.GetProperty("toCompetitionId").GetInt32());

        await _factory.WithDbAsync(async db =>
        {
            var s = await db.Submissions.SingleAsync(x => x.Id == work);
            Assert.Equal(mastaga.Id, s.CompetitionId);
            Assert.Equal($"SUB-2098-{novxani.Id:000}01", s.ReferenceNumber);
            var answers = await db.SubmissionAnswers.Where(a => a.SubmissionId == work).Select(a => a.CompetitionFieldId).OrderBy(x => x).ToListAsync();
            Assert.Equal(new[] { mastaga.Fields["title"], mastaga.Fields["concept"] }.Order(), answers);
            Assert.Equal(mastaga.Fields["files"], (await db.SubmissionFiles.SingleAsync(f => f.SubmissionId == work)).CompetitionFieldId);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.SubmissionMoved && a.EntityId == work.ToString()
                                                         && a.Summary!.Contains("novxani-sinaq → mastaga-sinaq")));
        });

        // Starting the vote places it on its new ballot, beside that competition's own works.
        await Json(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { mastaga.Id }, endsAt = _factory.Clock.Now.AddDays(2) }));
        var works = await Json(await _factory.CreateClient().GetAsync("/api/competitions/mastaga-sinaq/voting/works"));
        Assert.Equal(4, works.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_work_is_not_moved_into_a_form_without_its_fields_nor_once_its_vote_has_begun()
    {
        var admin = await AdminAsync();
        var novxani = await CompetitionAsync(admin, "novxani-sinaq", NovxaniForm);
        var thin = await CompetitionAsync(admin, "nazik-forma", ["title", "files"]);
        var mastaga = await CompetitionAsync(admin, "mastaga-sinaq", MastagaForm);
        var work = novxani.Final[0];

        var options = await Json(await admin.GetAsync($"/api/admin/submissions/{work}/move"));
        var problems = Strings(options.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == thin.Id).GetProperty("problems"));
        Assert.Contains(problems, p => p.Contains("(concept) sahəsi yoxdur"));
        await Refused(await admin.PostAsJsonAsync($"/api/admin/submissions/{work}/move", new { competitionId = thin.Id }), HttpStatusCode.Conflict);

        await Json(await admin.PostAsJsonAsync("/api/admin/voting/launch",
            new { competitionIds = new[] { novxani.Id }, endsAt = _factory.Clock.Now.AddDays(2) }));
        Assert.Contains("səsvermə başlayıb", await Refused(
            await admin.PostAsJsonAsync($"/api/admin/submissions/{work}/move", new { competitionId = mastaga.Id }), HttpStatusCode.Conflict));
        Assert.Equal(novxani.Id, _factory.WithDb(db => db.Submissions.Single(s => s.Id == work).CompetitionId));
    }
}
