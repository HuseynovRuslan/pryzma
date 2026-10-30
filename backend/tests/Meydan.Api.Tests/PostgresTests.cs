using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain;
using Meydan.Infrastructure.Persistence;
using Meydan.Infrastructure.Persistence.Seed;
using Meydan.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>Behaviour that depends on the database engine, checked against real PostgreSQL.</summary>
public sealed class PostgresTests
{
    private const string AzLetters = "Ə ə I ı İ i Ğ ğ Ş ş Ç ç Ö ö Ü ü";

    [Fact]
    public async Task A_fresh_empty_database_is_migrated_from_zero_and_seeded()
    {
        using var factory = new MeydanFactory { FreshDatabase = true };
        var client = factory.CreateClient();

        var competition = await client.GetFromJsonAsync<JsonElement>("/api/competitions/baki-kendleri-sevron-dizayni");
        Assert.Equal(59, competition.GetProperty("categories").GetArrayLength());

        await factory.WithDbAsync(async db =>
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_InitialPostgres"));
            Assert.Equal(Roles.All.Order(), (await db.Roles.Select(r => r.Name!).ToListAsync()).Order());
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            var encoding = await db.Database
                .SqlQuery<string>($"SELECT pg_encoding_to_char(encoding) AS \"Value\" FROM pg_database WHERE datname = current_database()")
                .ToListAsync();
            Assert.Equal("UTF8", encoding.Single());
        });

        // Identity + JWT + refresh cookie work end to end on the fresh database.
        await client.RegisterAsync("fresh@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Azerbaijani_letters_round_trip_exactly_through_api_and_database()
    {
        using var factory = new MeydanFactory();
        var admin = factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, factory.AdminPassword));

        var name = $"Hərflər: {AzLetters}";
        var created = await admin.PostAsJsonAsync("/api/admin/projects", new
        {
            name, slug = "herfler", shortDescription = $"Şüvəlan, Əmircan, Bülbülə, Görədil, Kürdəxanı, Pirşağı, Maştağa — {AzLetters}",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await admin.PutAsJsonAsync($"/api/admin/projects/{projectId}/status", new { status = "Published" })).EnsureSuccessStatusCode();

        var project = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/projects/herfler");
        Assert.Equal(name, project.GetProperty("name").GetString());
        Assert.EndsWith(AzLetters, project.GetProperty("shortDescription").GetString());

        await factory.WithDbAsync(async db =>
        {
            // Exact equality in SQL, not just in .NET.
            Assert.Equal(1, await db.Projects.CountAsync(p => p.Name == name));
            var stored = await db.Database
                .SqlQuery<int>($"SELECT length(\"Name\") AS \"Value\" FROM \"Projects\" WHERE \"Slug\" = 'herfler'")
                .ToListAsync();
            Assert.Equal(name.Length, stored.Single()); // characters, not bytes: nothing was mangled
        });

        // Identity data too.
        var user = factory.CreateClient();
        var token = await user.RegisterAsync("unicode@example.com");
        user.UseToken(token);
        await user.PutAsJsonAsync("/api/me/profile", new { firstName = "İlqar", lastName = "Şükürlü-Çələbiyev Ğ ğ Ö ö Ü ü ı", phone = TestPhones.Next() });
        var me = await user.GetFromJsonAsync<JsonElement>("/api/me/profile");
        Assert.Equal("İlqar", me.GetProperty("firstName").GetString());
        Assert.Equal("Şükürlü-Çələbiyev Ğ ğ Ö ö Ü ü ı", me.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task The_59_settlements_are_stored_and_read_back_exactly_in_order()
    {
        using var factory = new MeydanFactory();
        await factory.WithDbAsync(async db =>
        {
            var names = await db.CompetitionCategories
                .Where(c => c.Competition.Slug == DbSeeder.ChevronCompetitionSlug)
                .OrderBy(c => c.SortOrder)
                .Select(c => c.Name)
                .ToListAsync();
            Assert.Equal(BakuSettlements.Names, names);

            foreach (var probe in new[] { "Maştağa", "Şüvəlan", "Əmircan", "Kürdəxanı", "Bülbülə", "Pirşağı", "Zığ", "M.Ə. Rəsulzadə" })
                Assert.Equal(1, await db.CompetitionCategories.CountAsync(c => c.Name == probe));
        });
    }

    [Fact]
    public async Task Reference_numbers_stay_unique_and_gapless_under_parallel_load()
    {
        using var factory = new MeydanFactory();
        factory.CreateClient(); // start the host (migrations + seed)

        var references = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ReferenceNumberGenerator>().NextAsync("SUB", default);
        }));

        var year = factory.Clock.Now.Year;
        Assert.Equal(40, references.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 40).Select(n => $"SUB-{year}-{n:D5}"), references.Order());
    }

    [Fact]
    public async Task Dates_are_stored_as_utc_instants_whatever_offset_they_arrive_with()
    {
        using var factory = new MeydanFactory();
        var admin = factory.CreateClient();
        admin.UseToken(await admin.LoginAsync(MeydanFactory.AdminEmail, factory.AdminPassword));
        var competitionId = factory.WithDb(db => db.Competitions.Single().Id);
        var projectId = factory.WithDb(db => db.Projects.Single().Id);

        var baku = DateTimeOffset.Parse("2026-12-31T23:59:00+04:00");
        var res = await admin.PutAsJsonAsync($"/api/admin/competitions/{competitionId}", new
        {
            projectId, title = "Bakı kəndləri üçün şevron dizayn müsabiqəsi", slug = DbSeeder.ChevronCompetitionSlug,
            shortDescription = "Qəsəbəni seçin və onun üçün şevron dizaynı təklif edin.", status = "Open",
            hasCategories = true, categoryLabel = "Qəsəbələr", endDate = "2026-12-31T23:59:00+04:00",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var stored = factory.WithDb(db => db.Competitions.AsNoTracking().Single().EndDate!.Value);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
        Assert.Equal(baku.UtcDateTime, stored.UtcDateTime);

        var column = factory.WithDb(db => db.Database
            .SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'Competitions' AND column_name = 'EndDate'")
            .ToList().Single());
        Assert.Equal("timestamp with time zone", column);

        var publicView = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/competitions/{DbSeeder.ChevronCompetitionSlug}");
        Assert.Equal(baku, publicView.GetProperty("endDate").GetDateTimeOffset());
    }
}
