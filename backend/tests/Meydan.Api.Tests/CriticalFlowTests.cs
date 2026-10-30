using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The one journey the platform exists for, walked end to end on each competition that is actually published: a
/// stranger registers, enters, uploads a file, submits, sees their work in "İşlərim", and an admin then finds that
/// same work, sees who sent it, opens what they sent and moves it through moderation — with the participant seeing
/// the new status afterwards.
///
/// The pieces are covered in detail elsewhere (<see cref="SubmissionTests"/>, <see cref="AdminTests"/>). What is
/// asserted here is that they hold together, in the order a real person meets them: the failure this catches is not
/// a broken function but a broken seam.
/// </summary>
public sealed class CriticalFlowTests : IDisposable
{
    private readonly MeydanFactory _factory = new() { LaunchContent = true };

    public void Dispose() => _factory.Dispose();

    public static TheoryData<string, string> LiveCompetitions => new()
    {
        { DbSeeder.NovxaniCompetitionSlug, "Novxanı" },
        { DbSeeder.EmircanCompetitionSlug, "Əmircan" },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Errors(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

    private async Task<HttpClient> AdminAsync()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));
        return admin;
    }

    private sealed record Participant(HttpClient Client, string Email, string Phone)
    {
        public const string Password = "secret123";
        public const string FirstName = "Qa";
        public const string LastName = "Sınaqlı";
        public static string FullName => $"{FirstName} {LastName}";
    }

    /// <summary>A participant whose details are known exactly, so "the admin sees them" can be asserted, not eyeballed.</summary>
    private async Task<Participant> NewParticipantAsync(string tag)
    {
        var email = $"qa-{tag}-{Guid.NewGuid():N}@example.com";
        var phone = TestPhones.Next();
        var client = _factory.CreateClient();
        var registered = await Json(await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = Participant.FirstName, lastName = Participant.LastName, phone, email,
            password = Participant.Password, confirmPassword = Participant.Password, acceptTerms = true,
        }));
        client.UseToken(registered.GetProperty("accessToken").GetString()!);
        return new Participant(client, email, phone);
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, int id, string fileName, byte[] bytes, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return client.PostAsync($"/api/submissions/{id}/files", form);
    }

    // ---- The whole journey, per competition ---------------------------------------------------

    [Theory]
    [MemberData(nameof(LiveCompetitions))]
    public async Task A_stranger_enters_and_the_admin_finds_exactly_what_they_sent(string slug, string settlement)
    {
        var participant = await NewParticipantAsync(settlement == "Novxanı" ? "novxani" : "emircan");
        var client = participant.Client;

        // Registering gives a participant, never an admin.
        var me = await Json(await client.GetAsync("/api/auth/me"));
        Assert.Equal(participant.Email, me.GetProperty("email").GetString());
        Assert.Equal(participant.Phone, me.GetProperty("phone").GetString());
        Assert.Equal(["Participant"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/submissions")).StatusCode);

        // The competition is open, which is what makes the "İştirak et" button real.
        var competition = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{slug}"));
        Assert.Equal("Open", competition.GetProperty("phase").GetString());

        // The entry itself, on the form the launch actually ships.
        var draft = await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = slug }));
        var id = draft.GetProperty("id").GetInt32();
        Assert.Equal(
            ["nomination", "title", "concept", "symbols", "files", "notes"],
            draft.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToArray());

        var answers = new
        {
            nomination = "Simvol və Loqotip Dizaynı",
            title = $"{settlement} üçün simvol",
            concept = $"{settlement} qəsəbəsinin dəniz və daş kimliyini birləşdirən simvol.",
            symbols = "Xilə-buta, əhəngdaşı faktura, dalğa xətti.",
            notes = "Fayllar 300 dpi-dır.",
        };
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new { answers }));
        var uploaded = await Json(await UploadAsync(client, id, "eskiz.png", TestImages.Png()));
        var fileId = uploaded.GetProperty("id").GetInt32();

        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));
        var reference = submitted.GetProperty("referenceNumber").GetString()!;
        Assert.StartsWith("SUB-", reference);
        Assert.Equal("Submitted", submitted.GetProperty("status").GetString());

        // A second click on the same button must not make a second entry or a second reference.
        var userId = Guid.Parse(me.GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/submissions/{id}/submit", null)).StatusCode);
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Submissions.CountAsync(s => s.UserId == userId)));
        Assert.Equal(reference, await _factory.QueryDbAsync(db =>
            db.Submissions.Where(s => s.Id == id).Select(s => s.ReferenceNumber).SingleAsync()));

        // "İşlərim": their own work, with the reference and the competition it belongs to.
        var mine = await Json(await client.GetAsync("/api/me/submissions"));
        var row = mine.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal(reference, row.GetProperty("referenceNumber").GetString());
        Assert.Equal("Submitted", row.GetProperty("status").GetString());
        Assert.Equal(slug, row.GetProperty("competitionSlug").GetString());
        Assert.Equal($"{settlement} üçün simvol", row.GetProperty("title").GetString());
        Assert.False(row.GetProperty("isEditable").GetBoolean());

        // And the same work, as the admin meets it.
        var admin = await AdminAsync();
        var listed = await Json(await admin.GetAsync($"/api/admin/submissions?q={Uri.EscapeDataString(reference)}"));
        var found = listed.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(reference, found.GetProperty("referenceNumber").GetString());
        Assert.Equal(Participant.FullName, found.GetProperty("participant").GetProperty("name").GetString());
        Assert.Equal(participant.Email, found.GetProperty("participant").GetProperty("email").GetString());
        Assert.Equal(1, found.GetProperty("fileCount").GetInt32());

        var detail = await Json(await admin.GetAsync($"/api/admin/submissions/{id}"));
        var who = detail.GetProperty("participant");
        Assert.Equal(Participant.FirstName, who.GetProperty("firstName").GetString());
        Assert.Equal(Participant.LastName, who.GetProperty("lastName").GetString());
        Assert.Equal(participant.Email, who.GetProperty("email").GetString());  // exactly what was registered,
        Assert.Equal(participant.Phone, who.GetProperty("phone").GetString());  // not a dash standing in for it
        Assert.Equal(slug, detail.GetProperty("competitionSlug").GetString());

        var shown = detail.GetProperty("answers").EnumerateArray()
            .ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value").GetString());
        Assert.Equal(answers.nomination, shown["nomination"]);
        Assert.Equal(answers.title, shown["title"]);
        Assert.Equal(answers.concept, shown["concept"]);
        Assert.Equal(answers.symbols, shown["symbols"]);
        Assert.Equal(answers.notes, shown["notes"]);
        Assert.Equal("eskiz.png",
            detail.GetProperty("files").EnumerateArray().Single().GetProperty("originalFileName").GetString());

        // The admin can open what was sent; nobody else can.
        var opened = await admin.GetAsync($"/api/submissions/{id}/files/{fileId}");
        Assert.True(opened.IsSuccessStatusCode, $"admin could not open the file: {(int)opened.StatusCode}");
        Assert.Equal("image/png", opened.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await opened.Content.ReadAsByteArrayAsync());

        var stranger = await NewParticipantAsync("stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/submissions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.Client.GetAsync($"/api/submissions/{id}/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync($"/api/submissions/{id}/files/{fileId}")).StatusCode);

        // Moderation, and the participant seeing it.
        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status",
            new { status = "UnderReview", comment = "Baxılır" }));
        var afterReview = await Json(await client.GetAsync("/api/me/submissions"));
        Assert.Equal("UnderReview", afterReview.EnumerateArray()
            .Single(x => x.GetProperty("id").GetInt32() == id).GetProperty("status").GetString());

        await Json(await admin.PutAsJsonAsync($"/api/admin/submissions/{id}/status", new { status = "Approved" }));
        var afterApproval = await Json(await client.GetAsync("/api/me/submissions"));
        Assert.Equal("Approved", afterApproval.EnumerateArray()
            .Single(x => x.GetProperty("id").GetInt32() == id).GetProperty("status").GetString());
    }

    [Fact]
    public async Task One_person_can_enter_both_competitions_and_each_entry_stays_its_own()
    {
        var participant = await NewParticipantAsync("both");
        var client = participant.Client;

        var references = new Dictionary<string, string>();
        foreach (var (slug, settlement) in new[]
                 {
                     (DbSeeder.NovxaniCompetitionSlug, "Novxanı"), (DbSeeder.EmircanCompetitionSlug, "Əmircan"),
                 })
        {
            var id = (await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = slug })))
                .GetProperty("id").GetInt32();
            await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
            {
                answers = new
                {
                    nomination = "Məkan və Küçə Tərtibatı", title = $"{settlement} divar rəsmi",
                    concept = "Küçə divarı üçün mural konsepti.", symbols = "Ornament və dalğa.",
                },
            }));
            (await UploadAsync(client, id, "maket.png", TestImages.Png())).EnsureSuccessStatusCode();
            references[slug] = (await Json(await client.PostAsync($"/api/submissions/{id}/submit", null)))
                .GetProperty("referenceNumber").GetString()!;
        }

        Assert.Equal(2, references.Values.Distinct().Count()); // two entries, two references

        var mine = (await Json(await client.GetAsync("/api/me/submissions"))).EnumerateArray().ToList();
        Assert.Equal(2, mine.Count);
        foreach (var (slug, reference) in references)
            Assert.Single(mine, x => x.GetProperty("competitionSlug").GetString() == slug
                                     && x.GetProperty("referenceNumber").GetString() == reference);
    }

    // ---- The ways in ---------------------------------------------------------------------------

    [Fact]
    public async Task Signing_in_again_returns_the_same_person_and_their_work()
    {
        var participant = await NewParticipantAsync("login");
        var id = (await Json(await participant.Client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = DbSeeder.NovxaniCompetitionSlug }))).GetProperty("id").GetInt32();

        // A fresh client: only the password, as after closing the browser.
        var returning = _factory.CreateClient();
        returning.UseToken(await returning.LoginAsync(participant.Email, Participant.Password));

        Assert.Equal(participant.Email,
            (await Json(await returning.GetAsync("/api/auth/me"))).GetProperty("email").GetString());
        var mine = await Json(await returning.GetAsync("/api/me/submissions"));
        Assert.Contains(mine.EnumerateArray(), x => x.GetProperty("id").GetInt32() == id); // the draft is waiting
    }

    [Fact]
    public async Task A_wrong_password_is_answered_in_words_and_changes_nothing()
    {
        var participant = await NewParticipantAsync("wrong-password");
        var client = _factory.CreateClient();

        var refused = await client.PostAsJsonAsync("/api/auth/login",
            new { email = participant.Email, password = "not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("yanlışdır", body); // a sentence for a person, not a stack trace
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("   at ", body);

        // The right password still works right afterwards.
        client.UseToken(await client.LoginAsync(participant.Email, Participant.Password));
        Assert.Equal(participant.Email,
            (await Json(await client.GetAsync("/api/auth/me"))).GetProperty("email").GetString());
    }

    // ---- The ways it should not work ------------------------------------------------------------

    [Fact]
    public async Task An_entry_cannot_be_finished_while_anything_required_is_missing()
    {
        var participant = await NewParticipantAsync("incomplete");
        var client = participant.Client;
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = DbSeeder.NovxaniCompetitionSlug }))).GetProperty("id").GetInt32();

        var tooEarly = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);
        var errors = await Errors(tooEarly);
        foreach (var key in new[] { "nomination", "title", "concept", "symbols", "files" })
            Assert.True(errors.TryGetProperty(key, out _), $"no error for the required field {key}");
        Assert.False(errors.TryGetProperty("notes", out _)); // the one optional field

        // Answers but still no file: the entry is not finished and no reference is handed out.
        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new
            {
                nomination = "Promo Məhsul Tətbiqləri", title = "Yarımçıq",
                concept = "Konsept.", symbols = "Simvollar.",
            },
        }));
        var noFile = await client.PostAsync($"/api/submissions/{id}/submit", null);
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.True((await Errors(noFile)).TryGetProperty("files", out _));

        await _factory.WithDbAsync(async db =>
        {
            var submission = await db.Submissions.SingleAsync(s => s.Id == id);
            Assert.Equal(SubmissionStatus.Draft, submission.Status);
            Assert.Null(submission.ReferenceNumber);
        });
        // A draft is the participant's own business and never reaches moderation.
        Assert.Empty((await Json(await (await AdminAsync()).GetAsync("/api/admin/submissions")))
            .GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_refused_upload_leaves_the_entry_as_it_was()
    {
        var participant = await NewParticipantAsync("bad-upload");
        var client = participant.Client;
        var id = (await Json(await client.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = DbSeeder.EmircanCompetitionSlug }))).GetProperty("id").GetInt32();

        // A format the competition does not accept, and a file past the size it allows.
        var svg = await UploadAsync(client, id, "logo.svg",
            "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/svg+xml");
        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);
        Assert.Contains("qəbul olunmur", (await Errors(svg)).GetProperty("files")[0].GetString());

        var huge = await UploadAsync(client, id, "boyuk.png", TestImages.PaddedPng(10 * 1024 * 1024 + 1));
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Contains("10 MB", (await Errors(huge)).GetProperty("files")[0].GetString());

        await _factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.SubmissionFiles.Where(f => f.SubmissionId == id).ToListAsync());
            Assert.Equal(SubmissionStatus.Draft, (await db.Submissions.SingleAsync(s => s.Id == id)).Status);
        });
        Assert.Empty(Directory.GetFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories));

        // And the entry is still usable afterwards: a good file goes through.
        Assert.True((await UploadAsync(client, id, "eskiz.png", TestImages.Png())).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Nobody_enters_without_signing_in()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/submissions",
            new { competitionSlug = DbSeeder.NovxaniCompetitionSlug })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/submissions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/submissions")).StatusCode);

        // The competition itself stays open to read: that is how anyone decides to take part.
        foreach (var slug in new[] { DbSeeder.NovxaniCompetitionSlug, DbSeeder.EmircanCompetitionSlug })
            Assert.True((await anonymous.GetAsync($"/api/competitions/{slug}")).IsSuccessStatusCode);
    }
}
