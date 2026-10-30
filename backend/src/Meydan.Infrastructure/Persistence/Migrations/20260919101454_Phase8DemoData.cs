using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8DemoData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Submissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Competitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CoverIsDemo",
                table: "CompetitionResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "CompetitionResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "CoverIsDemo",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "AspNetUsers");
        }
    }
}
