using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase10LaunchReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JudgingCriteria",
                table: "Competitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsageTerms",
                table: "Competitions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JudgingCriteria",
                table: "Competitions");

            migrationBuilder.DropColumn(
                name: "UsageTerms",
                table: "Competitions");
        }
    }
}
