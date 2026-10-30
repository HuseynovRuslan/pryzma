using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase20JuryAllWorks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JuryEvaluations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RosterFrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RosterFrozenById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuryEvaluations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JuryEvaluations_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryEvaluations_AspNetUsers_RosterFrozenById",
                        column: x => x.RosterFrozenById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryEvaluations_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JuryAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EvaluationId = table.Column<int>(type: "integer", nullable: false),
                    JurorId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InviteTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InviteCodeHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    InviteIssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InviteIssuedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InviteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InviteRevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedCodeAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdentityConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuryAssignments", x => x.Id);
                    table.UniqueConstraint("AK_JuryAssignments_EvaluationId_JurorId", x => new { x.EvaluationId, x.JurorId });
                    table.ForeignKey(
                        name: "FK_JuryAssignments_AspNetUsers_InviteIssuedById",
                        column: x => x.InviteIssuedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryAssignments_AspNetUsers_SubmittedById",
                        column: x => x.SubmittedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryAssignments_FinalJurors_JurorId",
                        column: x => x.JurorId,
                        principalTable: "FinalJurors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JuryAssignments_JuryEvaluations_EvaluationId",
                        column: x => x.EvaluationId,
                        principalTable: "JuryEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JuryRosterEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EvaluationId = table.Column<int>(type: "integer", nullable: false),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuryRosterEntries", x => x.Id);
                    table.UniqueConstraint("AK_JuryRosterEntries_EvaluationId_SubmissionId", x => new { x.EvaluationId, x.SubmissionId });
                    table.ForeignKey(
                        name: "FK_JuryRosterEntries_JuryEvaluations_EvaluationId",
                        column: x => x.EvaluationId,
                        principalTable: "JuryEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JuryRosterEntries_Submissions_SubmissionId_CompetitionId",
                        columns: x => new { x.SubmissionId, x.CompetitionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JurySessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssignmentId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JurySessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JurySessions_JuryAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "JuryAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JuryWorkScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EvaluationId = table.Column<int>(type: "integer", nullable: false),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    JurorId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuryWorkScores", x => x.Id);
                    table.CheckConstraint("CK_JuryWorkScores_Score", "\"Score\" BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_JuryWorkScores_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryWorkScores_JuryAssignments_EvaluationId_JurorId",
                        columns: x => new { x.EvaluationId, x.JurorId },
                        principalTable: "JuryAssignments",
                        principalColumns: new[] { "EvaluationId", "JurorId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JuryWorkScores_JuryRosterEntries_EvaluationId_SubmissionId",
                        columns: x => new { x.EvaluationId, x.SubmissionId },
                        principalTable: "JuryRosterEntries",
                        principalColumns: new[] { "EvaluationId", "SubmissionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JuryAssignments_InviteIssuedById",
                table: "JuryAssignments",
                column: "InviteIssuedById");

            migrationBuilder.CreateIndex(
                name: "IX_JuryAssignments_InviteTokenHash",
                table: "JuryAssignments",
                column: "InviteTokenHash",
                unique: true,
                filter: "\"InviteTokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JuryAssignments_JurorId",
                table: "JuryAssignments",
                column: "JurorId");

            migrationBuilder.CreateIndex(
                name: "IX_JuryAssignments_SubmittedById",
                table: "JuryAssignments",
                column: "SubmittedById");

            migrationBuilder.CreateIndex(
                name: "IX_JuryEvaluations_CompetitionId",
                table: "JuryEvaluations",
                column: "CompetitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JuryEvaluations_CreatedById",
                table: "JuryEvaluations",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_JuryEvaluations_RosterFrozenById",
                table: "JuryEvaluations",
                column: "RosterFrozenById");

            migrationBuilder.CreateIndex(
                name: "IX_JuryRosterEntries_EvaluationId_SortOrder",
                table: "JuryRosterEntries",
                columns: new[] { "EvaluationId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JuryRosterEntries_SubmissionId_CompetitionId",
                table: "JuryRosterEntries",
                columns: new[] { "SubmissionId", "CompetitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_JurySessions_AssignmentId",
                table: "JurySessions",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_JurySessions_TokenHash",
                table: "JurySessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JuryWorkScores_CompetitionId_JurorId_SubmissionId",
                table: "JuryWorkScores",
                columns: new[] { "CompetitionId", "JurorId", "SubmissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JuryWorkScores_EvaluationId_JurorId",
                table: "JuryWorkScores",
                columns: new[] { "EvaluationId", "JurorId" });

            migrationBuilder.CreateIndex(
                name: "IX_JuryWorkScores_EvaluationId_SubmissionId",
                table: "JuryWorkScores",
                columns: new[] { "EvaluationId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_JuryWorkScores_UpdatedById",
                table: "JuryWorkScores",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JurySessions");

            migrationBuilder.DropTable(
                name: "JuryWorkScores");

            migrationBuilder.DropTable(
                name: "JuryAssignments");

            migrationBuilder.DropTable(
                name: "JuryRosterEntries");

            migrationBuilder.DropTable(
                name: "JuryEvaluations");
        }
    }
}
