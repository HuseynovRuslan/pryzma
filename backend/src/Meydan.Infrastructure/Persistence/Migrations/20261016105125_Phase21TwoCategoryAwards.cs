using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase21TwoCategoryAwards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompetitionAwards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Place = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    ParticipantName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RecordedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitionAwards", x => x.Id);
                    table.CheckConstraint("CK_CompetitionAwards_Place", "\"Place\" BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_CompetitionAwards_Points", "\"Points\" >= 0");
                    table.ForeignKey(
                        name: "FK_CompetitionAwards_AspNetUsers_RecordedById",
                        column: x => x.RecordedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionAwards_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompetitionAwards_Submissions_SubmissionId_CompetitionId",
                        columns: x => new { x.SubmissionId, x.CompetitionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionAwards_CompetitionId_Category_Place",
                table: "CompetitionAwards",
                columns: new[] { "CompetitionId", "Category", "Place" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionAwards_CompetitionId_Category_SubmissionId",
                table: "CompetitionAwards",
                columns: new[] { "CompetitionId", "Category", "SubmissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionAwards_RecordedById",
                table: "CompetitionAwards",
                column: "RecordedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionAwards_SubmissionId_CompetitionId",
                table: "CompetitionAwards",
                columns: new[] { "SubmissionId", "CompetitionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompetitionAwards");
        }
    }
}
