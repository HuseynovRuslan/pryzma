using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>Phase 5 operations: health endpoints, configuration fail-fast, headers, CORS, errors.</summary>
public sealed class OperationsTests
{
    [Fact]
    public async Task Liveness_and_readiness_report_state_without_details()
    {
        using var factory = new MeydanFactory();
        var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("database").GetString());
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("storage").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // The probe leaves nothing behind.
        Assert.Empty(Directory.GetFiles(factory.StorageRoot, ".health-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Readiness_fails_when_storage_is_unusable_but_liveness_does_not()
    {
        using var factory = new MeydanFactory();
        var client = factory.CreateClient();
        Directory.Delete(factory.StorageRoot, recursive: true);
        await File.WriteAllTextAsync(factory.StorageRoot, "not a directory");
        try
        {
            var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            var text = await ready.Content.ReadAsStringAsync();
            var body = JsonDocument.Parse(text).RootElement;
            Assert.Equal("Unhealthy", body.GetProperty("checks").GetProperty("storage").GetString());
            Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("database").GetString());
            Assert.DoesNotContain(factory.StorageRoot.Replace("\\", "\\\\"), text);
            Assert.DoesNotContain("meydan-tests", text);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            File.Delete(factory.StorageRoot);
        }
    }

    [Fact]
    public void Production_refuses_to_start_with_missing_settings_and_names_them_without_values()
    {
        var signingKey = "k" + new string('x', 40);
        using var factory = new MeydanFactory
        {
            EnvironmentName = "Production",
            Settings = new()
            {
                ["Jwt:SigningKey"] = signingKey,
                ["App:PublicUrl"] = "http://insecure.example",
                ["Cors:Origins:0"] = "*",
            },
        };
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient()).ToString();
        Assert.Contains("App:PublicUrl", ex);
        Assert.Contains("AuthCookie:Secure", ex);
        Assert.Contains("Email:Mode", ex);
        Assert.Contains("Cors:Origins", ex);
        Assert.DoesNotContain(signingKey, ex);
        Assert.DoesNotContain("insecure.example", ex);
        Assert.DoesNotContain("Password=", ex);
    }

    [Fact]
    public void Production_refuses_storage_inside_the_application_directory()
    {
        using var factory = new MeydanFactory
        {
            EnvironmentName = "Production",
            ProductionLike = true,
            Settings = new() { ["Uploads:StorageRoot"] = Path.Combine(AppContext.BaseDirectory, "uploads") },
        };
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient()).ToString();
        Assert.Contains("Uploads:StorageRoot", ex);
    }

    [Fact]
    public async Task A_complete_production_configuration_starts_and_is_ready()
    {
        using var factory = new MeydanFactory { EnvironmentName = "Production", ProductionLike = true };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://meydan.test") });
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        // HSTS on HTTPS responses in production.
        Assert.Contains("max-age=31536000", ready.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Api_responses_carry_security_headers_and_a_correlation_id()
    {
        using var factory = new MeydanFactory();
        var res = await factory.CreateClient().GetAsync("/api/projects");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("geolocation=()", res.Headers.GetValues("Permissions-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Matches("^[0-9a-f]{32}$", res.Headers.GetValues("X-Correlation-ID").Single());
        Assert.False(res.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Cors_allows_only_configured_origins_and_never_a_wildcard()
    {
        using var factory = new MeydanFactory { Settings = new() { ["Cors:Origins:0"] = "https://admin.meydan.test", ["Cors:Origins:1"] = "*" } };
        var client = factory.CreateClient();

        HttpRequestMessage Preflight(string origin)
        {
            var r = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
            r.Headers.Add("Origin", origin);
            r.Headers.Add("Access-Control-Request-Method", "GET");
            return r;
        }

        var allowed = await client.SendAsync(Preflight("https://admin.meydan.test"));
        Assert.Equal("https://admin.meydan.test", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var other = await client.SendAsync(Preflight("https://evil.example"));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unexpected_errors_show_a_safe_azerbaijani_message_with_a_reference_only()
    {
        using var factory = new MeydanFactory();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IStartupFilter>(new ThrowingEndpoint())));
        var res = await app.CreateClient().GetAsync("/test-boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Gözlənilməz xəta baş verdi. Bir az sonra yenidən cəhd edin.", body.GetProperty("title").GetString());
        var traceId = body.GetProperty("traceId").GetString()!;
        Assert.Contains(traceId, body.GetProperty("detail").GetString());
        Assert.Equal(traceId, res.Headers.GetValues("X-Correlation-ID").Single());
        foreach (var secret in new[] { "SECRET-DETAIL", "C:\\\\secret", "Npgsql", "at Meydan", "StackTrace", "InvalidOperationException" })
            Assert.DoesNotContain(secret, text);

        var missing = await factory.CreateClient().GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Framework_errors_use_azerbaijani_titles()
    {
        using var factory = new MeydanFactory();
        var res = await factory.CreateClient().GetAsync("/api/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bu əməliyyat üçün daxil olmaq lazımdır.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task An_oversized_body_is_a_413_with_an_azerbaijani_message_not_a_server_error()
    {
        using var factory = new MeydanFactory();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IStartupFilter>(new ThrowingEndpoint(new BadHttpRequestException("Request body too large.", 413)))));
        var res = await app.CreateClient().GetAsync("/test-boom");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sorğu çox böyükdür.", body.GetProperty("title").GetString());
        Assert.DoesNotContain(factory.Logs.Lines, l => l.StartsWith("Error") && l.Contains("Unhandled exception"));
    }

    private sealed class ThrowingEndpoint(Exception? exception = null) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/test-boom", b => b.Run(_ =>
                throw exception ?? new InvalidOperationException("SECRET-DETAIL at C:\\secret\\path Npgsql")));
        };
    }
}
