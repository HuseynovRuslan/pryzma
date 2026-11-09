using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase23ClientDeviceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CastDeviceId",
                table: "Votes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CastIp",
                table: "Votes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CastUserAgent",
                table: "Votes",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByDeviceId",
                table: "RefreshTokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserAgent",
                table: "RefreshTokens",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CastDeviceId",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "CastIp",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "CastUserAgent",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "CreatedByDeviceId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "CreatedByUserAgent",
                table: "RefreshTokens");
        }
    }
}
