using System;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MeydanDbContext))]
[Migration("20261103130400_Phase23VoteAdjustments")]
public partial class Phase23VoteAdjustments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VoteAdjustments",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CompetitionId = table.Column<int>(type: "integer", nullable: false),
                SubmissionId = table.Column<int>(type: "integer", nullable: false),
                Delta = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                RequestedById = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VoteAdjustments", x => x.Id);
                table.CheckConstraint("CK_VoteAdjustments_Delta", "\"Delta\" <> 0 AND \"Delta\" BETWEEN -100 AND 100");
                table.CheckConstraint("CK_VoteAdjustments_Status", "\"Status\" IN ('Pending', 'Approved', 'Rejected')");
                table.CheckConstraint("CK_VoteAdjustments_Decision",
                    "(\"Status\" = 'Pending' AND \"DecidedById\" IS NULL AND \"DecidedAt\" IS NULL) OR " +
                    "(\"Status\" <> 'Pending' AND \"DecidedById\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL)");
                table.ForeignKey("FK_VoteAdjustments_Competitions_CompetitionId",
                    x => x.CompetitionId, "Competitions", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_VoteAdjustments_Submissions_SubmissionId",
                    x => x.SubmissionId, "Submissions", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_VoteAdjustments_AspNetUsers_RequestedById",
                    x => x.RequestedById, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_VoteAdjustments_AspNetUsers_DecidedById",
                    x => x.DecidedById, "AspNetUsers", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_VoteAdjustments_CompetitionId_Status",
            "VoteAdjustments", new[] { "CompetitionId", "Status" });
        migrationBuilder.CreateIndex("IX_VoteAdjustments_SubmissionId_Status",
            "VoteAdjustments", new[] { "SubmissionId", "Status" });
        migrationBuilder.CreateIndex("IX_VoteAdjustments_RequestedById", "VoteAdjustments", "RequestedById");
        migrationBuilder.CreateIndex("IX_VoteAdjustments_DecidedById", "VoteAdjustments", "DecidedById");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("VoteAdjustments");
}
