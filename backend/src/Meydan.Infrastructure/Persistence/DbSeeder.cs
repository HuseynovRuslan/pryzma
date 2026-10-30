using System.Text.Json;
using Meydan.Domain;
using Meydan.Domain.Common;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>
    /// Optional bootstrap admin. Nothing is created unless both values are set. Supply them through
    /// user-secrets or environment variables only — never appsettings files.
    /// </summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }

    /// <summary>
    /// Whether the seed also writes the platform's live content — today "Abşeronun simvolları" and its competition.
    /// On everywhere by default; the API test factory turns it off so catalog tests can assert an exact baseline, and
    /// the tests that cover the content itself turn it back on.
    /// </summary>
    public bool LaunchContent { get; set; } = true;
}

/// <summary>
/// Seeds roles, an optional bootstrap admin and the platform's first real content.
/// Content changes are named steps recorded in <see cref="SeedHistory"/>: each runs once per database, so
/// anything an admin edits or deletes afterwards stays that way. Dates, prize, rules and submission limits
/// are intentionally absent until supplied by the owner.
/// </summary>
public sealed class DbSeeder(
    MeydanDbContext db,
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<ApplicationUser> users,
    IOptions<SeedOptions> options,
    TimeProvider clock,
    ILogger<DbSeeder> logger)
{
    public const string BakiProjectSlug = "baki-kendleri";
    public const string ChevronCompetitionSlug = "baki-kendleri-sevron-dizayni";
    public const string AbseronProjectSlug = "abseronun-simvollari";
    /// <summary>The first shape of the launch: one competition for both settlements, superseded by the two below.</summary>
    public const string SymbolsCompetitionSlug = "novxani-ve-emircanin-simvollari";
    public const string NovxaniCompetitionSlug = "novxaninin-simvolu";
    public const string EmircanCompetitionSlug = "emircanin-simvolu";

    /// <summary>
    /// The umbrella project's own line under its tile. A label, not a sentence, so it carries no full stop —
    /// the same rule the competition short descriptions follow.
    /// </summary>
    public const string AbseronProjectShortDescription =
        "Abşeron qəsəbələrinin tarixini, mədəni irsini və yerli kimliyini müasir dizayn dili ilə yenidən kəşf edən "
        + "yaradıcı layihə";

    /// <summary>The project's own cover, shipped with the site (frontend/public). A later admin upload replaces it.</summary>
    public const string BakiCoverImage = "/images/projects/baki-kendleri-cover.png";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        await SeedAdminAsync();
        await RunStepAsync("2026-09-18-baki-project", SeedBakiProjectAsync, ct);
        await RunStepAsync("2026-09-20-chevron-settlements", SeedSettlementsAsync, ct);
        await RunStepAsync("2026-09-20-chevron-form", SeedChevronFormAsync, ct);
        await RunStepAsync("2026-09-21-baki-cover", SeedBakiCoverAsync, ct);
        if (options.Value.LaunchContent)
        {
            await RunStepAsync("2026-09-21-abseron-symbols", SeedAbseronProjectAsync, ct);
            await RunStepAsync("2026-09-21-settlement-competitions", SeedSettlementCompetitionsAsync, ct);
            await RunStepAsync("2026-09-21-launch-readiness", SeedLaunchReadinessAsync, ct);
        }
        // Outside the launch-content gate on purpose: the platform's own lines exist on every deployment, and the
        // competition part simply finds nothing where those rows were never seeded.
        await RunStepAsync("2026-09-23-short-copy-and-ai-note", SeedShortCopyAndAiNoteAsync, ct);
        await RunStepAsync("2026-09-30-nominations-as-categories", SeedNominationsAsCategoriesAsync, ct);
    }

    /// <summary>
    /// Gives Bakı kəndləri its real cover through the project's own CoverImage — never over one an admin has already
    /// uploaded. Projects without a cover keep the generated fallback.
    /// </summary>
    private async Task SeedBakiCoverAsync(CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == BakiProjectSlug, ct);
        if (project is not null && string.IsNullOrWhiteSpace(project.CoverImage))
            project.CoverImage = BakiCoverImage;
    }

    private async Task RunStepAsync(string key, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        if (await db.SeedHistory.AnyAsync(h => h.Key == key, ct))
            return;
        await step(ct);
        db.SeedHistory.Add(new SeedHistory { Key = key, AppliedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seed step {Key} applied", key);
    }

    private async Task SeedAdminAsync()
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AdminEmail) || string.IsNullOrWhiteSpace(o.AdminPassword))
        {
            logger.LogInformation("No bootstrap admin configured (Seed:AdminEmail / Seed:AdminPassword)");
            return;
        }
        if (await users.FindByEmailAsync(o.AdminEmail) is not null)
            return;

        var now = clock.GetUtcNow();
        var admin = new ApplicationUser
        {
            UserName = o.AdminEmail,
            Email = o.AdminEmail,
            EmailConfirmed = true,
            FirstName = "Admin",
            LastName = "MEYDAN",
            TermsAcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var result = await users.CreateAsync(admin, o.AdminPassword);
        if (!result.Succeeded)
        {
            logger.LogWarning("Bootstrap admin was not created: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRolesAsync(admin, [Roles.Admin, Roles.Participant]);
        logger.LogInformation("Bootstrap admin created");
    }

    private async Task SeedBakiProjectAsync(CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == BakiProjectSlug, ct);
        if (project is null)
        {
            project = new Project
            {
                Name = "Bakı kəndləri",
                Slug = BakiProjectSlug,
                ShortDescription = "Bakı kəndlərinin kimliyini yaradıcı işlərlə təqdim edən layihə.",
                AccentColor = "#56652A",
                Status = ProjectStatus.Published,
                SortOrder = 1,
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
        }

        if (!await db.Competitions.AnyAsync(c => c.Slug == ChevronCompetitionSlug, ct))
        {
            db.Competitions.Add(new Competition
            {
                ProjectId = project.Id,
                Title = "Bakı kəndləri üçün şevron dizayn müsabiqəsi",
                Slug = ChevronCompetitionSlug,
                ShortDescription = "Qəsəbəni seçin və onun üçün şevron dizaynı təklif edin.",
                Status = CompetitionStatus.Published,
                HasCategories = true,
                CategoryLabel = "Qəsəbələr",
                SortOrder = 1,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Replaces the Phase 1 development placeholders with the 59 official settlements.</summary>
    private async Task SeedSettlementsAsync(CancellationToken ct)
    {
        var competition = await db.Competitions.Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Slug == ChevronCompetitionSlug, ct);
        if (competition is null)
            return;

        var placeholders = competition.Categories
            .Where(c => c.Slug.StartsWith("numune-", StringComparison.Ordinal))
            .ToList();
        foreach (var p in placeholders)
        {
            if (!await db.Submissions.AnyAsync(s => s.CompetitionCategoryId == p.Id, ct))
                db.CompetitionCategories.Remove(p);
        }

        // Phase 1 wording ("Kəndlər", "Kəndinizi seçin…") → settlements. Only if nobody changed it since.
        if (competition.CategoryLabel == "Kəndlər")
            competition.CategoryLabel = "Qəsəbələr";
        if (competition.ShortDescription == "Kəndinizi seçin və onun üçün şevron dizaynı təklif edin.")
            competition.ShortDescription = "Qəsəbəni seçin və onun üçün şevron dizaynı təklif edin.";

        var existing = competition.Categories.Select(c => c.Slug).ToHashSet();
        var order = 0;
        foreach (var name in BakuSettlements.Names)
        {
            order++;
            var slug = Slug.From(name);
            if (!existing.Add(slug))
                continue;
            db.CompetitionCategories.Add(new CompetitionCategory
            {
                CompetitionId = competition.Id,
                Name = name,
                Slug = slug,
                SortOrder = order,
                IsActive = true,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The chevron form as configuration rows — an admin can change any of it later.</summary>
    private async Task SeedChevronFormAsync(CancellationToken ct)
    {
        var competition = await db.Competitions.Include(c => c.Fields)
            .FirstOrDefaultAsync(c => c.Slug == ChevronCompetitionSlug, ct);
        if (competition is null || competition.Fields.Count > 0)
            return;

        var fileRules = JsonSerializer.Serialize(new
        {
            maxFiles = 5,
            maxFileSizeMb = 10,
            accept = new[] { "png", "jpeg", "webp", "pdf" },
        });

        competition.Fields.Add(new CompetitionField
        {
            Key = "title", Label = "İşin adı", FieldType = CompetitionFieldType.ShortText,
            IsRequired = true, IsSubmissionTitle = true, IsPublicAfterResults = true, SortOrder = 1,
            OptionsJson = """{"maxLength":150}""",
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "concept", Label = "Konseptin izahı", FieldType = CompetitionFieldType.LongText,
            IsRequired = true, SortOrder = 2,
            HelpText = "Şevronun ideyasını və seçdiyiniz qəsəbə ilə bağlılığını izah edin.",
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "symbols", Label = "İstifadə olunan simvollar və onların mənası",
            FieldType = CompetitionFieldType.LongText, IsRequired = true, SortOrder = 3,
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "notes", Label = "Əlavə qeyd", FieldType = CompetitionFieldType.LongText,
            IsRequired = false, SortOrder = 4,
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "files", Label = "Fayllar", FieldType = CompetitionFieldType.File,
            IsRequired = true, SortOrder = 5, OptionsJson = fileRules,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The umbrella project the settlement competitions hang from. Public visitors reach a competition directly, so
    /// this is mostly an admin grouping — but it stays a real project, with its own page and words an admin owns.
    /// </summary>
    private async Task SeedAbseronProjectAsync(CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(p => p.Slug == AbseronProjectSlug, ct))
            return;

        db.Projects.Add(new Project
        {
            Name = "Abşeronun simvolları",
            Slug = AbseronProjectSlug,
            ShortDescription = AbseronProjectShortDescription,
            Description = "Abşeronun zəngin tarixi, özünəməxsus memarlığı və mədəni irsindən ilhamlanan layihə qəsəbələrimizin "
                          + "simvollarını müasir dizayn, vizual kommunikasiya və şəhər mühitində yeni formada təqdim etməyi hədəfləyir.",
            AccentColor = "#8C6A3F",
            Status = ProjectStatus.Published,
            SortOrder = 1,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One competition per settlement — Novxanı and Əmircan — instead of one competition that asked which settlement
    /// the entry was for. The competition now carries that answer, so the form is one question shorter. Content only:
    /// every word, date and prize stays editable in the admin afterwards, and no date or contact line is invented.
    /// </summary>
    private async Task SeedSettlementCompetitionsAsync(CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == AbseronProjectSlug, ct);
        if (project is null)
            return;

        // The competition the two below replace. Archived, never deleted — and left alone if anyone entered it.
        var combined = await db.Competitions
            .FirstOrDefaultAsync(c => c.Slug == SymbolsCompetitionSlug && c.Status != CompetitionStatus.Archived, ct);
        if (combined is not null)
        {
            if (await db.Submissions.AnyAsync(s => s.CompetitionId == combined.Id, ct))
                logger.LogWarning("Competition {Slug} has entries and was left published", combined.Slug);
            else
                combined.Status = CompetitionStatus.Archived;
        }

        await AddSettlementCompetitionAsync(project.Id, new SettlementCompetition(
            Slug: NovxaniCompetitionSlug,
            Settlement: "Novxanı",
            Title: "\u201CNovxanının simvolu\u201D yaradıcılıq müsabiqəsi",
            ShortDescription: SettlementCompetitionContent.NovxaniShortDescription,
            Description: "Abşeronun zəngin tarixi və özünəməxsus mədəni irsi ilə seçilən Novxanının simvollarını müasir dizayn "
                         + "konseptlərində yenidən kəşf etməyə dəvət edirik.\n\n"
                         + "Müsabiqənin məqsədi Novxanının yerli kimliyini ifadə edən simvol və dizaynların yaradılması, həmin "
                         + "vizual həllərin promo məhsullarda, şəhər və küçə tərtibatında, informasiya panellərində və digər "
                         + "yaradıcı tətbiqlərdə istifadəsi üçün ideyaların ortaya çıxarılmasıdır.",
            SortOrder: 1), ct);

        await AddSettlementCompetitionAsync(project.Id, new SettlementCompetition(
            Slug: EmircanCompetitionSlug,
            Settlement: "Əmircan",
            Title: "\u201CƏmircanın simvolu\u201D yaradıcılıq müsabiqəsi",
            ShortDescription: SettlementCompetitionContent.EmircanShortDescription,
            Description: "Abşeronun mədəni və yaradıcı irsində xüsusi yeri olan Əmircanın kimliyini müasir dizayn dili ilə "
                         + "yenidən təqdim etməyə dəvət edirik.\n\n"
                         + "Müsabiqənin məqsədi Əmircanın tarixi, memarlığı, mədəniyyəti və yaradıcı irsindən ilhamlanan simvol "
                         + "və dizaynların hazırlanması, onların promo məhsullarda və şəhər mühitində tətbiq oluna biləcək vizual "
                         + "konseptlərə çevrilməsidir.\n\n"
                         + "İlham üçün: Əzim Əzimzadə, Səttar Bəhlulzadə, Abşeron memarlığı, xalçaçılıq və ornament ənənələri, "
                         + "Xilə-buta. Bunlar yalnız istiqamətdir — heç biri məcburi deyil.",
            SortOrder: 2), ct);

        await db.SaveChangesAsync(ct);
    }

    private sealed record SettlementCompetition(
        string Slug, string Settlement, string Title, string ShortDescription, string Description, int SortOrder);

    /// <summary>
    /// The nominations, the prizes and the entry form are the same for both settlements; only the words above them
    /// differ. Written once here so the two competitions cannot drift apart before an admin edits them.
    /// </summary>
    private async Task AddSettlementCompetitionAsync(int projectId, SettlementCompetition text, CancellationToken ct)
    {
        if (await db.Competitions.AnyAsync(c => c.Slug == text.Slug, ct))
            return;

        var fileRules = JsonSerializer.Serialize(new
        {
            maxFiles = 5,
            maxFileSizeMb = 10,
            accept = new[] { "png", "jpeg", "webp", "pdf" },
        });

        var competition = new Competition
        {
            ProjectId = projectId,
            Title = text.Title,
            Slug = text.Slug,
            ShortDescription = text.ShortDescription,
            Description = text.Description,
            Nominations = SettlementCompetitionContent.Nominations,
            Rules = SettlementCompetitionContent.Rules,
            // Written from the form's own limits, so the page cannot promise what the validator would refuse.
            Requirements = SettlementCompetitionContent.Requirements(fileRules),
            JudgingCriteria = SettlementCompetitionContent.JudgingCriteria,
            UsageTerms = SettlementCompetitionContent.UsageTerms,
            Prize = SettlementCompetitionContent.Prize,
            Status = CompetitionStatus.Published,
            HasCategories = false,
            SortOrder = text.SortOrder,
        };
        db.Competitions.Add(competition);
        await db.SaveChangesAsync(ct);

        // The nomination is picked from a list, never typed, so the answers stay comparable. The settlement is no
        // longer asked at all: the competition the entry belongs to already is the answer.
        competition.Fields.Add(new CompetitionField
        {
            Key = "nomination", Label = "Nominasiya", FieldType = CompetitionFieldType.Select,
            IsRequired = true, IsPublicAfterResults = true, SortOrder = 1,
            OptionsJson = JsonSerializer.Serialize(new[]
            {
                "Simvol və Loqotip Dizaynı", "Promo Məhsul Tətbiqləri", "Məkan və Küçə Tərtibatı",
            }),
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "title", Label = "İşin adı", FieldType = CompetitionFieldType.ShortText,
            IsRequired = true, IsSubmissionTitle = true, IsPublicAfterResults = true, SortOrder = 2,
            OptionsJson = """{"maxLength":150}""",
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "concept", Label = "Konseptin izahı", FieldType = CompetitionFieldType.LongText,
            IsRequired = true, IsPublicAfterResults = true, SortOrder = 3,
            HelpText = SettlementCompetitionContent.ConceptHelp,
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "symbols", Label = "İstifadə olunan simvollar və onların mənası",
            FieldType = CompetitionFieldType.LongText, IsRequired = true, SortOrder = 4,
            HelpText = SettlementCompetitionContent.SymbolsHelp(text.Settlement),
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "files", Label = "Fayllar", FieldType = CompetitionFieldType.File,
            IsRequired = true, IsPublicAfterResults = true, SortOrder = 5, OptionsJson = fileRules,
            HelpText = SettlementCompetitionContent.FileRules(fileRules),
        });
        competition.Fields.Add(new CompetitionField
        {
            Key = "notes", Label = "Əlavə qeyd", FieldType = CompetitionFieldType.LongText,
            IsRequired = false, SortOrder = 6,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Everything the first version of the launch did not say yet: the participation rules, what the entries are
    /// judged on, what happens to an author's work, the full participation section, and the help text under the
    /// form's harder questions. Also corrects the prize figures, which were wrong when they were first written.
    ///
    /// Every edit here is conditional on the field still holding what the seeder itself put there (or nothing at
    /// all). An admin's own words are never overwritten — if the text has been touched, this step leaves it.
    /// </summary>
    private async Task SeedLaunchReadinessAsync(CancellationToken ct)
    {
        string[] slugs = [NovxaniCompetitionSlug, EmircanCompetitionSlug];
        var competitions = await db.Competitions.Include(c => c.Fields)
            .Where(c => slugs.Contains(c.Slug)).ToListAsync(ct);

        foreach (var competition in competitions)
        {
            var settlement = competition.Slug == NovxaniCompetitionSlug ? "Novxanı" : "Əmircan";
            var files = competition.Fields.FirstOrDefault(f => f.Key == "files");

            if (string.IsNullOrWhiteSpace(competition.Rules))
                competition.Rules = SettlementCompetitionContent.Rules;
            if (string.IsNullOrWhiteSpace(competition.JudgingCriteria))
                competition.JudgingCriteria = SettlementCompetitionContent.JudgingCriteria;
            if (string.IsNullOrWhiteSpace(competition.UsageTerms))
                competition.UsageTerms = SettlementCompetitionContent.UsageTerms;

            // The one-line eligibility note was all there was; anything longer is an admin's and stays.
            if (string.IsNullOrWhiteSpace(competition.Requirements)
                || competition.Requirements.Trim() == SettlementCompetitionContent.OriginalRequirements)
                competition.Requirements = SettlementCompetitionContent.Requirements(files?.OptionsJson);

            // 500/300/200 were the figures of the first draft; 250/150/100 are the real ones. Only the draft's
            // own wording is replaced — an admin who has already corrected or rewritten it keeps their text.
            if (competition.Prize is not null && competition.Prize.Contains("500 AZN", StringComparison.Ordinal))
                competition.Prize = SettlementCompetitionContent.Prize;

            if (files is not null && string.IsNullOrWhiteSpace(files.HelpText))
                files.HelpText = SettlementCompetitionContent.FileRules(files.OptionsJson);
            var symbols = competition.Fields.FirstOrDefault(f => f.Key == "symbols");
            if (symbols is not null && string.IsNullOrWhiteSpace(symbols.HelpText))
                symbols.HelpText = SettlementCompetitionContent.SymbolsHelp(settlement);
            var concept = competition.Fields.FirstOrDefault(f => f.Key == "concept");
            if (concept is not null && string.IsNullOrWhiteSpace(concept.HelpText))
                concept.HelpText = SettlementCompetitionContent.ConceptHelp;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Two corrections to words already published, both made the same careful way: only text that is still,
    /// character for character, what an earlier seed wrote is touched. Anything an administrator has since edited
    /// is theirs, and comes back exactly as they wrote it.
    ///
    /// The first is punctuation. Short public lines — the home page's supporting line, the line beside the sign-in
    /// form, the line under a poster — read as labels rather than sentences, and the launch ended them with a full
    /// stop. That stop is removed. Long prose keeps its punctuation: rules, descriptions and help text are not
    /// touched here, and nothing in this step strips a full stop from a paragraph.
    ///
    /// The second is the note that work made with AI may be entered. It belongs in the participation section,
    /// which is where someone looks for it.
    /// </summary>
    private async Task SeedShortCopyAndAiNoteAsync(CancellationToken ct)
    {
        var settings = await db.PlatformSettings.FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            settings.HomeHeroTitle = WithoutSeededFullStop(settings.HomeHeroTitle, PlatformDefaults.HomeHeroTitle);
            settings.HomeHeroSubtitle =
                WithoutSeededFullStop(settings.HomeHeroSubtitle, PlatformDefaults.HomeHeroSubtitle);
            settings.AuthPromoTitle = WithoutSeededFullStop(settings.AuthPromoTitle, PlatformDefaults.AuthPromoTitle);
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == AbseronProjectSlug, ct);
        if (project is not null)
            project.ShortDescription =
                WithoutSeededFullStop(project.ShortDescription, AbseronProjectShortDescription);

        string[] slugs = [NovxaniCompetitionSlug, EmircanCompetitionSlug];
        var competitions = await db.Competitions.Include(c => c.Fields)
            .Where(c => slugs.Contains(c.Slug)).ToListAsync(ct);

        foreach (var competition in competitions)
        {
            competition.ShortDescription = WithoutSeededFullStop(
                competition.ShortDescription,
                competition.Slug == NovxaniCompetitionSlug
                    ? SettlementCompetitionContent.NovxaniShortDescription
                    : SettlementCompetitionContent.EmircanShortDescription);

            // What Requirements produced before the AI note was written into it, recovered by taking the note back
            // out again — so there is never a second copy of that paragraph here to drift out of step with the first.
            var files = competition.Fields.FirstOrDefault(f => f.Key == "files");
            var withNote = SettlementCompetitionContent.Requirements(files?.OptionsJson);
            var withoutNote = withNote.Replace(SettlementCompetitionContent.AiNote, "", StringComparison.Ordinal);
            var current = competition.Requirements?.Trim() ?? "";
            if (current.Length == 0
                || current == withoutNote.Trim()
                || current == SettlementCompetitionContent.OriginalRequirements)
                competition.Requirements = withNote;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gives the settlement competitions' nominations an identity, so a vote can be counted against one.
    ///
    /// <para>
    /// The nomination a participant picked has always been the text of a required Select answer, which is fine for
    /// reading and useless for counting: text has no identity, and an admin rewording an option would silently
    /// change what every vote had been cast in. CompetitionCategory is the subdivision this platform already has —
    /// "a village, a medium, a nomination" — so each nomination becomes one, and the vote's unique index has
    /// something stable to hold.
    /// </para>
    /// <para>
    /// HasCategories is deliberately left alone. Turning it on would put a category picker in front of the entry
    /// form, where the participant already chooses the nomination inside the form; the link is made here instead,
    /// from the answer they already gave.
    /// </para>
    /// <para>
    /// Nothing is guessed. The answer is compared to the option text exactly; a submission whose answer matches
    /// none is left unlinked and logged, and its answer text is never touched either way.
    /// </para>
    /// </summary>
    private async Task SeedNominationsAsCategoriesAsync(CancellationToken ct)
    {
        string[] slugs = [NovxaniCompetitionSlug, EmircanCompetitionSlug];
        var competitions = await db.Competitions.Where(c => slugs.Contains(c.Slug)).ToListAsync(ct);

        foreach (var competition in competitions)
        {
            var existing = await db.CompetitionCategories
                .Where(x => x.CompetitionId == competition.Id).ToListAsync(ct);

            for (var i = 0; i < SettlementCompetitionContent.NominationSlugs.Length; i++)
            {
                var (option, slug) = SettlementCompetitionContent.NominationSlugs[i];
                if (existing.Any(x => x.Slug == slug)) continue;
                db.CompetitionCategories.Add(new CompetitionCategory
                {
                    CompetitionId = competition.Id,
                    Name = option,
                    Slug = slug,
                    SortOrder = i,
                    IsActive = true,
                });
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var competition in competitions)
        {
            var categories = await db.CompetitionCategories
                .Where(x => x.CompetitionId == competition.Id)
                .ToDictionaryAsync(x => x.Slug, x => x.Id, ct);
            var field = await db.CompetitionFields
                .FirstOrDefaultAsync(f => f.CompetitionId == competition.Id && f.Key == "nomination", ct);
            if (field is null) continue;

            // Only entries that have not been placed in a nomination already; a category chosen at entry wins.
            var entries = await db.Submissions
                .Where(x => x.CompetitionId == competition.Id && x.CompetitionCategoryId == null)
                .Select(x => new
                {
                    x.Id,
                    Answer = db.SubmissionAnswers
                        .Where(a => a.SubmissionId == x.Id && a.CompetitionFieldId == field.Id)
                        .Select(a => a.Value).FirstOrDefault(),
                })
                .ToListAsync(ct);

            var linked = 0;
            var unmatched = 0;
            foreach (var entry in entries)
            {
                var answer = entry.Answer?.Trim();
                var match = SettlementCompetitionContent.NominationSlugs
                    .FirstOrDefault(n => string.Equals(n.Option, answer, StringComparison.Ordinal));
                if (match.Slug is null || !categories.TryGetValue(match.Slug, out var categoryId))
                {
                    // A draft that has not answered yet is the ordinary case; anything else is worth a line in the log.
                    if (!string.IsNullOrWhiteSpace(answer)) unmatched++;
                    continue;
                }
                await db.Submissions.Where(x => x.Id == entry.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.CompetitionCategoryId, categoryId), ct);
                linked++;
            }

            logger.LogInformation(
                "Nominations for {Slug}: {Linked} entries linked, {Unmatched} left unlinked (answer matched no option).",
                competition.Slug, linked, unmatched);
        }
    }

    /// <summary>
    /// <paramref name="seeded"/> without its full stop — but only when <paramref name="current"/> is still exactly
    /// that seeded line with one on the end. Anything else is an administrator's own wording and is returned
    /// unchanged, full stop included.
    /// </summary>
    private static string WithoutSeededFullStop(string? current, string seeded) =>
        current?.Trim() == seeded + "." ? seeded : current ?? "";
}
