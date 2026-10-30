using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase9LaunchContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactNote",
                table: "Competitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExhibitionOn",
                table: "Competitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nominations",
                table: "Competitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResultsAnnouncedOn",
                table: "Competitions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactNote",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "ExhibitionOn",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "Nominations",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "ResultsAnnouncedOn",
                table: "Competitions");
        }
    }
}
