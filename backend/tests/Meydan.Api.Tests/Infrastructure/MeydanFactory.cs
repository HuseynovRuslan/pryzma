using System.Net.Http.Headers;
using System.Net.Http.Json;
using Meydan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Meydan.Api.Tests.Infrastructure;

/// <summary>Controllable clock so refresh-token expiry and grace windows can be tested.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The full API against its own real PostgreSQL database (see <see cref="TestDatabase"/>), with the real
/// startup path: migrations, then the seed (Bakı kəndləri, the chevron competition, 59 settlements, the form)
/// and a bootstrap admin.
/// </summary>
public sealed class MeydanFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";

    /// <summary>
    /// The session limits (refresh, QRLog result polling); lifted for ordinary tests, kept at production values with
    /// <see cref="ProductionSessionLimits"/>.
    /// </summary>
    public static readonly string[] SessionLimitKeys =
    [
        "RefreshPermitsPerSessionPerMinute", "RefreshPermitsPerIpPerMinute", "AnonymousRefreshPermitsPerIpPerMinute",
        "MalformedRefreshPermitsPerIpPerMinute", "FailedRefreshesPerIpPerMinute",
        "QrlogPollPermitsPerTicketPerMinute", "QrlogPollPermitsPerIpPerMinute", "QrlogPollPermitsPerMinute",
    ];

    private static readonly string[] UnlimitedInTests =
        ["AuthPermitsPerMinute", "PasswordPermitsPer15Minutes", "SubmissionWritesPerMinute", "UploadsPerMinute",
            "VotesPerMinute", "RegistrationStartsPer10Minutes", "JurySignInsPer15Minutes", "JuryWritesPerMinute",
            "ContactMessagesPerHour", .. SessionLimitKeys];

    /// <summary>Generated per factory, so no credential — not even a test one — lives in source.</summary>
    public string AdminPassword { get; } = $"Aa1{Guid.NewGuid():N}";

    public TestClock Clock { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Host environment for this factory; "Testing" behaves like any non-Development deployment.</summary>
    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>Per-factory upload directory, removed on dispose.</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "meydan-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Keep the session limits from appsettings.json (production values) instead of lifting them.</summary>
    public bool ProductionSessionLimits { get; init; }

    /// <summary>The live content ("Abşeronun simvolları"); off by default so a test's baseline is the pilot alone.</summary>
    public bool LaunchContent { get; init; }

    public bool OmitAdminSeed { get; init; }
    public bool OmitSigningKey { get; init; }

    /// <summary>Start from an empty database instead of the migrated template: exercises every migration from zero.</summary>
    public bool FreshDatabase { get; init; }

    /// <summary>Overrides Database:MigrateOnStartup (default: only for a fresh database).</summary>
    public bool? MigrateOnStartup { get; init; }

    /// <summary>Supplies a complete, valid production configuration (https URL, secure cookie, explicit e-mail mode).</summary>
    public bool ProductionLike { get; init; }

    /// <summary>Extra configuration values, applied last (null removes a value).</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    /// <summary>Replaces services after the application's own registrations (a fake for an outside service).</summary>
    public Action<IServiceCollection>? Overrides { get; init; }

    /// <summary>When set, replaces the configured e-mail sender (delivery still goes through the background queue).</summary>
    public CapturingEmailSender? Mail { get; init; }

    private string? _database;
    // Task.Run: block on the thread pool, not on xUnit's limited synchronisation context (parallel hosts deadlock otherwise).
    public string DatabaseName => _database ??= Task.Run(() => TestDatabase.CreateAsync(FreshDatabase)).GetAwaiter().GetResult();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        // Only the capturing provider: console output from dozens of parallel hosts would block on the full console queue.
        builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(Logs); });
        builder.UseSetting("ConnectionStrings:Meydan", TestDatabase.ConnectionStringFor(DatabaseName));
        builder.UseSetting("Jwt:SigningKey",
            OmitSigningKey ? "" : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
        builder.UseSetting("AuthCookie:Secure", "false");
        builder.UseSetting("Seed:LaunchContent", LaunchContent ? "true" : "false");
        builder.UseSetting("Seed:AdminEmail", OmitAdminSeed ? "" : AdminEmail);
        builder.UseSetting("Seed:AdminPassword", OmitAdminSeed ? "" : AdminPassword);
        foreach (var key in UnlimitedInTests.Where(k => !ProductionSessionLimits || !SessionLimitKeys.Contains(k)))
            builder.UseSetting($"RateLimiting:{key}", "100000");
        builder.UseSetting("Email:RetryDelaySeconds", "0");
        builder.UseSetting("Uploads:StorageRoot", StorageRoot);
        // Template databases are migrated once per run; a fresh database runs every migration at startup.
        builder.UseSetting("Database:MigrateOnStartup", (MigrateOnStartup ?? FreshDatabase) ? "true" : "false");
        if (ProductionLike)
        {
            builder.UseSetting("App:PublicUrl", "https://meydan.test");
            builder.UseSetting("AuthCookie:Secure", "true");
            builder.UseSetting("Email:Mode", "Disabled");
        }
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (Mail is not null)
            {
                services.RemoveAll<Meydan.Infrastructure.Email.IEmailSender>();
                services.AddSingleton<Meydan.Infrastructure.Email.IEmailSender>(Mail);
            }
            Overrides?.Invoke(services);
        });
    }

    public T WithDb<T>(Func<MeydanDbContext, T> action)
    {
        using var scope = Services.CreateScope();
        return action(scope.ServiceProvider.GetRequiredService<MeydanDbContext>());
    }

    public async Task WithDbAsync(Func<MeydanDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<MeydanDbContext>());
    }

    public async Task<T> QueryDbAsync<T>(Func<MeydanDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<MeydanDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        if (_database is not null)
            Task.Run(() => TestDatabase.DropAsync(_database)).GetAwaiter().GetResult();
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

public static class ClientExtensions
{
    public static async Task<string> RegisterAsync(this HttpClient client, string email, string password = "secret123")
    {
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            firstName = "Aysel",
            lastName = "Məmmədova",
            phone = TestPhones.Next(),
            email,
            password,
            confirmPassword = password,
            acceptTerms = true,
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<TokenBody>();
        return body!.AccessToken;
    }

    public static async Task<string> LoginAsync(this HttpClient client, string email, string password, bool rememberMe = false)
    {
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password, rememberMe });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TokenBody>())!.AccessToken;
    }

    public static void UseToken(this HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public sealed record TokenBody(string AccessToken);
}
