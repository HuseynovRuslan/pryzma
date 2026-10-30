using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class CompetitionFinalConfiguration : IEntityTypeConfiguration<CompetitionFinal>
{
    public void Configure(EntityTypeBuilder<CompetitionFinal> b)
    {
        // One final per competition — held by the database, not only the code.
        b.HasIndex(x => x.CompetitionId).IsUnique();
        b.HasOne(x => x.Competition)
            .WithOne()
            .HasForeignKey<CompetitionFinal>(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Stage).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FrozenVotesFingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.FrozenById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.JuryLockedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.PublishedById).OnDelete(DeleteBehavior.SetNull);
        b.ToTable(t => t.HasCheckConstraint("CK_CompetitionFinals_Prizes",
            "\"FirstPrizeAzn\" >= 0 AND \"SecondPrizeAzn\" >= 0 AND \"ThirdPrizeAzn\" >= 0"));
    }
}

internal sealed class CompetitionFinalistConfiguration : IEntityTypeConfiguration<CompetitionFinalist>
{
    public void Configure(EntityTypeBuilder<CompetitionFinalist> b)
    {
        b.HasIndex(x => new { x.FinalId, x.SubmissionId }).IsUnique();
        b.HasOne(x => x.Final)
            .WithMany(f => f.Finalists)
            .HasForeignKey(x => x.FinalId)
            .OnDelete(DeleteBehavior.Cascade);
        // A finalist is a work of the final's own competition: the key covers both columns, as for the result's places.
        b.HasOne(x => x.Submission)
            .WithMany()
            .HasForeignKey(x => new { x.SubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.JuryTotal).HasPrecision(5, 1);
        b.ToTable(t => t.HasCheckConstraint("CK_CompetitionFinalists_Snapshot",
            "\"ValidVotes\" >= 0 AND \"PublicRank\" >= 1 AND \"PublicPoints\" >= 0"));
    }
}

internal sealed class FinalJurorConfiguration : IEntityTypeConfiguration<FinalJuror>
{
    public void Configure(EntityTypeBuilder<FinalJuror> b)
    {
        b.Property(x => x.Label).HasMaxLength(60).IsRequired();
        b.HasIndex(x => new { x.FinalId, x.SortOrder }).IsUnique();
        b.HasOne(x => x.Final)
            .WithMany(f => f.Jurors)
            .HasForeignKey(x => x.FinalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JuryScoreConfiguration : IEntityTypeConfiguration<JuryScore>
{
    private const string Mark = "IS NULL OR ({0} >= 0 AND {0} <= 10 AND {0} * 2 = trunc({0} * 2))";

    public void Configure(EntityTypeBuilder<JuryScore> b)
    {
        b.HasIndex(x => new { x.FinalistId, x.JurorId }).IsUnique();
        b.HasOne(x => x.Finalist)
            .WithMany(f => f.Scores)
            .HasForeignKey(x => x.FinalistId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Juror)
            .WithMany()
            .HasForeignKey(x => x.JurorId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedById).OnDelete(DeleteBehavior.SetNull);
        b.Property(x => x.Idea).HasPrecision(3, 1);
        b.Property(x => x.Execution).HasPrecision(3, 1);
        b.Property(x => x.Relevance).HasPrecision(3, 1);
        // 0–10 in steps of 0.5, whatever writes the row.
        b.ToTable(t => t.HasCheckConstraint("CK_JuryScores_Marks",
            $"(\"Idea\" {string.Format(Mark, "\"Idea\"")}) AND (\"Execution\" {string.Format(Mark, "\"Execution\"")})"
            + $" AND (\"Relevance\" {string.Format(Mark, "\"Relevance\"")})"));
    }
}

internal sealed class FinalTieBreakConfiguration : IEntityTypeConfiguration<FinalTieBreak>
{
    public void Configure(EntityTypeBuilder<FinalTieBreak> b)
    {
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => x.FinalId);
        b.HasOne(x => x.Final)
            .WithMany(f => f.TieBreaks)
            .HasForeignKey(x => x.FinalId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DecidedById).OnDelete(DeleteBehavior.SetNull);
    }
}
