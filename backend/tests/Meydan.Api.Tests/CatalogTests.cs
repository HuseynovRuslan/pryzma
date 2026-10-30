using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;

namespace Meydan.Api.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly MeydanFactory _factory = new();
    private readonly HttpClient _client;

    public CatalogTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Home_lists_seeded_project_and_open_chevron_competition()
    {
        var home = await _client.GetFromJsonAsync<JsonElement>("/api/home");

        var project = Assert.Single(home.GetProperty("projects").EnumerateArray());
        Assert.Equal("baki-kendleri", project.GetProperty("slug").GetString());
        Assert.Equal("Bakı kəndləri", project.GetProperty("name").GetString());
        Assert.Equal(1, project.GetProperty("openCompetitionCount").GetInt32());

        var competition = Assert.Single(home.GetProperty("openCompetitions").EnumerateArray());
        Assert.Equal("Bakı kəndləri üçün şevron dizayn müsabiqəsi", competition.GetProperty("title").GetString());
        Assert.Equal("Open", competition.GetProperty("phase").GetString());
        Assert.True(competition.GetProperty("hasCategories").GetBoolean());
        Assert.Equal("Qəsəbələr", competition.GetProperty("categoryLabel").GetString());
        Assert.Equal(59, competition.GetProperty("categoryCount").GetInt32());
    }

    [Fact]
    public async Task Seed_invents_no_business_data()
    {
        var c = await _client.GetFromJsonAsync<JsonElement>("/api/competitions/baki-kendleri-sevron-dizayni");

        Assert.Equal(JsonValueKind.Null, c.GetProperty("prize").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("startDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("endDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("rules").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("maxSubmissionsPerUser").ValueKind);
    }

    [Fact]
    public async Task Project_detail_includes_its_competitions()
    {
        var p = await _client.GetFromJsonAsync<JsonElement>("/api/projects/baki-kendleri");
        var competition = Assert.Single(p.GetProperty("competitions").EnumerateArray());
        Assert.Equal("baki-kendleri-sevron-dizayni", competition.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Unknown_slugs_return_404_problem_in_azerbaijani()
    {
        var res = await _client.GetAsync("/api/projects/yoxdur");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Belə layihə tapılmadı.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Draft_projects_and_competitions_are_never_public()
    {
        await _factory.WithDbAsync(async db =>
        {
            var draft = new Project { Name = "Gizli", Slug = "gizli", ShortDescription = "x", Status = ProjectStatus.Draft };
            db.Projects.Add(draft);
            await db.SaveChangesAsync();
            db.Competitions.Add(new Competition
            {
                ProjectId = draft.Id, Title = "Gizli müsabiqə", Slug = "gizli-musabiqe", ShortDescription = "x",
                Status = CompetitionStatus.Published,
            });
            var baki = db.Projects.Single(p => p.Slug == "baki-kendleri");
            db.Competitions.Add(new Competition
            {
                ProjectId = baki.Id, Title = "Qaralama", Slug = "qaralama", ShortDescription = "x",
                Status = CompetitionStatus.Draft,
            });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/projects/gizli")).StatusCode);
        // A published competition inside a draft project is hidden too.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/competitions/gizli-musabiqe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/competitions/qaralama")).StatusCode);

        var all = await _client.GetFromJsonAsync<JsonElement>("/api/competitions");
        Assert.Single(all.EnumerateArray());
    }

    [Fact]
    public async Task Open_competition_past_its_end_date_is_shown_as_closed()
    {
        await _factory.WithDbAsync(async db =>
        {
            var c = db.Competitions.Single(x => x.Slug == "baki-kendleri-sevron-dizayni");
            c.EndDate = _factory.Clock.Now.AddDays(-1);
            await db.SaveChangesAsync();
        });

        var c = await _client.GetFromJsonAsync<JsonElement>("/api/competitions/baki-kendleri-sevron-dizayni");
        Assert.Equal("Closed", c.GetProperty("phase").GetString());
        Assert.False(c.GetProperty("isAcceptingSubmissions").GetBoolean());

        var home = await _client.GetFromJsonAsync<JsonElement>("/api/home");
        Assert.Empty(home.GetProperty("openCompetitions").EnumerateArray());
    }

    [Fact]
    public async Task Inactive_categories_are_hidden_and_order_is_respected()
    {
        await _factory.WithDbAsync(async db =>
        {
            var c = db.Competitions.Single(x => x.Slug == "baki-kendleri-sevron-dizayni");
            db.CompetitionCategories.AddRange(
                new CompetitionCategory { CompetitionId = c.Id, Name = "B", Slug = "b", SortOrder = -1 },
                new CompetitionCategory { CompetitionId = c.Id, Name = "A", Slug = "a", SortOrder = -2 },
                new CompetitionCategory { CompetitionId = c.Id, Name = "Gizli", Slug = "gizli", SortOrder = -3, IsActive = false });
            await db.SaveChangesAsync();
        });

        var c = await _client.GetFromJsonAsync<JsonElement>("/api/competitions/baki-kendleri-sevron-dizayni");
        var names = c.GetProperty("categories").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(["A", "B", "Biləcəri"], names.Take(3));
        Assert.DoesNotContain("Gizli", names);
    }
}
