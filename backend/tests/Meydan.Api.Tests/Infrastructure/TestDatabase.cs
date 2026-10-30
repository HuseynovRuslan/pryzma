using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Meydan.Api.Tests.Infrastructure;

/// <summary>
/// Real PostgreSQL for integration tests — no in-memory stand-in. The server comes from
/// TestDatabase:AdminConnectionString (user-secrets locally, TestDatabase__AdminConnectionString in CI):
/// a role allowed to create databases. Each test gets its own database, cloned from a template that is
/// migrated once per test run, and dropped afterwards.
/// </summary>
public static class TestDatabase
{
    private const string TemplateName = "meydan_test_template";
    private static readonly SemaphoreSlim TemplateLock = new(1, 1);
    private static bool _templateReady;

    public static string AdminConnectionString { get; } = LoadAdminConnectionString();

    private static string LoadAdminConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets(typeof(TestDatabase).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config["TestDatabase:AdminConnectionString"]
               ?? throw new InvalidOperationException(
                   "TestDatabase:AdminConnectionString is not set. Run scripts/dev-postgres.ps1 (it writes the test " +
                   "user-secret), or set environment variable TestDatabase__AdminConnectionString to a PostgreSQL " +
                   "connection string whose role may CREATE DATABASE.");
    }

    public static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database, Pooling = true }.ConnectionString;

    /// <summary>Creates a database. Cloned from the migrated template, or completely empty when <paramref name="fresh"/>.</summary>
    public static async Task<string> CreateAsync(bool fresh)
    {
        var name = $"meydan_test_{Guid.NewGuid():N}";
        if (!fresh)
            await EnsureTemplateAsync();
        await ExecuteAsync(fresh
            ? $"""CREATE DATABASE "{name}" ENCODING 'UTF8' TEMPLATE template0 {Strategy}"""
            : $"""CREATE DATABASE "{name}" TEMPLATE "{TemplateName}" {Strategy}""");
        return name;
    }

    /// <summary>
    /// Each clone is written and synced by the CREATE that makes it. The default (WAL_LOG) leaves a clone's ~550 files
    /// to the next checkpoint, and every DROP DATABASE forces one: the test that dropped next paid for all the clones
    /// made since — measured on the dev server, 20 s after twelve clones and 35 s after the opening burst of a run,
    /// past Npgsql's 30 s, which failed tests whose assertions had passed. FILE_COPY: 0.3 s, clones or not.
    /// </summary>
    private const string Strategy = "STRATEGY = FILE_COPY";

    public static async Task DropAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync($"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""");
    }

    /// <summary>Once per test run: rebuild the template from the current migrations.</summary>
    private static async Task EnsureTemplateAsync()
    {
        if (_templateReady) return;
        await TemplateLock.WaitAsync();
        try
        {
            if (_templateReady) return;
            await ExecuteAsync($"""DROP DATABASE IF EXISTS "{TemplateName}" WITH (FORCE)""");
            await ExecuteAsync($"""CREATE DATABASE "{TemplateName}" ENCODING 'UTF8' TEMPLATE template0 {Strategy}""");
            var options = new DbContextOptionsBuilder<MeydanDbContext>()
                .UseNpgsql(ConnectionStringFor(TemplateName))
                .Options;
            await using (var db = new MeydanDbContext(options, TimeProvider.System))
                await db.Database.MigrateAsync();
            NpgsqlConnection.ClearAllPools(); // a template must have no open connections to be copied
            _templateReady = true;
        }
        finally
        {
            TemplateLock.Release();
        }
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor("postgres"));
        await connection.OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                return;
            }
            // CREATE DATABASE … TEMPLATE fails while another session is copying the same template; retry briefly.
            catch (PostgresException ex) when (ex.SqlState == "55006" && attempt < 20)
            {
                await Task.Delay(100 * attempt);
            }
        }
    }
}
