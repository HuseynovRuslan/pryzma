using Meydan.Api.Tests.Infrastructure;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Meydan.Api.Tests;

/// <summary>How the schema reaches real databases: upgrades keep data, production never migrates by itself.</summary>
public sealed class MigrationTests
{
    [Fact]
    public async Task Upgrading_a_phase_two_database_keeps_submissions_and_maps_old_statuses()
    {
        var name = await TestDatabase.CreateAsync(fresh: true);
        try
        {
            var options = new DbContextOptionsBuilder<MeydanDbContext>().UseNpgsql(TestDatabase.ConnectionStringFor(name)).Options;
            await using (var db = new MeydanDbContext(options, TimeProvider.System))
            {
                var migrator = db.GetService<IMigrator>();
                var initial = db.Database.GetMigrations().Single(m => m.EndsWith("_InitialPostgres"));
                await migrator.MigrateAsync(initial);

                // Data exactly as Phase 1–2 wrote it: old status names, a form field, a submitted work.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Projects" ("Id","Name","Slug","ShortDescription","Status","SortOrder","CreatedAt","UpdatedAt")
                    VALUES (1,'Bakı kəndləri','baki-kendleri','x','Active',1,now(),now()), (2,'Köhnə','kohne','x','Completed',2,now(),now());
                    INSERT INTO "Competitions" ("Id","ProjectId","Title","Slug","ShortDescription","Status","HasCategories","IsPublicGalleryEnabled","SortOrder","CreatedAt","UpdatedAt")
                    VALUES (1,1,'Şevron','sevron','x','Open',true,false,1,now(),now()),
                           (2,1,'Tezliklə','tezlikle','x','Upcoming',false,false,2,now(),now()),
                           (3,2,'Bitmiş','bitmis','x','Completed',false,false,1,now(),now());
                    INSERT INTO "CompetitionCategories" ("Id","CompetitionId","Name","Slug","SortOrder","IsActive") VALUES (1,1,'Maştağa','mastaga',1,true);
                    INSERT INTO "CompetitionFields" ("Id","CompetitionId","Key","Label","FieldType","IsRequired","IsSubmissionTitle","SortOrder")
                    VALUES (1,1,'title','İşin adı','ShortText',true,true,1);
                    INSERT INTO "AspNetUsers" ("Id","FirstName","LastName","TermsAcceptedAt","CreatedAt","UpdatedAt","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
                    VALUES ('11111111-1111-1111-1111-111111111111','Aysel','Məmmədova',now(),now(),now(),false,false,false,true,0);
                    INSERT INTO "Submissions" ("Id","UserId","CompetitionId","CompetitionCategoryId","Title","Status","ReferenceNumber","SubmittedAt","CreatedAt","UpdatedAt","ConcurrencyStamp")
                    VALUES (1,'11111111-1111-1111-1111-111111111111',1,1,'Maştağa şevronu','Submitted','SUB-2026-00001',now(),now(),now(),gen_random_uuid());
                    INSERT INTO "SubmissionAnswers" ("Id","SubmissionId","CompetitionFieldId","Value") VALUES (1,1,1,'Maştağa şevronu');
                    """);

                await migrator.MigrateAsync(); // → latest
            }

            await using (var db = new MeydanDbContext(options, TimeProvider.System))
            {
                var projects = await db.Database.SqlQuery<string>($"""SELECT "Status" AS "Value" FROM "Projects" ORDER BY "Id" """).ToListAsync();
                Assert.Equal(["Published", "Published"], projects);
                var competitions = await db.Database.SqlQuery<string>($"""SELECT "Status" AS "Value" FROM "Competitions" ORDER BY "Id" """).ToListAsync();
                Assert.Equal(["Published", "Published", "Closed"], competitions);

                var submission = await db.Submissions.Include(s => s.Answers).Include(s => s.Category).SingleAsync();
                Assert.Equal("SUB-2026-00001", submission.ReferenceNumber);
                Assert.Equal("Maştağa", submission.Category!.Name);
                Assert.Equal("Maştağa şevronu", submission.Answers.Single().Value);
                Assert.True((await db.CompetitionFields.SingleAsync()).IsActive); // existing forms stay live
                // Phase 4: the title field becomes public after results; nothing else does, and no result exists.
                Assert.True((await db.CompetitionFields.SingleAsync()).IsPublicAfterResults);
                Assert.Empty(await db.CompetitionResults.ToListAsync());
            }
        }
        finally
        {
            await TestDatabase.DropAsync(name);
        }
    }

    [Fact]
    public async Task Phase7_moves_the_single_winner_into_its_category_and_keeps_everything_else()
    {
        var name = await TestDatabase.CreateAsync(fresh: true);
        try
        {
            var options = new DbContextOptionsBuilder<MeydanDbContext>().UseNpgsql(TestDatabase.ConnectionStringFor(name)).Options;
            await using (var db = new MeydanDbContext(options, TimeProvider.System))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_Phase6QrlogSso")));

                // Production as it is today: one published result whose winner is Balaxanı's entry; plus a competition
                // without categories whose single winner must stay its (overall) winner.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "AspNetUsers" ("Id","FirstName","LastName","TermsAcceptedAt","CreatedAt","UpdatedAt","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
                    VALUES ('11111111-1111-1111-1111-111111111111','Leyla','Quliyeva',now(),now(),now(),false,false,false,true,0),
                           ('22222222-2222-2222-2222-222222222222','Admin','MEYDAN',now(),now(),now(),true,false,false,true,0);
                    INSERT INTO "Projects" ("Id","Name","Slug","ShortDescription","Status","SortOrder","CreatedAt","UpdatedAt")
                    VALUES (1,'Bakı kəndləri','baki-kendleri','x','Published',1,now(),now());
                    INSERT INTO "Competitions" ("Id","ProjectId","Title","Slug","ShortDescription","Status","HasCategories","SortOrder","CreatedAt","UpdatedAt")
                    VALUES (1,1,'Şevron','sevron','x','Closed',true,1,now(),now()), (2,1,'Loqo','loqo','x','Closed',false,2,now(),now());
                    INSERT INTO "CompetitionCategories" ("Id","CompetitionId","Name","Slug","SortOrder","IsActive")
                    VALUES (1,1,'Balaxanı','balaxani',1,true), (2,1,'Ramana','ramana',2,true);
                    INSERT INTO "Submissions" ("Id","UserId","CompetitionId","CompetitionCategoryId","Title","Status","ReferenceNumber","SubmittedAt","CreatedAt","UpdatedAt","ConcurrencyStamp")
                    VALUES (7,'11111111-1111-1111-1111-111111111111',1,2,'Ramana','UnderReview','SUB-2026-00005',now(),now(),now(),gen_random_uuid()),
                           (8,'11111111-1111-1111-1111-111111111111',1,1,'Balaxanı şevronu','Approved','SUB-2026-00006',now(),now(),now(),gen_random_uuid()),
                           (9,'11111111-1111-1111-1111-111111111111',2,NULL,'Loqo','Submitted','SUB-2026-00007',now(),now(),now(),gen_random_uuid());
                    INSERT INTO "SubmissionFiles" ("Id","SubmissionId","OriginalFileName","StoragePath","ContentType","SizeBytes","SortOrder","UploadedAt","ScanStatus","PublicAssetStatus","PublicAssetKey")
                    VALUES (10,8,'a.png','2026/09/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png','image/png',10,1,now(),'NotScanned','Ready','derived/2026/09/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.webp'),
                           (11,7,'b.pdf','2026/09/cccccccccccccccccccccccccccccccc.pdf','application/pdf',10,1,now(),'NotScanned','Failed',NULL);
                    INSERT INTO "CompetitionResults" ("CompetitionId","WinningSubmissionId","SelectedById","SelectedAt","PublishedAt","PublishedById","Announcement")
                    VALUES (1,8,'22222222-2222-2222-2222-222222222222','2026-09-19T07:51:11Z','2026-09-19T07:57:14Z','22222222-2222-2222-2222-222222222222','Təbrik edirik'),
                           (2,9,NULL,'2026-09-18T10:00:00Z',NULL,NULL,NULL);
                    """);

                await migrator.MigrateAsync(); // → latest
            }

            await using (var db = new MeydanDbContext(options, TimeProvider.System))
            {
                var winner = await db.CompetitionCategoryWinners.SingleAsync();
                Assert.Equal((1, 1, 8), (winner.CompetitionId, winner.CompetitionCategoryId, winner.SubmissionId));
                Assert.Equal(DateTimeOffset.Parse("2026-09-19T07:51:11Z"), winner.SelectedAt);
                Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), winner.SelectedById);

                var results = await db.CompetitionResults.OrderBy(r => r.CompetitionId).ToListAsync();
                // Publication data untouched; the moved winner is not also an overall winner.
                Assert.Equal(DateTimeOffset.Parse("2026-09-19T07:57:14Z"), results[0].PublishedAt);
                Assert.Equal("Təbrik edirik", results[0].Announcement);
                Assert.Null(results[0].OverallWinnerSubmissionId);
                Assert.Null(results[0].CoverImage);
                // Without categories the single winner stays the competition's winner.
                Assert.Equal(9, results[1].OverallWinnerSubmissionId);
                Assert.Equal(DateTimeOffset.Parse("2026-09-18T10:00:00Z"), results[1].OverallWinnerSelectedAt);

                // Nothing else changed: every submission and file is there; thumbnails will be made from the derivatives.
                Assert.Equal(3, await db.Submissions.CountAsync());
                var files = await db.SubmissionFiles.OrderBy(f => f.Id).ToListAsync();
                Assert.Equal([PublicAssetStatus.Pending, PublicAssetStatus.Failed], files.Select(f => f.ThumbnailStatus).ToList());
            }
        }
        finally
        {
            await TestDatabase.DropAsync(name);
        }
    }

    [Fact]
    public async Task Phase22_carries_registration_links_that_still_work_over_and_back()
    {
        var name = await TestDatabase.CreateAsync(fresh: true);
        try
        {
            var options = new DbContextOptionsBuilder<MeydanDbContext>().UseNpgsql(TestDatabase.ConnectionStringFor(name)).Options;
            await using var db = new MeydanDbContext(options, TimeProvider.System);
            var migrator = db.GetService<IMigrator>();
            var migrations = db.Database.GetMigrations().ToList();
            var phase21 = migrations.Single(m => m.EndsWith("_Phase21TwoCategoryAwards"));
            await migrator.MigrateAsync(phase21);

            // A mail sent ten minutes before the deploy, and one whose link has already run out.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "PendingRegistrations" ("Id","Email","NormalizedEmail","TokenHash","TokenExpiresAt","CreatedAt","ExpiresAt","SendCount","SendWindowStartedAt","LastSentAt")
                VALUES ('aaaaaaaa-0000-0000-0000-000000000001','a@example.com','A@EXAMPLE.COM','aaaa',now() + interval '35 minutes',now(),now() + interval '1 day',1,now(),now()),
                       ('aaaaaaaa-0000-0000-0000-000000000002','b@example.com','B@EXAMPLE.COM','bbbb',now() - interval '5 minutes',now(),now() + interval '1 day',1,now(),now());
                """);

            await migrator.MigrateAsync(migrations.Single(m => m.EndsWith("_Phase22RegistrationLinks")));
            var link = Assert.Single(await db.PendingRegistrationLinks.AsNoTracking().ToListAsync());
            Assert.Equal(("aaaa", Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")), (link.TokenHash, link.PendingRegistrationId));
            Assert.Equal(2, await db.PendingRegistrations.CountAsync());   // both registrations stay; only the dead link is gone

            await migrator.MigrateAsync(phase21);
            var restored = await db.Database
                .SqlQuery<string>($"""SELECT coalesce("TokenHash", '-') AS "Value" FROM "PendingRegistrations" ORDER BY "Email" """).ToListAsync();
            Assert.Equal(["aaaa", "-"], restored);
        }
        finally
        {
            await TestDatabase.DropAsync(name);
        }
    }

    [Fact]
    public void Outside_development_the_api_refuses_to_start_on_a_database_with_pending_migrations()
    {
        using var factory = new MeydanFactory { FreshDatabase = true, MigrateOnStartup = false, EnvironmentName = "Production", ProductionLike = true };
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("pending migration", ex.ToString());
        Assert.Contains("migrate", ex.ToString());
    }
}
