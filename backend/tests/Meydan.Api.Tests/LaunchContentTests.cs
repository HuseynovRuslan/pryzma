using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Meydan.Api.Tests;

/// <summary>
/// The platform's launch content: the umbrella project "Abşeronun simvolları" and one competition per settlement —
/// Novxanı and Əmircan. The settlement is the competition, so the entry form never asks for it; everything else
/// (nominations, prizes, form) is identical between the two, and an admin owns every word afterwards.
/// </summary>
public sealed class LaunchContentTests : IDisposable
{
    private const string Novxani = DbSeeder.NovxaniCompetitionSlug;
    private const string Emircan = DbSeeder.EmircanCompetitionSlug;

    private readonly MeydanFactory _factory = new() { LaunchContent = true };

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

    private static async Task<JsonElement> DraftAsync(HttpClient client, string competition) =>
        await Json(await client.PostAsJsonAsync("/api/submissions", new { competitionSlug = competition }));

    [Theory]
    [InlineData(Novxani, "Novxanının simvolu", "Novxanının yerli kimliyini")]
    [InlineData(Emircan, "Əmircanın simvolu", "Əmircanın tarixi, memarlığı")]
    public async Task Each_settlement_has_its_own_published_competition(string slug, string title, string body)
    {
        var anonymous = _factory.CreateClient();

        var project = await Json(await anonymous.GetAsync($"/api/projects/{DbSeeder.AbseronProjectSlug}"));
        Assert.Equal("Abşeronun simvolları", project.GetProperty("name").GetString());

        var competition = await Json(await anonymous.GetAsync($"/api/competitions/{slug}"));
        Assert.Contains(title, competition.GetProperty("title").GetString());
        Assert.Equal("Published", competition.GetProperty("status").GetString());
        Assert.True(competition.GetProperty("isAcceptingSubmissions").GetBoolean());
        Assert.False(competition.GetProperty("hasCategories").GetBoolean());
        Assert.Contains(body, competition.GetProperty("description").GetString());
        Assert.Equal("Abşeronun simvolları", competition.GetProperty("project").GetProperty("name").GetString());

        // The same nominations and prizes on both, written once.
        var nominations = competition.GetProperty("nominations").GetString()!;
        foreach (var nomination in new[] { "Simvol və Loqotip Dizaynı", "Promo Məhsul Tətbiqləri", "Məkan və Küçə Tərtibatı" })
            Assert.Contains(nomination, nominations);
        var prize = competition.GetProperty("prize").GetString()!;
        foreach (var line in new[] { "1-ci yer — 250 AZN", "2-ci yer — 150 AZN", "3-cü yer — 100 AZN", "Tətbiq imkanı" })
            Assert.Contains(line, prize);
        Assert.Contains("istifadə oluna bilər", prize); // an opportunity, never a promise
        foreach (var old in new[] { "500 AZN", "300 AZN", "200 AZN" })
            Assert.DoesNotContain(old, prize); // the first draft's figures are gone

        // Everything a visitor needs before entering, and nothing invented.
        Assert.Contains("öz yaradıcı işi olmalıdır", competition.GetProperty("rules").GetString());
        Assert.Contains("Orijinallıq və yaradıcı yanaşma", competition.GetProperty("judgingCriteria").GetString());
        Assert.DoesNotContain("%", competition.GetProperty("judgingCriteria").GetString()); // no scoring formula
        var usage = competition.GetProperty("usageTerms").GetString()!;
        Assert.Contains("müəllif göstərilməklə", usage);
        Assert.Contains("ayrıca razılaşdırıla bilər", usage);
        Assert.DoesNotContain("bütün hüquqlar təşkilatçıya", usage); // no silent transfer of copyright

        // Nothing was invented: no dates, no contact details, no placeholder text anywhere public.
        foreach (var empty in new[] { "startDate", "endDate", "resultsAnnouncedOn", "exhibitionOn", "contactNote" })
            Assert.Equal(JsonValueKind.Null, competition.GetProperty(empty).ValueKind);
        var raw = competition.GetRawText();
        foreach (var placeholder in new[] { "Tarix qeyd edin", "Email ünvanı", "Nömrə", "Veb-sayt" })
            Assert.DoesNotContain(placeholder, raw);
    }

