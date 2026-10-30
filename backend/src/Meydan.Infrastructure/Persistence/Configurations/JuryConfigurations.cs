using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class JuryEvaluationConfiguration : IEntityTypeConfiguration<JuryEvaluation>
{
    public void Configure(EntityTypeBuilder<JuryEvaluation> b)
    {
        // One evaluation per competition — held by the database.
        b.HasIndex(x => x.CompetitionId).IsUnique();
        b.HasOne(x => x.Competition).WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Format).HasMaxLength(60).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RosterFrozenById).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class JuryRosterEntryConfiguration : IEntityTypeConfiguration<JuryRosterEntry>
{
    public void Configure(EntityTypeBuilder<JuryRosterEntry> b)
    {
        // Each work once, each position once; scores point at (evaluation, work).
        b.HasAlternateKey(x => new { x.EvaluationId, x.SubmissionId });
        b.HasIndex(x => new { x.EvaluationId, x.SortOrder }).IsUnique();
        b.Property(x => x.Reference).HasMaxLength(40).IsRequired();
        b.HasOne(x => x.Evaluation).WithMany(e => e.Roster).HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Restrict);
        // A roster work belongs to the evaluation's own competition.
        b.HasOne(x => x.Submission)
            .WithMany()
            .HasForeignKey(x => new { x.SubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JuryAssignmentConfiguration : IEntityTypeConfiguration<JuryAssignment>
{
    public void Configure(EntityTypeBuilder<JuryAssignment> b)
    {
        b.HasAlternateKey(x => new { x.EvaluationId, x.JurorId });
        b.HasOne(x => x.Evaluation).WithMany(e => e.Assignments).HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Restrict);
        // The juror's identity is never removed from under their scores.
        b.HasOne(x => x.Juror).WithMany().HasForeignKey(x => x.JurorId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.InviteTokenHash).HasMaxLength(64);
        b.Property(x => x.InviteCodeHash).HasMaxLength(200);
        b.HasIndex(x => x.InviteTokenHash).IsUnique().HasFilter("\"InviteTokenHash\" IS NOT NULL");
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SubmittedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.InviteIssuedById).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class JurySessionConfiguration : IEntityTypeConfiguration<JurySession>
{
    public void Configure(EntityTypeBuilder<JurySession> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JuryWorkScoreConfiguration : IEntityTypeConfiguration<JuryWorkScore>
{
    public void Configure(EntityTypeBuilder<JuryWorkScore> b)
    {
        // The owner's rule: one authoritative score per competition, juror and work — online and paper alike.
        b.HasIndex(x => new { x.CompetitionId, x.JurorId, x.SubmissionId }).IsUnique();
        // Only a work of the frozen roster, only by a juror of this evaluation.
        b.HasOne<JuryRosterEntry>().WithMany()
            .HasForeignKey(x => new { x.EvaluationId, x.SubmissionId })
            .HasPrincipalKey(r => new { r.EvaluationId, r.SubmissionId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JuryAssignment>().WithMany()
            .HasForeignKey(x => new { x.EvaluationId, x.JurorId })
            .HasPrincipalKey(a => new { a.EvaluationId, a.JurorId })
            .OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Comment).HasMaxLength(1000);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedById).OnDelete(DeleteBehavior.SetNull);
        b.ToTable(t => t.HasCheckConstraint("CK_JuryWorkScores_Score", "\"Score\" BETWEEN 1 AND 10"));
    }
}
