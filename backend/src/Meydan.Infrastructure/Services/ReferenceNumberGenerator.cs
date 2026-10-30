using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meydan.Infrastructure.Services;

/// <summary>Mints sequential, human-friendly references such as SUB-2026-00124.</summary>
public sealed class ReferenceNumberGenerator(MeydanDbContext db, TimeProvider clock)
{
    public const string SubmissionPrefix = "SUB";

    /// <summary>
    /// One atomic PostgreSQL upsert: the row for (prefix, year) is created or incremented under a row lock and
    /// the new value returned in the same statement, so concurrent callers can never receive the same number.
    /// </summary>
    public async Task<string> NextAsync(string prefix, CancellationToken ct)
    {
        var year = clock.GetUtcNow().Year;
        var values = await db.Database.SqlQuery<int>($"""
            INSERT INTO "ReferenceCounters" ("Prefix", "Year", "Value")
            VALUES ({prefix}, {year}, 1)
            ON CONFLICT ("Prefix", "Year") DO UPDATE SET "Value" = "ReferenceCounters"."Value" + 1
            RETURNING "Value"
            """).ToListAsync(ct);
        return Format(prefix, year, values.Single());
    }

    public static string Format(string prefix, int year, int value) => $"{prefix}-{year}-{value:D5}";
}
