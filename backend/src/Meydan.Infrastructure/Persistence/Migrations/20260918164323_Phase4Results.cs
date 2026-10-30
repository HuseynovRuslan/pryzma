using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Results : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPublicGalleryEnabled",
                table: "Competitions");

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicAfterResults",
                table: "CompetitionFields",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Conservative defaults for existing forms: only the work's title becomes public after results;
            // every other answer and every file stays private until an admin marks its field public.
            migrationBuilder.Sql("""UPDATE "CompetitionFields" SET "IsPublicAfterResults" = TRUE WHERE "IsSubmissionTitle";""");

            // IsPublicGalleryEnabled (dropped above) was never set anywhere — the gallery now opens with published results.

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Submissions_Id_CompetitionId",
                table: "Submissions",
                columns: new[] { "Id", "CompetitionId" });

            migrationBuilder.CreateTable(
                name: "CompetitionResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    WinningSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    SelectedById = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Announcement = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompetitionResults_AspNetUsers_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionResults_AspNetUsers_SelectedById",
                        column: x => x.SelectedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionResults_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompetitionResults_Submissions_WinningSubmissionId_Competit~",
                        columns: x => new { x.WinningSubmissionId, x.CompetitionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_CompetitionId",
                table: "CompetitionResults",
                column: "CompetitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_PublishedById",
                table: "CompetitionResults",
                column: "PublishedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_SelectedById",
                table: "CompetitionResults",
                column: "SelectedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_WinningSubmissionId_CompetitionId",
                table: "CompetitionResults",
                columns: new[] { "WinningSubmissionId", "CompetitionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompetitionResults");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Submissions_Id_CompetitionId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "IsPublicAfterResults",
                table: "CompetitionFields");

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicGalleryEnabled",
                table: "Competitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
