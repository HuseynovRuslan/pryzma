using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Tests;

/// <summary>
/// The platform's own public words: readable by anyone, editable only by an admin, plain text only, and audited.
/// Renaming the site or rewriting its headline must be an afternoon in the admin, not a deployment.
/// </summary>
public sealed class PlatformSettingsTests : IDisposable
{
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

    private static object Settings(
        string name = "PRIZMA", string hero = "İdeyanı meydana çıxar",
        string subtitle = "Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma",
        string promo = "Bir hesab, bütün müsabiqələr") =>
        new { platformName = name, homeHeroTitle = hero, homeHeroSubtitle = subtitle, authPromoTitle = promo };

    [Fact]
    public async Task Anyone_may_read_the_platforms_own_words_and_they_start_as_PRIZMA()
    {
        var settings = await Json(await _factory.CreateClient().GetAsync("/api/settings"));

        Assert.Equal("PRIZMA", settings.GetProperty("platformName").GetString());
        // Short public lines are labels, not sentences: they carry no full stop.
        Assert.Equal("İdeyanı meydana çıxar", settings.GetProperty("homeHeroTitle").GetString());
        Assert.Equal("Yaradıcı layihələr və açıq müsabiqələr üçün vahid platforma",
            settings.GetProperty("homeHeroSubtitle").GetString());
        Assert.Equal("Bir hesab, bütün müsabiqələr", settings.GetProperty("authPromoTitle").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("authPromoImage").ValueKind);

        // Only the public words; nothing about the machine this runs on.
        var names = settings.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["platformName", "homeHeroTitle", "homeHeroSubtitle", "authPromoTitle", "authPromoImage"], names);
    }

    [Fact]
    public async Task An_admin_renames_the_platform_and_everyone_sees_it()
    {
        var admin = await AdminAsync();

        var saved = await Json(await admin.PutAsJsonAsync("/api/admin/settings",
            Settings(name: "SƏNƏT MEYDANI", hero: "Şəhəri birlikdə çəkək")));
        Assert.Equal("SƏNƏT MEYDANI", saved.GetProperty("platformName").GetString());

        var published = await Json(await _factory.CreateClient().GetAsync("/api/settings"));
        Assert.Equal("SƏNƏT MEYDANI", published.GetProperty("platformName").GetString());
        Assert.Equal("Şəhəri birlikdə çəkək", published.GetProperty("homeHeroTitle").GetString());

        await _factory.WithDbAsync(async db =>
        {
            var entry = await db.AuditLogs.SingleAsync(a => a.Action == AuditActions.PlatformBrandSettingsUpdated);
            Assert.Equal(AuditEntities.Platform, entry.EntityType);
            Assert.Equal("brand", entry.EntityId);
            Assert.NotNull(entry.UserId);
        });
    }

    [Fact]
    public async Task Whitespace_is_trimmed_and_markup_is_refused()
    {
        var admin = await AdminAsync();

        var trimmed = await Json(await admin.PutAsJsonAsync("/api/admin/settings", Settings(name: "  PRIZMA  ")));
        Assert.Equal("PRIZMA", trimmed.GetProperty("platformName").GetString());

        var markup = await admin.PutAsJsonAsync("/api/admin/settings",
            Settings(hero: "<script>alert(1)</script>"));
        Assert.Equal(HttpStatusCode.BadRequest, markup.StatusCode);
        Assert.Contains("HTML", await markup.Content.ReadAsStringAsync());

        // The refused value never reached the database.
        var published = await Json(await _factory.CreateClient().GetAsync("/api/settings"));
        Assert.DoesNotContain("<", published.GetProperty("homeHeroTitle").GetString());
    }

    [Theory]
    [InlineData("platformName", "")]
    [InlineData("platformName", "   ")]
    [InlineData("homeHeroTitle", "")]
    [InlineData("homeHeroSubtitle", "")]
    [InlineData("authPromoTitle", "")]
    public async Task Empty_values_are_refused_so_the_site_can_never_be_nameless(string field, string value)
    {
        var admin = await AdminAsync();
        var body = new Dictionary<string, string>
        {
            ["platformName"] = "PRIZMA",
            ["homeHeroTitle"] = "Başlıq",
            ["homeHeroSubtitle"] = "Alt mətn",
            ["authPromoTitle"] = "Giriş",
            [field] = value,
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", body)).StatusCode);
    }

    [Fact]
    public async Task Too_long_values_are_refused()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/settings", Settings(name: new string('a', 51)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/settings", Settings(hero: new string('a', 121)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/settings", Settings(subtitle: new string('a', 301)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/admin/settings", Settings(promo: new string('a', 121)))).StatusCode);
    }

    [Fact]
    public async Task The_sign_in_picture_goes_through_the_same_pipeline_as_every_other_public_image()
    {
        var admin = await AdminAsync();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "giris.png");

        var saved = await Json(await admin.PostAsync("/api/admin/settings/auth-image", form));
        var url = saved.GetProperty("authPromoImage").GetString()!;
        Assert.StartsWith("/api/media/covers/", url); // re-encoded, metadata-free, in the public covers area
        Assert.EndsWith(".webp", url);
        Assert.Equal(url, (await Json(await _factory.CreateClient().GetAsync("/api/settings")))
            .GetProperty("authPromoImage").GetString());

        var removed = await Json(await admin.DeleteAsync("/api/admin/settings/auth-image"));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("authPromoImage").ValueKind);
    }

    [Fact]
    public async Task A_participant_may_read_but_never_write()
    {
        var participant = _factory.CreateClient();
        participant.UseToken(await participant.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));

        Assert.True((await participant.GetAsync("/api/settings")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await participant.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await participant.PutAsJsonAsync("/api/admin/settings", Settings(name: "Mənim saytım"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await participant.DeleteAsync("/api/admin/settings/auth-image")).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/admin/settings", Settings())).StatusCode);

        Assert.Equal("PRIZMA",
            (await Json(await anonymous.GetAsync("/api/settings"))).GetProperty("platformName").GetString());
    }
}
