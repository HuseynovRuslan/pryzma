using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Api.Features.Demo;

/// <summary>
/// <c>Meydan.Api seed-demo [--assets &lt;folder&gt;]</c> and <c>Meydan.Api remove-demo</c>: explicit operator commands,
/// never run at startup. They refuse a database with pending migrations, and outside Development they refuse to run
/// without <c>--allow-production</c> — which is meant to be typed only after a database backup.
/// </summary>
public static class DemoCommands
{
    public const string Seed = "seed-demo";
    public const string Remove = "remove-demo";
    public const string AllowProduction = "--allow-production";
    private const string AssetsOption = "--assets";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Seed, StringComparison.OrdinalIgnoreCase) || string.Equals(a, Remove, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IHostEnvironment environment)
    {
        var seed = args.Any(a => string.Equals(a, Seed, StringComparison.OrdinalIgnoreCase));
        var remove = args.Any(a => string.Equals(a, Remove, StringComparison.OrdinalIgnoreCase));
        if (seed == remove)
        {
            Console.Error.WriteLine($"Choose one: {Seed} or {Remove}.");
            return 2;
        }
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") && !args.Contains(AllowProduction))
        {
            Console.Error.WriteLine($"Environment is {environment.EnvironmentName}. Take a database backup first, then repeat with {AllowProduction}.");
            return 2;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            Console.Error.WriteLine($"The database has {pending.Count} pending migration(s). Run `migrate` first.");
            return 3;
        }

        var demo = scope.ServiceProvider.GetRequiredService<DemoData>();
        if (seed)
        {
            var folder = AssetsFolder(args);
            if (folder is null)
            {
                Console.Error.WriteLine($"No demo-assets/manifest.json found. Pass {AssetsOption} <folder>.");
                return 2;
            }
            var report = await demo.SeedAsync(folder, CancellationToken.None);
            Console.WriteLine($"Demo data seeded from {folder}");
            Console.WriteLine($"  projects created:     {report.ProjectsCreated}");
            Console.WriteLine($"  competitions created: {report.CompetitionsCreated}");
            Console.WriteLine($"  participants created: {report.UsersCreated}");
            Console.WriteLine($"  submissions created:  {report.SubmissionsCreated}");
            Console.WriteLine($"  files imported:       {report.FilesImported}");
            Console.WriteLine($"  winners chosen:       {report.WinnersCreated}");
            Console.WriteLine($"  result cover set:     {(report.ResultCoverSet ? "yes" : "no")}");
            foreach (var note in report.Notes) Console.WriteLine($"  note: {note}");
        }
        else
        {
            var report = await demo.RemoveAsync(CancellationToken.None);
            Console.WriteLine("Demo data removed");
            Console.WriteLine($"  projects:      {report.ProjectsRemoved}");
            Console.WriteLine($"  competitions:  {report.CompetitionsRemoved}");
            Console.WriteLine($"  participants:  {report.UsersRemoved}");
            Console.WriteLine($"  submissions:   {report.SubmissionsRemoved}");
            Console.WriteLine($"  winners:       {report.WinnersRemoved}");
            Console.WriteLine($"  stored files:  {report.StoredFilesDeleted}");
            Console.WriteLine($"  result cover:  {(report.ResultCoverRemoved ? "removed" : "none")}");
            foreach (var kept in report.Kept) Console.WriteLine($"  kept: {kept}");
        }
        return 0;
    }

    /// <summary>--assets &lt;folder&gt;, else the nearest demo-assets/ with a manifest above the working or app folder.</summary>
    private static string? AssetsFolder(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, AssetsOption, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            return index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "demo-assets");
                if (File.Exists(Path.Combine(candidate, "manifest.json")))
                    return candidate;
            }
        return null;
    }
}
