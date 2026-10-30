using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Phase 7: winners per category (settlement) instead of one per competition, a results banner, and card-size
    /// thumbnails of the public derivatives.
    /// <para>
    /// Hand-written so that no data is lost (the scaffold dropped the old winner columns): the existing winner of every
    /// result is COPIED into CompetitionCategoryWinners under its submission's category — who chose it and when are
    /// kept — before the old columns are renamed into the optional "overall winner". Only for competitions without
    /// categories does the winner stay there, as their one winner. Publication data (PublishedAt, PublishedById,
    /// Announcement) is untouched.
    /// </para>
    /// </summary>
    public partial class Phase7CategoryWinners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- New winners table -------------------------------------------------------------------------
            migrationBuilder.AddUniqueConstraint(
                name: "AK_CompetitionCategories_Id_CompetitionId",
                table: "CompetitionCategories",
                columns: new[] { "Id", "CompetitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_Id_CompetitionCategoryId",
                table: "Submissions",
                columns: new[] { "Id", "CompetitionCategoryId" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "CompetitionCategoryWinners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    CompetitionCategoryId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    SelectedById = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitionCategoryWinners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompetitionCategoryWinners_AspNetUsers_SelectedById",
                        column: x => x.SelectedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionCategoryWinners_CompetitionCategories_Competitio~",
                        columns: x => new { x.CompetitionCategoryId, x.CompetitionId },
                        principalTable: "CompetitionCategories",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompetitionCategoryWinners_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompetitionCategoryWinners_Submissions_SubmissionId_Competi~",
                        columns: x => new { x.SubmissionId, x.CompetitionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            // A winner wins its OWN category: (SubmissionId, CompetitionCategoryId) must be a real (submission, category)
            // pair. Not in the EF model (the submission's category column is nullable), hence plain SQL.
            migrationBuilder.Sql("""
                ALTER TABLE "CompetitionCategoryWinners"
                    ADD CONSTRAINT "FK_CompetitionCategoryWinners_Submissions_OwnCategory"
                    FOREIGN KEY ("SubmissionId", "CompetitionCategoryId")
                    REFERENCES "Submissions" ("Id", "CompetitionCategoryId")
                    ON UPDATE RESTRICT ON DELETE RESTRICT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionCategoryWinners_CompetitionCategoryId_Competitio~",
                table: "CompetitionCategoryWinners",
                columns: new[] { "CompetitionCategoryId", "CompetitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionCategoryWinners_CompetitionId",
                table: "CompetitionCategoryWinners",
                column: "CompetitionId");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionCategoryWinners_SelectedById",
                table: "CompetitionCategoryWinners",
                column: "SelectedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionCategoryWinners_SubmissionId_CompetitionId",
                table: "CompetitionCategoryWinners",
                columns: new[] { "SubmissionId", "CompetitionId" },
                unique: true);

            // ---- Existing winners move to their category (nothing is dropped) -----------------------------
            migrationBuilder.Sql("""
                INSERT INTO "CompetitionCategoryWinners" ("CompetitionId", "CompetitionCategoryId", "SubmissionId", "SelectedById", "SelectedAt")
                SELECT r."CompetitionId", s."CompetitionCategoryId", r."WinningSubmissionId", r."SelectedById", r."SelectedAt"
                FROM "CompetitionResults" r
                JOIN "Submissions" s ON s."Id" = r."WinningSubmissionId" AND s."CompetitionId" = r."CompetitionId"
                WHERE s."CompetitionCategoryId" IS NOT NULL;
                """);

            // ---- The old single winner becomes the optional overall winner --------------------------------
            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_Submissions_WinningSubmissionId_Competit~",
                table: "CompetitionResults");

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_SelectedById",
                table: "CompetitionResults");

            migrationBuilder.RenameColumn(
                name: "WinningSubmissionId",
                table: "CompetitionResults",
                newName: "OverallWinnerSubmissionId");

            migrationBuilder.RenameColumn(
                name: "SelectedById",
                table: "CompetitionResults",
                newName: "OverallWinnerSelectedById");

            migrationBuilder.RenameColumn(
                name: "SelectedAt",
                table: "CompetitionResults",
                newName: "OverallWinnerSelectedAt");

            migrationBuilder.RenameIndex(
                name: "IX_CompetitionResults_WinningSubmissionId_CompetitionId",
                table: "CompetitionResults",
                newName: "IX_CompetitionResults_OverallWinnerSubmissionId_CompetitionId");

            migrationBuilder.RenameIndex(
                name: "IX_CompetitionResults_SelectedById",
                table: "CompetitionResults",
                newName: "IX_CompetitionResults_OverallWinnerSelectedById");

            migrationBuilder.AlterColumn<int>(
                name: "OverallWinnerSubmissionId",
                table: "CompetitionResults",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "OverallWinnerSelectedAt",
                table: "CompetitionResults",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // Winners copied to their category are not ALSO overall winners.
            migrationBuilder.Sql("""
                UPDATE "CompetitionResults" r
                SET "OverallWinnerSubmissionId" = NULL, "OverallWinnerSelectedById" = NULL, "OverallWinnerSelectedAt" = NULL
                WHERE EXISTS (SELECT 1 FROM "CompetitionCategoryWinners" w WHERE w."SubmissionId" = r."OverallWinnerSubmissionId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_OverallWinnerSelectedById",
                table: "CompetitionResults",
                column: "OverallWinnerSelectedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_Submissions_OverallWinnerSubmissionId_Co~",
                table: "CompetitionResults",
                columns: new[] { "OverallWinnerSubmissionId", "CompetitionId" },
                principalTable: "Submissions",
                principalColumns: new[] { "Id", "CompetitionId" },
                onDelete: ReferentialAction.Restrict);

            // ---- Results banner --------------------------------------------------------------------------
            migrationBuilder.AddColumn<string>(
                name: "CoverImage",
                table: "CompetitionResults",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            // ---- Card-size thumbnails (made by the worker; existing derivatives are backfilled) ------------
            migrationBuilder.AddColumn<string>(
                name: "ThumbnailKey",
                table: "SubmissionFiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailStatus",
                table: "SubmissionFiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<int>(
                name: "ThumbnailWidth",
                table: "SubmissionFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThumbnailHeight",
                table: "SubmissionFiles",
                type: "integer",
                nullable: true);

            // A file whose derivative failed will not get a thumbnail either.
            migrationBuilder.Sql("""UPDATE "SubmissionFiles" SET "ThumbnailStatus" = 'Failed' WHERE "PublicAssetStatus" = 'Failed';""");
        }

        /// <summary>
        /// Development only (production rolls back by restoring the pre-deploy dump): the earliest category winner of
        /// each competition goes back into the single-winner column; results without any winner cannot exist in the
        /// old schema and are removed; banners and thumbnails are dropped.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "CompetitionResults" r
                SET "OverallWinnerSubmissionId" = w."SubmissionId", "OverallWinnerSelectedById" = w."SelectedById", "OverallWinnerSelectedAt" = w."SelectedAt"
                FROM (SELECT DISTINCT ON ("CompetitionId") * FROM "CompetitionCategoryWinners" ORDER BY "CompetitionId", "SelectedAt") w
                WHERE w."CompetitionId" = r."CompetitionId" AND r."OverallWinnerSubmissionId" IS NULL;
                INSERT INTO "CompetitionResults" ("CompetitionId", "OverallWinnerSubmissionId", "OverallWinnerSelectedById", "OverallWinnerSelectedAt")
                SELECT DISTINCT ON (w."CompetitionId") w."CompetitionId", w."SubmissionId", w."SelectedById", w."SelectedAt"
                FROM "CompetitionCategoryWinners" w
                WHERE NOT EXISTS (SELECT 1 FROM "CompetitionResults" r WHERE r."CompetitionId" = w."CompetitionId")
                ORDER BY w."CompetitionId", w."SelectedAt";
                DELETE FROM "CompetitionResults" WHERE "OverallWinnerSubmissionId" IS NULL;
                UPDATE "CompetitionResults" SET "OverallWinnerSelectedAt" = now() WHERE "OverallWinnerSelectedAt" IS NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_OverallWinnerSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_Submissions_OverallWinnerSubmissionId_Co~",
                table: "CompetitionResults");

            migrationBuilder.DropTable(
                name: "CompetitionCategoryWinners");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_Id_CompetitionCategoryId",
                table: "Submissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CompetitionCategories_Id_CompetitionId",
                table: "CompetitionCategories");

            migrationBuilder.DropColumn(name: "ThumbnailHeight", table: "SubmissionFiles");
            migrationBuilder.DropColumn(name: "ThumbnailKey", table: "SubmissionFiles");
            migrationBuilder.DropColumn(name: "ThumbnailStatus", table: "SubmissionFiles");
            migrationBuilder.DropColumn(name: "ThumbnailWidth", table: "SubmissionFiles");
            migrationBuilder.DropColumn(name: "CoverImage", table: "CompetitionResults");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "OverallWinnerSelectedAt",
                table: "CompetitionResults",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "OverallWinnerSubmissionId",
                table: "CompetitionResults",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.RenameColumn(name: "OverallWinnerSubmissionId", table: "CompetitionResults", newName: "WinningSubmissionId");
            migrationBuilder.RenameColumn(name: "OverallWinnerSelectedById", table: "CompetitionResults", newName: "SelectedById");
            migrationBuilder.RenameColumn(name: "OverallWinnerSelectedAt", table: "CompetitionResults", newName: "SelectedAt");

            migrationBuilder.RenameIndex(
                name: "IX_CompetitionResults_OverallWinnerSubmissionId_CompetitionId",
                table: "CompetitionResults",
                newName: "IX_CompetitionResults_WinningSubmissionId_CompetitionId");

            migrationBuilder.RenameIndex(
                name: "IX_CompetitionResults_OverallWinnerSelectedById",
                table: "CompetitionResults",
                newName: "IX_CompetitionResults_SelectedById");

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_SelectedById",
                table: "CompetitionResults",
                column: "SelectedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_Submissions_WinningSubmissionId_Competit~",
                table: "CompetitionResults",
                columns: new[] { "WinningSubmissionId", "CompetitionId" },
                principalTable: "Submissions",
                principalColumns: new[] { "Id", "CompetitionId" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