    [Theory]
    [InlineData(Novxani, "Novxanı")]
    [InlineData(Emircan, "Əmircan")]
    public async Task The_participation_section_states_the_limits_the_server_actually_enforces(string slug, string settlement)
    {
        var anonymous = _factory.CreateClient();
        var requirements = (await Json(await anonymous.GetAsync($"/api/competitions/{slug}")))
            .GetProperty("requirements").GetString()!;

        Assert.Contains("rəssamlar, qrafik dizaynerlər", requirements); // who may enter, unchanged
        Assert.Contains("Təqdimat üçün nə lazımdır", requirements);

        // The numbers are the form's, not a copy of them: read the field back and demand the same sentence.
        var (files, help) = await _factory.QueryDbAsync(async db =>
        {
            var field = await db.CompetitionFields.Include(f => f.Competition)
                .SingleAsync(f => f.Competition.Slug == slug && f.Key == "files");
            return (field.OptionsJson, field.HelpText);
        });
        var options = JsonDocument.Parse(files!).RootElement;
        var maxFiles = options.GetProperty("maxFiles").GetInt32();
        var maxMb = options.GetProperty("maxFileSizeMb").GetInt32();
        Assert.Contains($"Ən çox {maxFiles} fayl", requirements);
        Assert.Contains($"hər biri ən çox {maxMb} MB", requirements);
        foreach (var format in options.GetProperty("accept").EnumerateArray().Select(x => x.GetString()!))
            Assert.Contains(format.ToUpperInvariant(), requirements);

        // The same sentence is under the upload field itself, so the form cannot promise something else.
        Assert.Contains($"Ən çox {maxFiles} fayl", help);

        var symbols = await _factory.QueryDbAsync(async db => (await db.CompetitionFields.Include(f => f.Competition)
            .SingleAsync(f => f.Competition.Slug == slug && f.Key == "symbols")).HelpText);
        Assert.Contains(settlement, symbols); // the form asks about this settlement, not about both
    }

    /// <summary>Puts a database back into the shape the first version of the launch left it in.</summary>
    private async Task RewindToFirstLaunchAsync(Action<Competition>? andThen = null)
    {
        await _factory.WithDbAsync(async db =>
        {
            foreach (var competition in await db.Competitions.Where(c => c.Slug == Novxani || c.Slug == Emircan)
                         .Include(c => c.Fields).ToListAsync())
            {
                competition.Rules = null;
                competition.JudgingCriteria = null;
                competition.UsageTerms = null;
                competition.Requirements =
                    "Müsabiqəyə rəssamlar, qrafik dizaynerlər, memarlar, illüstratorlar və yaradıcı gənclər müraciət edə bilərlər.";
                competition.Prize = "1-ci yer — 500 AZN + Sertifikat\n2-ci yer — 300 AZN + Sertifikat\n"
                                    + "3-cü yer — 200 AZN + Sertifikat\n\nTətbiq imkanı\nİstifadə oluna bilər.";
                foreach (var field in competition.Fields)
                    field.HelpText = null;
                andThen?.Invoke(competition);
            }
            db.SeedHistory.RemoveRange(db.SeedHistory.Where(h => h.Key == "2026-09-21-launch-readiness"));
            await db.SaveChangesAsync();
        });
    }

