using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase13PlacesAndHiddenFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HiddenFromPublic",
                table: "SubmissionFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SecondPlaceSelectedAt",
                table: "CompetitionResults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SecondPlaceSelectedById",
                table: "CompetitionResults",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecondPlaceSubmissionId",
                table: "CompetitionResults",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ThirdPlaceSelectedAt",
                table: "CompetitionResults",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ThirdPlaceSelectedById",
                table: "CompetitionResults",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThirdPlaceSubmissionId",
                table: "CompetitionResults",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_SecondPlaceSelectedById",
                table: "CompetitionResults",
                column: "SecondPlaceSelectedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_SecondPlaceSubmissionId_CompetitionId",
                table: "CompetitionResults",
                columns: new[] { "SecondPlaceSubmissionId", "CompetitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_ThirdPlaceSelectedById",
                table: "CompetitionResults",
                column: "ThirdPlaceSelectedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionResults_ThirdPlaceSubmissionId_CompetitionId",
                table: "CompetitionResults",
                columns: new[] { "ThirdPlaceSubmissionId", "CompetitionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CompetitionResults_DistinctPlaces",
                table: "CompetitionResults",
                sql: "(\"SecondPlaceSubmissionId\" IS NULL OR \"SecondPlaceSubmissionId\" IS DISTINCT FROM \"OverallWinnerSubmissionId\") AND (\"ThirdPlaceSubmissionId\" IS NULL OR (\"ThirdPlaceSubmissionId\" IS DISTINCT FROM \"OverallWinnerSubmissionId\" AND \"ThirdPlaceSubmissionId\" IS DISTINCT FROM \"SecondPlaceSubmissionId\"))");

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_SecondPlaceSelectedById",
                table: "CompetitionResults",
                column: "SecondPlaceSelectedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_ThirdPlaceSelectedById",
                table: "CompetitionResults",
                column: "ThirdPlaceSelectedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_Submissions_SecondPlaceSubmissionId_Comp~",
                table: "CompetitionResults",
                columns: new[] { "SecondPlaceSubmissionId", "CompetitionId" },
                principalTable: "Submissions",
                principalColumns: new[] { "Id", "CompetitionId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CompetitionResults_Submissions_ThirdPlaceSubmissionId_Compe~",
                table: "CompetitionResults",
                columns: new[] { "ThirdPlaceSubmissionId", "CompetitionId" },
                principalTable: "Submissions",
                principalColumns: new[] { "Id", "CompetitionId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_SecondPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_AspNetUsers_ThirdPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_Submissions_SecondPlaceSubmissionId_Comp~",
                table: "CompetitionResults");

            migrationBuilder.DropForeignKey(
                name: "FK_CompetitionResults_Submissions_ThirdPlaceSubmissionId_Compe~",
                table: "CompetitionResults");

            migrationBuilder.DropIndex(
                name: "IX_CompetitionResults_SecondPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropIndex(
                name: "IX_CompetitionResults_SecondPlaceSubmissionId_CompetitionId",
                table: "CompetitionResults");

            migrationBuilder.DropIndex(
                name: "IX_CompetitionResults_ThirdPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropIndex(
                name: "IX_CompetitionResults_ThirdPlaceSubmissionId_CompetitionId",
                table: "CompetitionResults");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CompetitionResults_DistinctPlaces",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "HiddenFromPublic",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "SecondPlaceSelectedAt",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "SecondPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "SecondPlaceSubmissionId",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "ThirdPlaceSelectedAt",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "ThirdPlaceSelectedById",
                table: "CompetitionResults");

            migrationBuilder.DropColumn(
                name: "ThirdPlaceSubmissionId",
                table: "CompetitionResults");
        }
    }
}
