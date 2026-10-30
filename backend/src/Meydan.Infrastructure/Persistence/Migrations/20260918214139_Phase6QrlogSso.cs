using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6QrlogSso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QrlogLoginTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimSecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LinkUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReturnTo = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Subject = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FirstName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    LastName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QrlogLoginTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QrlogLoginTickets_AspNetUsers_LinkUserId",
                        column: x => x.LinkUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QrlogLoginTickets_Code",
                table: "QrlogLoginTickets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QrlogLoginTickets_ExpiresAt",
                table: "QrlogLoginTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_QrlogLoginTickets_LinkUserId",
                table: "QrlogLoginTickets",
                column: "LinkUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QrlogLoginTickets");
        }
    }
}
