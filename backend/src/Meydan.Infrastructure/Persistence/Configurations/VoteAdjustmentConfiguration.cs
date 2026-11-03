using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class VoteAdjustmentConfiguration : IEntityTypeConfiguration<VoteAdjustment>
{
    public void Configure(EntityTypeBuilder<VoteAdjustment> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.EvidenceReference).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.HasIndex(x => new { x.CompetitionId, x.Status });
        b.HasIndex(x => new { x.SubmissionId, x.Status });
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_VoteAdjustments_Delta", "\"Delta\" <> 0 AND \"Delta\" BETWEEN -100 AND 100");
            t.HasCheckConstraint("CK_VoteAdjustments_Status",
                "\"Status\" IN ('Pending', 'Approved', 'Rejected')");
            t.HasCheckConstraint("CK_VoteAdjustments_Decision",
                "(\"Status\" = 'Pending' AND \"DecidedById\" IS NULL AND \"DecidedAt\" IS NULL) OR " +
                "(\"Status\" <> 'Pending' AND \"DecidedById\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL)");
        });
        b.HasOne(x => x.Competition).WithMany().HasForeignKey(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Submission).WithMany().HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RequestedById)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.DecidedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
