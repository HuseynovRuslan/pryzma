using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Email-first registration: a table of registrations that have only an address so far, and outbox mail that can
    /// go to one of them instead of an account. Additive; existing rows are untouched (all have an account).
    /// </summary>
    public partial class Phase17EmailFirstRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "EmailOutbox",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "PendingRegistrationId",
                table: "EmailOutbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProofHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProofExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SendCount = table.Column<int>(type: "integer", nullable: false),
                    SendWindowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRegistrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_PendingRegistrationId",
                table: "EmailOutbox",
                column: "PendingRegistrationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmailOutbox_HasRecipient",
                table: "EmailOutbox",
                sql: "\"UserId\" IS NOT NULL OR \"PendingRegistrationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_ExpiresAt",
                table: "PendingRegistrations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_NormalizedEmail",
                table: "PendingRegistrations",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_ProofHash",
                table: "PendingRegistrations",
                column: "ProofHash",
                unique: true,
                filter: "\"ProofHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_TokenHash",
                table: "PendingRegistrations",
                column: "TokenHash",
                unique: true,
                filter: "\"TokenHash\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailOutbox_PendingRegistrations_PendingRegistrationId",
                table: "EmailOutbox",
                column: "PendingRegistrationId",
                principalTable: "PendingRegistrations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mail for registrations that never became accounts has no one to go to once the column is required again.
            migrationBuilder.Sql("DELETE FROM \"EmailOutbox\" WHERE \"UserId\" IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_EmailOutbox_PendingRegistrations_PendingRegistrationId",
                table: "EmailOutbox");

            migrationBuilder.DropTable(
                name: "PendingRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_EmailOutbox_PendingRegistrationId",
                table: "EmailOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmailOutbox_HasRecipient",
                table: "EmailOutbox");

            migrationBuilder.DropColumn(
                name: "PendingRegistrationId",
                table: "EmailOutbox");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "EmailOutbox",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
