using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Meydan.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// The jury final (D13): the frozen finalists, the jurors, their marks and the jury's tie decisions. Additive — five
    /// new tables; no existing table, row or column is changed, and nothing points into Votes.
    /// </summary>
    public partial class Phase18JuryFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompetitionFinals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FrozenById = table.Column<Guid>(type: "uuid", nullable: true),
                    FrozenValidVotes = table.Column<int>(type: "integer", nullable: false),
                    FrozenInvalidatedVotes = table.Column<int>(type: "integer", nullable: false),
                    FrozenVotesFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstPrizeAzn = table.Column<int>(type: "integer", nullable: false),
                    SecondPrizeAzn = table.Column<int>(type: "integer", nullable: false),
                    ThirdPrizeAzn = table.Column<int>(type: "integer", nullable: false),
                    JuryLockedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JuryLockedById = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitionFinals", x => x.Id);
                    table.CheckConstraint("CK_CompetitionFinals_Prizes", "\"FirstPrizeAzn\" >= 0 AND \"SecondPrizeAzn\" >= 0 AND \"ThirdPrizeAzn\" >= 0");
                    table.ForeignKey(
                        name: "FK_CompetitionFinals_AspNetUsers_FrozenById",
                        column: x => x.FrozenById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionFinals_AspNetUsers_JuryLockedById",
                        column: x => x.JuryLockedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionFinals_AspNetUsers_PublishedById",
                        column: x => x.PublishedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CompetitionFinals_Competitions_CompetitionId",
                        column: x => x.CompetitionId,
                        principalTable: "Competitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompetitionFinalists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FinalId = table.Column<int>(type: "integer", nullable: false),
                    CompetitionId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionId = table.Column<int>(type: "integer", nullable: false),
                    ValidVotes = table.Column<int>(type: "integer", nullable: false),
                    PublicRank = table.Column<int>(type: "integer", nullable: false),
                    PublicPoints = table.Column<int>(type: "integer", nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JuryTotal = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    JuryRank = table.Column<int>(type: "integer", nullable: true),
                    JuryPoints = table.Column<int>(type: "integer", nullable: true),
                    FinalScore = table.Column<int>(type: "integer", nullable: true),
                    FinalPlace = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompetitionFinalists", x => x.Id);
                    table.CheckConstraint("CK_CompetitionFinalists_Snapshot", "\"ValidVotes\" >= 0 AND \"PublicRank\" >= 1 AND \"PublicPoints\" >= 0");
                    table.ForeignKey(
                        name: "FK_CompetitionFinalists_CompetitionFinals_FinalId",
                        column: x => x.FinalId,
                        principalTable: "CompetitionFinals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompetitionFinalists_Submissions_SubmissionId_CompetitionId",
                        columns: x => new { x.SubmissionId, x.CompetitionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "Id", "CompetitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinalJurors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FinalId = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinalJurors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinalJurors_CompetitionFinals_FinalId",
                        column: x => x.FinalId,
                        principalTable: "CompetitionFinals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FinalTieBreaks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FinalId = table.Column<int>(type: "integer", nullable: false),
                    SubmissionIds = table.Column<int[]>(type: "integer[]", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinalTieBreaks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinalTieBreaks_AspNetUsers_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FinalTieBreaks_CompetitionFinals_FinalId",
                        column: x => x.FinalId,
                        principalTable: "CompetitionFinals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JuryScores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FinalistId = table.Column<int>(type: "integer", nullable: false),
                    JurorId = table.Column<int>(type: "integer", nullable: false),
                    Idea = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    Execution = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    Relevance = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JuryScores", x => x.Id);
                    table.CheckConstraint("CK_JuryScores_Marks", "(\"Idea\" IS NULL OR (\"Idea\" >= 0 AND \"Idea\" <= 10 AND \"Idea\" * 2 = trunc(\"Idea\" * 2))) AND (\"Execution\" IS NULL OR (\"Execution\" >= 0 AND \"Execution\" <= 10 AND \"Execution\" * 2 = trunc(\"Execution\" * 2))) AND (\"Relevance\" IS NULL OR (\"Relevance\" >= 0 AND \"Relevance\" <= 10 AND \"Relevance\" * 2 = trunc(\"Relevance\" * 2)))");
                    table.ForeignKey(
                        name: "FK_JuryScores_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_JuryScores_CompetitionFinalists_FinalistId",
                        column: x => x.FinalistId,
                        principalTable: "CompetitionFinalists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JuryScores_FinalJurors_JurorId",
                        column: x => x.JurorId,
                        principalTable: "FinalJurors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinalists_FinalId_SubmissionId",
                table: "CompetitionFinalists",
                columns: new[] { "FinalId", "SubmissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinalists_SubmissionId_CompetitionId",
                table: "CompetitionFinalists",
                columns: new[] { "SubmissionId", "CompetitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinals_CompetitionId",
                table: "CompetitionFinals",
                column: "CompetitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinals_FrozenById",
                table: "CompetitionFinals",
                column: "FrozenById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinals_JuryLockedById",
                table: "CompetitionFinals",
                column: "JuryLockedById");

            migrationBuilder.CreateIndex(
                name: "IX_CompetitionFinals_PublishedById",
                table: "CompetitionFinals",
                column: "PublishedById");

            migrationBuilder.CreateIndex(
                name: "IX_FinalJurors_FinalId_SortOrder",
                table: "FinalJurors",
                columns: new[] { "FinalId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinalTieBreaks_DecidedById",
                table: "FinalTieBreaks",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_FinalTieBreaks_FinalId",
                table: "FinalTieBreaks",
                column: "FinalId");

            migrationBuilder.CreateIndex(
                name: "IX_JuryScores_FinalistId_JurorId",
                table: "JuryScores",
                columns: new[] { "FinalistId", "JurorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JuryScores_JurorId",
                table: "JuryScores",
                column: "JurorId");

            migrationBuilder.CreateIndex(
                name: "IX_JuryScores_UpdatedById",
                table: "JuryScores",
                column: "UpdatedById");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinalTieBreaks");

            migrationBuilder.DropTable(
                name: "JuryScores");

            migrationBuilder.DropTable(
                name: "CompetitionFinalists");

            migrationBuilder.DropTable(
                name: "FinalJurors");

            migrationBuilder.DropTable(
                name: "CompetitionFinals");
        }
    }
}