    private async Task SeedAgainAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
    }

    [Fact]
    public async Task A_database_from_the_first_launch_is_brought_up_to_the_published_content()
    {
        await RewindToFirstLaunchAsync();

        await SeedAgainAsync();

        var published = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Novxani}"));
        Assert.Contains("1-ci yer — 250 AZN", published.GetProperty("prize").GetString()); // corrected
        Assert.DoesNotContain("500 AZN", published.GetProperty("prize").GetString());
        Assert.Contains("öz yaradıcı işi olmalıdır", published.GetProperty("rules").GetString());
        Assert.Contains("Orijinallıq", published.GetProperty("judgingCriteria").GetString());
        Assert.Contains("müəllif göstərilməklə", published.GetProperty("usageTerms").GetString());
        Assert.Contains("Ən çox 5 fayl", published.GetProperty("requirements").GetString());

        await _factory.WithDbAsync(async db =>
        {
            var files = await db.CompetitionFields.Include(f => f.Competition)
                .SingleAsync(f => f.Competition.Slug == Novxani && f.Key == "files");
            Assert.Contains("PNG", files.HelpText);
        });
    }

    [Fact]
    public async Task The_correction_stops_at_anything_an_admin_has_written()
    {
        await RewindToFirstLaunchAsync(c =>
        {
            c.Prize = "Mükafat fondu sonra elan olunacaq."; // no "500 AZN": the admin has already rewritten it
            c.Requirements = "Yalnız Abşeron sakinləri.";
            c.Rules = "Öz qaydalarımız.";
        });

        await SeedAgainAsync();

        await _factory.WithDbAsync(async db =>
        {
            var competition = await db.Competitions.SingleAsync(c => c.Slug == Novxani);
            Assert.Equal("Mükafat fondu sonra elan olunacaq.", competition.Prize);
            Assert.Equal("Yalnız Abşeron sakinləri.", competition.Requirements);
            Assert.Equal("Öz qaydalarımız.", competition.Rules);
            // What the admin never wrote is still filled in.
            Assert.Contains("Orijinallıq", competition.JudgingCriteria);
        });
    }

    [Fact]
    public async Task Short_public_lines_carry_no_full_stop_and_the_ai_note_is_where_entrants_look()
    {
        var anonymous = _factory.CreateClient();

        var settings = await Json(await anonymous.GetAsync("/api/settings"));
        foreach (var key in new[] { "homeHeroTitle", "homeHeroSubtitle", "authPromoTitle" })
            Assert.False(settings.GetProperty(key).GetString()!.EndsWith('.'), key);

        var project = await Json(await anonymous.GetAsync($"/api/projects/{DbSeeder.AbseronProjectSlug}"));
        Assert.False(project.GetProperty("shortDescription").GetString()!.EndsWith('.'));

        foreach (var slug in new[] { Novxani, Emircan })
        {
            var competition = await Json(await anonymous.GetAsync($"/api/competitions/{slug}"));
            Assert.False(competition.GetProperty("shortDescription").GetString()!.EndsWith('.'), slug);

            // Prose is prose: the long description keeps every full stop it was written with.
            Assert.EndsWith(".", competition.GetProperty("description").GetString());

            Assert.Contains("Süni intellekt vasitəsilə hazırlanmış təqdimatlar da qəbul edilir",
                competition.GetProperty("requirements").GetString());
        }
    }

    [Fact]
    public async Task The_full_stops_come_off_the_seeded_short_lines_and_never_off_an_admin_s_own()
    {
        // There is one settings row and it is written the first time anybody reads it, not by the seeder.
        await Json(await _factory.CreateClient().GetAsync("/api/settings"));

        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.PlatformSettings.FirstAsync();
            settings.HomeHeroSubtitle = PlatformDefaults.HomeHeroSubtitle + ".";
            settings.AuthPromoTitle = "Bizim öz sözümüz."; // the admin has already rewritten this one

            foreach (var competition in await db.Competitions
                         .Where(c => c.Slug == Novxani || c.Slug == Emircan).ToListAsync())
                competition.ShortDescription += ".";

            db.SeedHistory.RemoveRange(db.SeedHistory.Where(h => h.Key == "2026-09-23-short-copy-and-ai-note"));
            await db.SaveChangesAsync();
        });

        await SeedAgainAsync();

        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.PlatformSettings.FirstAsync();
            Assert.Equal(PlatformDefaults.HomeHeroSubtitle, settings.HomeHeroSubtitle);
            Assert.Equal("Bizim öz sözümüz.", settings.AuthPromoTitle); // the admin's full stop is the admin's

            foreach (var competition in await db.Competitions
                         .Where(c => c.Slug == Novxani || c.Slug == Emircan).ToListAsync())
                Assert.False(competition.ShortDescription.EndsWith('.'), competition.Slug);
        });
    }

    [Fact]
    public async Task The_two_competitions_are_what_the_public_sees()
    {
        var anonymous = _factory.CreateClient();
        var competitions = await Json(await anonymous.GetAsync("/api/competitions"));
        var slugs = competitions.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToList();
        // Both are public, Novxanı before Əmircan — the order the two posters take on the home page.
        Assert.Equal([Novxani, Emircan], slugs.Where(s => s == Novxani || s == Emircan).ToArray());
        Assert.DoesNotContain(DbSeeder.SymbolsCompetitionSlug, slugs);
    }

    [Fact]
    public async Task The_competition_that_asked_for_the_settlement_is_archived_not_deleted()
    {
        // A database still carrying the launch's first shape: one competition for both settlements, nobody entered it.
        await _factory.WithDbAsync(async db =>
        {
            var project = await db.Projects.SingleAsync(p => p.Slug == DbSeeder.AbseronProjectSlug);
            db.Competitions.Add(new Competition
            {
                ProjectId = project.Id,
                Slug = DbSeeder.SymbolsCompetitionSlug,
                Title = "“Novxanı və Əmircanın simvolları” yaradıcılıq müsabiqəsi",
                ShortDescription = "Hər iki qəsəbə üçün bir müsabiqə.",
                Status = CompetitionStatus.Published,
            });
            db.SeedHistory.RemoveRange(db.SeedHistory.Where(h => h.Key == "2026-09-21-settlement-competitions"));
            await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();

        await _factory.WithDbAsync(async db =>
        {
            var old = await db.Competitions.SingleAsync(c => c.Slug == DbSeeder.SymbolsCompetitionSlug);
            Assert.Equal(CompetitionStatus.Archived, old.Status); // archived, never deleted
        });
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/competitions/{DbSeeder.SymbolsCompetitionSlug}")).StatusCode);
    }

    [Theory]
    [InlineData(Novxani)]
    [InlineData(Emircan)]
    public async Task The_form_asks_for_the_nomination_and_never_for_the_settlement(string slug)
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        var fields = (await DraftAsync(client, slug)).GetProperty("fields").EnumerateArray().ToList();

        Assert.Equal(["nomination", "title", "concept", "symbols", "files", "notes"],
            fields.Select(f => f.GetProperty("key").GetString()).ToArray());
        var nomination = fields[0];
        Assert.Equal("Nominasiya", nomination.GetProperty("label").GetString());
        Assert.Equal("Select", nomination.GetProperty("fieldType").GetString());
        Assert.True(nomination.GetProperty("isRequired").GetBoolean());
        Assert.Equal(["Simvol və Loqotip Dizaynı", "Promo Məhsul Tətbiqləri", "Məkan və Küçə Tərtibatı"],
            nomination.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray());
        Assert.True(fields.Single(f => f.GetProperty("key").GetString() == "files").GetProperty("isRequired").GetBoolean());
        Assert.False(fields.Single(f => f.GetProperty("key").GetString() == "notes").GetProperty("isRequired").GetBoolean());
    }

    [Fact]
    public async Task An_entry_belongs_to_one_settlement_because_of_the_competition_it_is_in()
    {
        var client = _factory.CreateClient();
        client.UseToken(await client.RegisterAsync($"p-{Guid.NewGuid():N}@example.com"));
        var id = (await DraftAsync(client, Emircan)).GetProperty("id").GetInt32();

        // A nomination that is not on the list is refused, whatever the participant sends.
        var invalid = await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new { nomination = "Başqa", title = "İş", concept = "Konsept", symbols = "Simvollar" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("Nominasiya", await invalid.Content.ReadAsStringAsync());

        await Json(await client.PutAsJsonAsync($"/api/submissions/{id}", new
        {
            answers = new
            {
                nomination = "Məkan və Küçə Tərtibatı", title = "Əhəngdaşı relyefi",
                concept = "Əmircanın küçə divarları üçün relyef konsepti.", symbols = "Əhəngdaşı, xilə-buta.",
            },
        }));
        var form = new MultipartFormDataContent { { new StringContent("files"), "fieldKey" } };
        var file = new ByteArrayContent(TestImages.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "relyef.png");
        await Json(await client.PostAsync($"/api/submissions/{id}/files", form));
        var submitted = await Json(await client.PostAsync($"/api/submissions/{id}/submit", null));

        Assert.StartsWith("SUB-", submitted.GetProperty("referenceNumber").GetString());
        await _factory.WithDbAsync(async db =>
        {
            var answers = await db.SubmissionAnswers.Include(a => a.Field).Where(a => a.SubmissionId == id).ToListAsync();
            Assert.Equal("Məkan və Küçə Tərtibatı", answers.Single(a => a.Field.Key == "nomination").Value);
            Assert.DoesNotContain(answers, a => a.Field.Key == "settlement");
            var entry = await db.Submissions.Include(s => s.Competition).SingleAsync(s => s.Id == id);
            Assert.Equal(Emircan, entry.Competition.Slug); // the settlement, asked once
            Assert.Equal("Əhəngdaşı relyefi", entry.Title);
            // The nomination the participant chose in the form is also recorded as the work's category, on the
            // server, without asking them a second time — this is what puts the work on a ballot.
            var nomination = await db.CompetitionCategories.SingleAsync(c => c.Id == entry.CompetitionCategoryId);
            Assert.Equal("Məkan və Küçə Tərtibatı", nomination.Name);
            Assert.Equal("mekan-kuce", nomination.Slug);
        });
    }

    [Fact]
    public async Task The_admin_owns_every_word_and_date_afterwards()
    {
        var admin = await AdminAsync();
        var current = (await Json(await admin.GetAsync("/api/admin/competitions")))
            .EnumerateArray().Single(c => c.GetProperty("slug").GetString() == Novxani);
        var id = current.GetProperty("id").GetInt32();

        var updated = await Json(await admin.PutAsJsonAsync($"/api/admin/competitions/{id}", new
        {
            projectId = current.GetProperty("projectId").GetInt32(),
            title = current.GetProperty("title").GetString(),
            slug = Novxani,
            shortDescription = current.GetProperty("shortDescription").GetString(),
            description = current.GetProperty("description").GetString(),
            rules = "",
            requirements = current.GetProperty("requirements").GetString(),
            nominations = "Yeni nominasiya\nİzah.",
            contactNote = "Suallar üçün: 012 000 00 00",
            endDate = "2026-11-15T19:59:00+00:00",
            resultsAnnouncedOn = "2026-11-25T08:00:00+00:00",
            exhibitionOn = "2026-12-05T08:00:00+00:00",
            prize = "1-ci yer — 500 AZN",
            hasCategories = false,
            categoryLabel = "",
            maxSubmissionsPerUser = (int?)null,
            sortOrder = 1,
        }));

        Assert.Equal("Yeni nominasiya\nİzah.", updated.GetProperty("nominations").GetString());
        Assert.Equal("Suallar üçün: 012 000 00 00", updated.GetProperty("contactNote").GetString());
        var published = await Json(await _factory.CreateClient().GetAsync($"/api/competitions/{Novxani}"));
        Assert.Equal("Yeni nominasiya\nİzah.", published.GetProperty("nominations").GetString());
        foreach (var date in new[] { "endDate", "resultsAnnouncedOn", "exhibitionOn" })
            Assert.NotEqual(JsonValueKind.Null, published.GetProperty(date).ValueKind);
    }

    [Fact]
    public async Task Seeding_again_changes_nothing()
    {
        await _factory.WithDbAsync(async db =>
        {
            var competition = await db.Competitions.Include(c => c.Fields).SingleAsync(c => c.Slug == Emircan);
            competition.Description = "Admin-in yazdığı mətn";
            competition.Fields.Single(f => f.Key == "notes").IsActive = false;
            await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();

        await _factory.WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.Projects.CountAsync(p => p.Slug == DbSeeder.AbseronProjectSlug));
            Assert.Equal(1, await db.Competitions.CountAsync(c => c.Slug == Emircan));
            var competition = await db.Competitions.Include(c => c.Fields).SingleAsync(c => c.Slug == Emircan);
            Assert.Equal("Admin-in yazdığı mətn", competition.Description); // an admin's edit is never overwritten
            Assert.Equal(6, competition.Fields.Count);
            Assert.False(competition.Fields.Single(f => f.Key == "notes").IsActive);
        });
    }
}
