using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meydan.Infrastructure.Files;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meydan.Api.Common;

/// <summary>
/// Gives every request a correlation id (accepts a well-formed X-Correlation-ID from the proxy, otherwise makes one),
/// returns it in the response, puts it in the log scope and logs one line per request: method, path (never the
/// query string), status and duration. Bodies, headers, tokens and cookies are never logged.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string Header = "X-Correlation-ID";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext http)
    {
        var incoming = http.Request.Headers[Header].ToString();
        var id = SafeId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        http.TraceIdentifier = id;
        http.Response.Headers[Header] = id;

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(http);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var path = http.Request.Path.Value ?? "";
            var level = path.StartsWith("/health", StringComparison.Ordinal) ? LogLevel.Debug
                : http.Response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Information;
            logger.Log(level, "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0} ms",
                http.Request.Method, path, http.Response.StatusCode, elapsed);
        }
    }
}

/// <summary>
/// Baseline headers on every API response. The API only returns JSON and files, so its CSP forbids everything;
/// file endpoints set their own (image + sandbox). The SPA's headers are set by the web server (see README).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        h.XFrameOptions = "DENY";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        if (!http.Request.Path.StartsWithSegments("/swagger"))
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return next(http);
    }
}

/// <summary>PostgreSQL reachable and the schema up to date (no pending migrations).</summary>
public sealed class DatabaseHealthCheck(MeydanDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
                return HealthCheckResult.Unhealthy("unreachable");
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            return pending.Any() ? HealthCheckResult.Unhealthy("pending-migrations") : HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("unreachable");
        }
    }
}

/// <summary>Storage root writable/readable, checked with a temporary probe file that is always removed.</summary>
public sealed class StorageHealthCheck(IFileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await storage.ProbeAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("not-writable");
}

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// /health/live: the process answers (no dependencies — a database outage must not get the process restarted).
    /// /health/ready and /health: database + storage. Responses name the checks and their state only: no paths,
    /// hosts, versions or exception text.
    /// </summary>
    public static void MapMeydanHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = Write });
        var ready = new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = Write };
        app.MapHealthChecks("/health/ready", ready);
        app.MapHealthChecks("/health", ready);
    }

    private static Task Write(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        http.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };
        return http.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

public static class ProblemDetailsSetup
{
    private static readonly Dictionary<int, string> Titles = new()
    {
        [400] = "Sorğu düzgün deyil.",
        [401] = "Bu əməliyyat üçün daxil olmaq lazımdır.",
        [403] = "Bu əməliyyat üçün icazəniz yoxdur.",
        [404] = "Axtardığınız məlumat tapılmadı.",
        [405] = "Bu əməliyyat dəstəklənmir.",
        [413] = "Sorğu çox böyükdür.",
        [415] = "Məlumatın formatı dəstəklənmir.",
        [429] = "Çox sayda sorğu göndərildi. Bir az sonra yenidən cəhd edin.",
        [500] = "Gözlənilməz xəta baş verdi. Bir az sonra yenidən cəhd edin.",
    };

    /// <summary>Azerbaijani titles for framework-generated errors and a traceId on every problem response.</summary>
    public static void Customize(ProblemDetailsContext ctx)
    {
        var p = ctx.ProblemDetails;
        var status = p.Status ?? ctx.HttpContext.Response.StatusCode;
        if (Titles.TryGetValue(status, out var title) && (string.IsNullOrEmpty(p.Title) || !ContainsAzerbaijani(p.Title)))
            p.Title = title;
        p.Type = null;
        p.Extensions.Remove("traceId");
        p.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    }

    // Framework defaults are English ("Not Found"); our own messages always contain Azerbaijani letters or words.
    private static bool ContainsAzerbaijani(string text) =>
        text.IndexOfAny(['ə', 'Ə', 'ş', 'Ş', 'ı', 'İ', 'ğ', 'Ğ', 'ç', 'Ç', 'ö', 'Ö', 'ü', 'Ü']) >= 0;
}
