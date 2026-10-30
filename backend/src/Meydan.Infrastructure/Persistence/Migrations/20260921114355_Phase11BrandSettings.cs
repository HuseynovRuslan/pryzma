using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11BrandSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PlatformName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HomeHeroTitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    HomeHeroSubtitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AuthPromoTitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuthPromoImage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformSettings");
        }
    }
}
