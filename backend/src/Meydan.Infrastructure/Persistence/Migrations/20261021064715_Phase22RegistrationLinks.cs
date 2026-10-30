using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Every registration mail keeps its own link (PendingRegistrationLinks) instead of one link per registration that
    /// each new mail replaced. Links still valid at the time of the upgrade are carried over, so a mail sent just
    /// before the deploy opens as it did; the downgrade keeps the newest valid link of each registration.
    /// </summary>
    public partial class Phase22RegistrationLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingRegistrationLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingRegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRegistrationLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingRegistrationLinks_PendingRegistrations_PendingRegist~",
                        column: x => x.PendingRegistrationId,
                        principalTable: "PendingRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrationLinks_PendingRegistrationId",
                table: "PendingRegistrationLinks",
                column: "PendingRegistrationId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrationLinks_TokenHash",
                table: "PendingRegistrationLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "PendingRegistrationLinks" ("PendingRegistrationId", "TokenHash", "CreatedAt", "ExpiresAt")
                SELECT "Id", "TokenHash", now(), "TokenExpiresAt"
                FROM "PendingRegistrations"
                WHERE "TokenHash" IS NOT NULL AND "TokenExpiresAt" > now();
                """);

            migrationBuilder.DropIndex(
                name: "IX_PendingRegistrations_TokenHash",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "TokenExpiresAt",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "PendingRegistrations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TokenExpiresAt",
                table: "PendingRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "PendingRegistrations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "PendingRegistrations" p
                SET "TokenHash" = l."TokenHash", "TokenExpiresAt" = l."ExpiresAt"
                FROM (
                    SELECT DISTINCT ON ("PendingRegistrationId") "PendingRegistrationId", "TokenHash", "ExpiresAt"
                    FROM "PendingRegistrationLinks"
                    WHERE "ExpiresAt" > now()
                    ORDER BY "PendingRegistrationId", "ExpiresAt" DESC
                ) l
                WHERE l."PendingRegistrationId" = p."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_TokenHash",
                table: "PendingRegistrations",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");

            migrationBuilder.DropTable(
                name: "PendingRegistrationLinks");
        }
    }
}
