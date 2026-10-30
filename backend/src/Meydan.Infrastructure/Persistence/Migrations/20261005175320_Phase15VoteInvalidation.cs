using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// D12: a vote an admin finds fraudulent is set aside, never deleted. Three nullable columns (when, by whom,
    /// why), a key to the admin's account that empties if the account goes, and a check that a vote set aside always
    /// carries its reason — written so that a missing reason makes it false rather than unknown, because a CHECK
    /// that evaluates to NULL passes. Purely additive: every existing vote stays valid, and a release that does not
    /// know the columns keeps casting and counting votes as before.
    /// </summary>
    public partial class Phase15VoteInvalidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvalidatedAt",
                table: "Votes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvalidatedById",
                table: "Votes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvalidationReason",
                table: "Votes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Votes_InvalidatedById",
                table: "Votes",
                column: "InvalidatedById");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Votes_InvalidationHasReason",
                table: "Votes",
                sql: "(\"InvalidatedAt\" IS NULL AND \"InvalidationReason\" IS NULL) OR (\"InvalidatedAt\" IS NOT NULL AND \"InvalidationReason\" IS NOT NULL AND length(btrim(\"InvalidationReason\")) > 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_Votes_AspNetUsers_InvalidatedById",
                table: "Votes",
                column: "InvalidatedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Votes_AspNetUsers_InvalidatedById",
                table: "Votes");

            migrationBuilder.DropIndex(
                name: "IX_Votes_InvalidatedById",
                table: "Votes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Votes_InvalidationHasReason",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "InvalidatedAt",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "InvalidatedById",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "InvalidationReason",
                table: "Votes");
        }
    }
}
