using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Common;

/// <summary>
/// Schema changes are a deliberate deployment step, never a side effect of a web process starting.
/// <list type="bullet">
/// <item>Development (Database:MigrateOnStartup = true): pending migrations are applied at startup.</item>
/// <item>Everywhere else: run <c>Meydan.Api migrate</c> (or an idempotent SQL script) before starting the new
/// version. The API refuses to start against a database with pending migrations instead of running on an
/// outdated schema.</item>
/// </list>
/// The content seed is idempotent and only runs against an up-to-date schema.
/// </summary>
public static class DatabaseStartup
{
    public static async Task InitializeAsync(WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:InitializeOnStartup", true))
            return;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
                throw new InvalidOperationException(
                    $"The database has {pending.Count} pending migration(s) ({string.Join(", ", pending)}). " +
                    "Apply them as a deployment step first: `dotnet Meydan.Api.dll migrate` " +
                    "(or the idempotent script from `dotnet ef migrations script --idempotent`).");
        }
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
    }

    public static async Task MigrateAndSeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MeydanDbContext>>();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
        logger.LogInformation("Database is up to date");
    }
}
