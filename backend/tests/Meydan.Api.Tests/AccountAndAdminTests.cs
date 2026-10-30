using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

public sealed class AccountAndAdminTests : IDisposable
{
    private readonly MeydanFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task My_submissions_requires_login()
    {
        var res = await _factory.CreateClient().GetAsync("/api/me/submissions");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task My_submissions_lists_only_own_items_with_project_competition_and_category()
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync("owner@example.com"));
        var other = _factory.CreateClient();
        await other.RegisterAsync("other@example.com");

        await _factory.WithDbAsync(async db =>
        {
            var c = db.Competitions.Single(x => x.Slug == "baki-kendleri-sevron-dizayni");
            var category = new CompetitionCategory { CompetitionId = c.Id, Name = "Kateqoriya A", Slug = "a" };
            db.CompetitionCategories.Add(category);
            await db.SaveChangesAsync();
            var owner = db.Users.Single(u => u.Email == "owner@example.com");
            var stranger = db.Users.Single(u => u.Email == "other@example.com");
            db.Submissions.AddRange(
                new Submission
                {
                    UserId = owner.Id, CompetitionId = c.Id, CompetitionCategoryId = category.Id, Title = "Mənim işim",
                    Status = SubmissionStatus.Submitted, ReferenceNumber = "SUB-2026-00001",
                    SubmittedAt = DateTimeOffset.UtcNow,
                },
                new Submission
                {
                    UserId = stranger.Id, CompetitionId = c.Id, Title = "Başqasının işi",
                    Status = SubmissionStatus.Submitted, ReferenceNumber = "SUB-2026-00002",
                });
            await db.SaveChangesAsync();
        });

        var items = await client.GetFromJsonAsync<JsonElement>("/api/me/submissions");
        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal("SUB-2026-00001", item.GetProperty("referenceNumber").GetString());
        Assert.Equal("Bakı kəndləri", item.GetProperty("projectName").GetString());
        Assert.Equal("Kateqoriya A", item.GetProperty("categoryName").GetString());
        Assert.Equal("Submitted", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Profile_can_be_updated()
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync("profile@example.com"));

        var res = await client.PutAsJsonAsync("/api/me/profile",
            new { firstName = "Günel", lastName = "Əliyeva", phone = TestPhones.Next() });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me/profile");
        Assert.Equal("Günel", me.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task Participants_cannot_reach_admin_api()
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync("p@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/projects")).StatusCode);
    }

    [Fact]
    public async Task Admin_builds_a_new_project_and_competition_without_code()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));

        var project = await (await admin.PostAsJsonAsync("/api/admin/projects", new
        {
            name = "Test layihəsi", shortDescription = "Qısa", accentColor = "#2b4fa0",
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("test-layihesi", project.GetProperty("slug").GetString());
        Assert.Equal("#2B4FA0", project.GetProperty("accentColor").GetString());
        Assert.Equal("Draft", project.GetProperty("status").GetString());
        var projectId = project.GetProperty("id").GetInt32();

        var competitionRes = await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Foto müsabiqəsi", shortDescription = "Qısa",
            hasCategories = true, categoryLabel = "Janrlar",
        });
        Assert.Equal(HttpStatusCode.Created, competitionRes.StatusCode);
        var competition = await competitionRes.Content.ReadFromJsonAsync<JsonElement>();
        var competitionId = competition.GetProperty("id").GetInt32();

        var categories = await (await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/categories/bulk",
            new { names = new[] { "Portret", "Mənzərə", "Portret", " " } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["Portret", "Mənzərə"], categories.EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        (await admin.PostAsJsonAsync($"/api/admin/competitions/{competitionId}/fields",
            new { label = "İşin adı", fieldType = "ShortText", isRequired = true, isSubmissionTitle = true })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}/status", new { status = "Published" })).EnsureSuccessStatusCode();

        // Still a draft project → invisible to the public.
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/competitions/foto-musabiqesi")).StatusCode);

        (await admin.PutAsJsonAsync($"/api/admin/projects/{projectId}/status", new { status = "Published" })).EnsureSuccessStatusCode();
        var published = await anon.GetFromJsonAsync<JsonElement>("/api/competitions/foto-musabiqesi");
        Assert.Equal("Janrlar", published.GetProperty("categoryLabel").GetString());
        Assert.Equal(2, published.GetProperty("categories").GetArrayLength());
    }

    [Fact]
    public async Task Admin_slug_conflicts_and_bad_dates_are_rejected()
    {
        var admin = _factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, _factory.AdminPassword));

        var dup = await admin.PostAsJsonAsync("/api/admin/projects",
            new { name = "Bakı kəndləri", shortDescription = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        var slugError = (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("slug")[0].GetString();
        Assert.Contains("baki-kendleri", slugError);

        var projectId = _factory.WithDb(db => db.Projects.Single().Id);
        var badDates = await admin.PostAsJsonAsync("/api/admin/competitions", new
        {
            projectId, title = "Tarix", shortDescription = "x",
            startDate = "2026-10-10T00:00:00+04:00", endDate = "2026-10-01T00:00:00+04:00",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDates.StatusCode);
    }

    [Fact]
    public async Task Reference_numbers_are_sequential_per_year()
    {
        using var scope = _factory.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<ReferenceNumberGenerator>();
        var year = _factory.Clock.Now.Year;

        Assert.Equal($"SUB-{year}-00001", await generator.NextAsync("SUB", default));
        Assert.Equal($"SUB-{year}-00002", await generator.NextAsync("SUB", default));
    }

    [Theory]
    [InlineData("Bakı kəndləri", "baki-kendleri")]
    [InlineData("Şevron dizayn müsabiqəsi", "sevron-dizayn-musabiqesi")]
    [InlineData("  İçərişəhər — Ağ Şəhər!  ", "iceriseher-ag-seher")]
    [InlineData("Göygöl 2026", "goygol-2026")]
    public void Slugs_transliterate_azerbaijani(string input, string expected) =>
        Assert.Equal(expected, Slug.From(input));
}
