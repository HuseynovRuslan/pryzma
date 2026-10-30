using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase12Voting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VotingEndsAt",
                table: "Competitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VotingStartsAt",
                table: "Competitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Votes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    CompetitionCategoryId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Votes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Votes_CompetitionCategories_CompetitionCategoryId",
                        column: x => x.CompetitionCategoryId,
                        principalTable: "CompetitionCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Votes_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // A vote is cast in the nomination its work was actually entered in: (SubmissionId,
            // CompetitionCategoryId) must be a real (submission, category) pair. Not in the EF model (the
            // submission's category column is nullable), hence plain SQL — as for the category winner.
            migrationBuilder.Sql("""
                ALTER TABLE "Votes"
                    ADD CONSTRAINT "FK_Votes_Submissions_OwnCategory"
                    FOREIGN KEY ("SubmissionId", "CompetitionCategoryId")
                    REFERENCES "Submissions" ("Id", "CompetitionCategoryId")
                    ON UPDATE RESTRICT ON DELETE RESTRICT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Votes_CompetitionCategoryId",
                table: "Votes",
                column: "CompetitionCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Votes_SubmissionId",
                table: "Votes",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Votes_UserId_CompetitionCategoryId",
                table: "Votes",
                columns: new[] { "UserId", "CompetitionCategoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "Votes" DROP CONSTRAINT IF EXISTS "FK_Votes_Submissions_OwnCategory";""");

            migrationBuilder.DropTable(
                name: "Votes");

            migrationBuilder.DropColumn(
                name: "VotingEndsAt",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "VotingStartsAt",
                table: "Competitions");
        }
    }
}
