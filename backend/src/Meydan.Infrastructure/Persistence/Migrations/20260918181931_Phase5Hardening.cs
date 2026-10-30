using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviewFileId",
                table: "Submissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicAssetCreatedAt",
                table: "SubmissionFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicAssetError",
                table: "SubmissionFiles",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicAssetHeight",
                table: "SubmissionFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicAssetKey",
                table: "SubmissionFiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicAssetStatus",
                table: "SubmissionFiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending"); // existing files get a derivative from the startup backfill

            migrationBuilder.AddColumn<int>(
                name: "PublicAssetWidth",
                table: "SubmissionFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanStatus",
                table: "SubmissionFiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotScanned"); // honest: nothing scanned them

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_PreviewFileId",
                table: "Submissions",
                column: "PreviewFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_SubmissionFiles_PreviewFileId",
                table: "Submissions",
                column: "PreviewFileId",
                principalTable: "SubmissionFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_SubmissionFiles_PreviewFileId",
                table: "Submissions");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_PreviewFileId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "PreviewFileId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "PublicAssetCreatedAt",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PublicAssetError",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PublicAssetHeight",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PublicAssetKey",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PublicAssetStatus",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PublicAssetWidth",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "ScanStatus",
                table: "SubmissionFiles");
        }
    }
}
