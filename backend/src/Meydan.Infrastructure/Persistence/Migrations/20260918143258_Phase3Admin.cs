using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Admin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lifecycles are now what an admin decides (Draft / Published / Closed / Archived); whether a competition
            // accepts work right now is computed from its dates. Map the Phase 1–2 values onto the new ones.
            migrationBuilder.Sql("""UPDATE "Projects" SET "Status" = 'Published' WHERE "Status" IN ('Active', 'Completed');""");
            migrationBuilder.Sql("""UPDATE "Competitions" SET "Status" = 'Published' WHERE "Status" IN ('Open', 'Upcoming');""");
            migrationBuilder.Sql("""UPDATE "Competitions" SET "Status" = 'Closed' WHERE "Status" IN ('Judging', 'Completed');""");

            migrationBuilder.AddColumn<string>(
                name: "FieldLabel",
                table: "SubmissionAnswers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "CompetitionFields",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing form fields stay live

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionNotes_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionNotes_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionStatusChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionStatusChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionStatusChanges_AspNetUsers_ChangedById",
                        column: x => x.ChangedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SubmissionStatusChanges_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId_CreatedAt",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionNotes_AuthorId",
                table: "SubmissionNotes",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionNotes_SubmissionId_CreatedAt",
                table: "SubmissionNotes",
                columns: new[] { "SubmissionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionStatusChanges_ChangedById",
                table: "SubmissionStatusChanges",
                column: "ChangedById");

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionStatusChanges_SubmissionId_ChangedAt",
                table: "SubmissionStatusChanges",
                columns: new[] { "SubmissionId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Projects" SET "Status" = 'Active' WHERE "Status" = 'Published';""");
            migrationBuilder.Sql("""UPDATE "Competitions" SET "Status" = 'Open' WHERE "Status" = 'Published';""");
            migrationBuilder.Sql("""UPDATE "Competitions" SET "Status" = 'Completed' WHERE "Status" = 'Closed';""");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "SubmissionNotes");

            migrationBuilder.DropTable(
                name: "SubmissionStatusChanges");

            migrationBuilder.DropColumn(
                name: "FieldLabel",
                table: "SubmissionAnswers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "CompetitionFields");
        }
    }
}
