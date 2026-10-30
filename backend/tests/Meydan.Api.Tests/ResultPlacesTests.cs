using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// First, second and third place of a competition without categories — as "Əmircanın simvolu" runs it: every work
/// in one "Ümumi" nomination for the vote, one prize list for the whole competition. The vote ranks the works; an
/// admin awards the places, and works level on votes are shown level, never ordered by a machine.
/// </summary>
public sealed partial class ResultsTests
{
    private const string Open = "emircan-sinaq";

    private sealed record Work(int Id, string Reference, string Title, string Email, string Name);

    private static Task<HttpResponseMessage> PlacesAsync(HttpClient admin, int competitionId, int?[] places, int?[] current, bool confirm = false) =>
        admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/result/places",
            new { places, current, confirmPublished = confirm });

    private static int?[] Holders(JsonElement result) =>
        result.GetProperty("places").EnumerateArray()
            .Select(p => p.GetProperty("winner").ValueKind == JsonValueKind.Null ? (int?)null : p.GetProperty("winner").GetProperty("submissionId").GetInt32())
            .ToArray();

    /// <summary>
    /// A competition without categories whose four works were all filed under one "Ümumi" nomination for the vote,
    /// with votes Qartal 3, Dağ 1, Çay 1, Bulaq 0.
    /// </summary>
    private async Task<(HttpClient Admin, int Competition, Work Qartal, Work Dag, Work Cay, Work Bulaq)> SingleListAsync()
    {
        var admin = await AdminAsync();
        var projectId = (await Json(await admin.GetAsync("/api/admin/projects"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var c = await Json(await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Əmircan sınaq", slug = Open, shortDescription = "Sınaq", hasCategories = false,
        }));
        var id = c.GetProperty("id").GetInt32();
        await Json(await admin.PostAsJsonAsync($"/api/admin/competitions/{id}/fields", new
        {
            key = "ad", label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true, isActive = true,
            isPublicAfterResults = true,
        }));
        await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}/status", new { status = "Published" }));

        var works = new List<Work>();
        foreach (var (title, first) in new[] { ("Qartal", "Elvin"), ("Dağ", "Fidan"), ("Çay", "Günel"), ("Bulaq", "Həsən") })
        {
            var (p, email) = await ParticipantAsync(first, "Nəsirli");
            var d = await Json(await p.PostAsJsonAsync("/api/submissions", new { competitionSlug = Open }));
            var sid = d.GetProperty("id").GetInt32();
            await Json(await p.PutAsJsonAsync($"/api/submissions/{sid}", new { answers = new { ad = title } }));
            var submitted = await Json(await p.PostAsync($"/api/submissions/{sid}/submit", null));
            works.Add(new Work(sid, submitted.GetProperty("referenceNumber").GetString()!, title, email, $"{first} Nəsirli"));
        }

        var voters = new List<string>();
        for (var i = 0; i < 5; i++) voters.Add((await ParticipantAsync("Səsverən", $"N{i}")).Email);
        await _factory.WithDbAsync(async db =>
        {
            var umumi = new CompetitionCategory { CompetitionId = id, Name = "Ümumi", Slug = "umumi", IsActive = true };
            db.CompetitionCategories.Add(umumi);
            await db.SaveChangesAsync();
            var ids = works.Select(w => w.Id).ToList();
            foreach (var s in await db.Submissions.Where(s => ids.Contains(s.Id)).ToListAsync())
                s.CompetitionCategoryId = umumi.Id;
            await db.SaveChangesAsync();
            var users = await db.Users.Where(u => voters.Contains(u.Email!)).OrderBy(u => u.LastName).Select(u => u.Id).ToListAsync();
            var ballot = new[] { works[0], works[0], works[0], works[1], works[2] };
            for (var i = 0; i < ballot.Length; i++)
                db.Votes.Add(new Vote { UserId = users[i], SubmissionId = ballot[i].Id, CompetitionCategoryId = umumi.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        return (admin, id, works[0], works[1], works[2], works[3]);
    }

    [Fact]
    public async Task An_admin_awards_three_places_with_the_vote_beside_them_and_level_works_stay_level()
    {
        var (admin, id, qartal, dag, cay, bulaq) = await SingleListAsync();

        var r = await Json(await admin.GetAsync($"/api/admin/competitions/{id}/result"));
        Assert.Equal(new int?[] { null, null, null }, Holders(r));
        // The single prize slot counts every work, though the vote filed them all under "Ümumi".
        var slot = Assert.Single(r.GetProperty("slots").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("category").ValueKind);
        Assert.Equal(4, slot.GetProperty("eligibleCount").GetInt32());
        var ranking = r.GetProperty("placeCandidates").EnumerateArray()
            .Select(x => (x.GetProperty("submissionId").GetInt32(), x.GetProperty("voteCount").GetInt32(), x.GetProperty("voteRank").GetInt32()))
            .ToList();
        Assert.Equal((qartal.Id, 3, 1), ranking[0]);
        Assert.Equal(new[] { (dag.Id, 1, 2), (cay.Id, 1, 2) }, ranking.Skip(1).Take(2).OrderBy(x => x.Item1 == dag.Id ? 0 : 1).ToList());
        Assert.Equal((bulaq.Id, 0, 4), ranking[3]);

        // The old one-winner route still works and lands on first place — not on the "Ümumi" nomination.
        r = await Json(await SelectAsync(admin, id, qartal.Id));
        Assert.Equal(new int?[] { qartal.Id, null, null }, Holders(r));
        Assert.False(await _factory.QueryDbAsync(db => db.CompetitionCategoryWinners.AnyAsync(w => w.CompetitionId == id)));

        // Filled from the top, one place per work, and the screen must say what it saw.
        Assert.Contains("boş qalarkən", await ErrorText(await PlacesAsync(admin, id, [qartal.Id, null, cay.Id], [qartal.Id, null, null]), HttpStatusCode.BadRequest));
        Assert.Contains("yalnız bir yer", await ErrorText(await PlacesAsync(admin, id, [qartal.Id, qartal.Id, null], [qartal.Id, null, null]), HttpStatusCode.BadRequest));
        Assert.Contains("artıq dəyişdirilib", await ErrorText(await PlacesAsync(admin, id, [qartal.Id, dag.Id, cay.Id], [null, null, null]), HttpStatusCode.Conflict));

        r = await Json(await PlacesAsync(admin, id, [qartal.Id, dag.Id, cay.Id], [qartal.Id, null, null]));
        Assert.Equal(new int?[] { qartal.Id, dag.Id, cay.Id }, Holders(r));
        Assert.Equal(qartal.Id, Assert.Single(r.GetProperty("slots").EnumerateArray()).GetProperty("winner").GetProperty("submissionId").GetInt32());

        // Dağ and Çay are level: the admin may put either second, and swapping them is one save.
        r = await Json(await PlacesAsync(admin, id, [qartal.Id, cay.Id, dag.Id], [qartal.Id, dag.Id, cay.Id]));
        Assert.Equal(new int?[] { qartal.Id, cay.Id, dag.Id }, Holders(r));

        // A placed work is a winner: it cannot be rejected, nor moved to first place by the one-winner route, and
        // a place is emptied from the bottom.
        var reject = await admin.PutAsJsonAsync($"/api/admin/submissions/{cay.Id}/status", new { status = "Rejected" });
        Assert.Contains("qalibdir", await ErrorText(reject, HttpStatusCode.Conflict));
        Assert.Contains("2-ci yeri tutur", await ErrorText(await ChangeAsync(admin, id, cay.Id, qartal.Id), HttpStatusCode.BadRequest));
        Assert.Contains("əvvəlcə 3-cü yeri", await ErrorText(await RemoveAsync(admin, id, cay.Id), HttpStatusCode.Conflict));

        var audit = await AuditAsync(admin, id);
        Assert.Contains(audit, a => a.Action == "WinnerSelected" && a.Summary.Contains($"place 2: submission {dag.Id} ({dag.Reference})"));
        Assert.Contains(audit, a => a.Action == "WinnerChanged" && a.Summary.Contains($"place 2: submission {dag.Id} ({dag.Reference}) → {cay.Id} ({cay.Reference})"));
        Assert.Contains(audit, a => a.Action == "WinnerChanged" && a.Summary.Contains("place 3:"));
    }

    [Fact]
    public async Task Published_places_name_their_authors_in_order_and_everybody_else_stays_anonymous()
    {
        var (admin, id, qartal, dag, cay, bulaq) = await SingleListAsync();
        await Json(await PlacesAsync(admin, id, [qartal.Id, cay.Id, dag.Id], [null, null, null]));
        await Json(await CloseAsync(admin, id));
        await Json(await PublishAsync(admin, id));
        Assert.Contains(await AuditAsync(admin, id), a => a.Action == "ResultsPublished"
            && a.Summary.Contains($"places: 1 {qartal.Reference}, 2 {cay.Reference}, 3 {dag.Reference}"));

        var anonymous = _factory.CreateClient();
        var result = await Json(await anonymous.GetAsync($"/api/competitions/{Open}/results"));
        var places = result.GetProperty("places").EnumerateArray()
            .Select(p => (p.GetProperty("place").GetInt32(), p.GetProperty("title").GetString(), p.GetProperty("winnerName").GetString()))
            .ToList();
        Assert.Equal(new[] { (1, (string?)"Qartal", (string?)qartal.Name), (2, "Çay", cay.Name), (3, "Dağ", dag.Name) }, places);
        Assert.Equal("Qartal", result.GetProperty("overallWinner").GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("winnerCount").GetInt32());
        Assert.Empty(result.GetProperty("categories").EnumerateArray());

        var gallery = await Json(await anonymous.GetAsync($"/api/competitions/{Open}/gallery"));
        var items = gallery.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "Qartal", "Çay", "Dağ", "Bulaq" }, items.Select(i => i.GetProperty("title").GetString()).ToList());
        Assert.Equal(new int?[] { 1, 2, 3, null }, items.Select(i => i.GetProperty("place").ValueKind == JsonValueKind.Null ? (int?)null : i.GetProperty("place").GetInt32()).ToList());
        // One prize list: no card says "Ümumi", and the work outside the places keeps its author to itself.
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("category").ValueKind));
        Assert.DoesNotContain(bulaq.Name, gallery.GetRawText());
        var work = await Json(await anonymous.GetAsync($"/api/competitions/{Open}/gallery/{cay.Reference}"));
        Assert.Equal(2, work.GetProperty("place").GetInt32());

        var author = _factory.CreateClient();
        author.UseToken(await author.LoginAsync(cay.Email, "secret123"));
        var mine = Assert.Single((await Json(await author.GetAsync("/api/me/submissions"))).EnumerateArray());
        Assert.True(mine.GetProperty("isWinner").GetBoolean());
        Assert.Equal(2, mine.GetProperty("place").GetInt32());

        // Once public, a change needs the admin's confirmation, and the result keeps its first place.
        Assert.Contains("təsdiqləyin", await ErrorText(await PlacesAsync(admin, id, [qartal.Id, cay.Id, null], [qartal.Id, cay.Id, dag.Id]), HttpStatusCode.Conflict));
        Assert.Contains("ən azı bir qalib", await ErrorText(await PlacesAsync(admin, id, [null, null, null], [qartal.Id, cay.Id, dag.Id], confirm: true), HttpStatusCode.Conflict));
        await Json(await PlacesAsync(admin, id, [qartal.Id, cay.Id, null], [qartal.Id, cay.Id, dag.Id], confirm: true));
        result = await Json(await anonymous.GetAsync($"/api/competitions/{Open}/results"));
        Assert.Equal(2, result.GetProperty("places").GetArrayLength());
        Assert.DoesNotContain(dag.Name, result.GetRawText());
    }
}
