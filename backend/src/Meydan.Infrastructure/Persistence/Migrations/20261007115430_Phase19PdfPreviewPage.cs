using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The page an admin chooses to stand for a PDF (owner, 7 October 2026). Additive — one nullable column on
    /// SubmissionFiles (null: the automatic choice, as before) and a check that a chosen page is 1 or more. No existing
    /// row changes; no file is touched.
    /// </summary>
    public partial class Phase19PdfPreviewPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviewPage",
                table: "SubmissionFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SubmissionFiles_PreviewPage",
                table: "SubmissionFiles",
                sql: "\"PreviewPage\" IS NULL OR \"PreviewPage\" >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SubmissionFiles_PreviewPage",
                table: "SubmissionFiles");

            migrationBuilder.DropColumn(
                name: "PreviewPage",
                table: "SubmissionFiles");
        }
    }
}
