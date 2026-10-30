using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> b)
    {
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        // Unique among submitted rows; drafts have no reference yet (NULLs do not collide).
        b.Property(x => x.ReferenceNumber).HasMaxLength(32);
        b.HasIndex(x => x.ReferenceNumber).IsUnique();
        b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.UserId, x.CompetitionId, x.Status });
        // Target of the "a winner belongs to its own category" key (see CompetitionCategoryWinnerConfiguration).
        b.HasIndex(x => new { x.Id, x.CompetitionCategoryId }).IsUnique();

        b.HasOne(x => x.User)
            .WithMany(u => u.Submissions)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Competition)
            .WithMany(c => c.Submissions)
            .HasForeignKey(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Restrict);
        // Categories live inside the competition; restrict so a category cannot vanish under a submission.
        b.HasOne(x => x.Category)
            .WithMany(c => c.Submissions)
            .HasForeignKey(x => x.CompetitionCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        // The chosen gallery image. That it belongs to this submission is checked on write and again on read.
        b.HasOne<SubmissionFile>()
            .WithMany()
            .HasForeignKey(x => x.PreviewFileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SubmissionFileConfiguration : IEntityTypeConfiguration<SubmissionFile>
{
    public void Configure(EntityTypeBuilder<SubmissionFile> b)
    {
        b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        b.Property(x => x.ScanStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PublicAssetKey).HasMaxLength(500);
        b.Property(x => x.PublicAssetStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PublicAssetError).HasMaxLength(40);
        b.Property(x => x.ThumbnailKey).HasMaxLength(500);
        b.Property(x => x.ThumbnailStatus).HasConversion<string>().HasMaxLength(20);
        b.ToTable(t => t.HasCheckConstraint("CK_SubmissionFiles_PreviewPage",
            "\"PreviewPage\" IS NULL OR \"PreviewPage\" >= 1"));
        b.HasOne(x => x.Submission)
            .WithMany(s => s.Files)
            .HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Field)
            .WithMany()
            .HasForeignKey(x => x.CompetitionFieldId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubmissionAnswerConfiguration : IEntityTypeConfiguration<SubmissionAnswer>
{
    public void Configure(EntityTypeBuilder<SubmissionAnswer> b)
    {
        b.Property(x => x.Value).HasMaxLength(5000);
        b.Property(x => x.FieldLabel).HasMaxLength(200);
        b.HasIndex(x => new { x.SubmissionId, x.CompetitionFieldId }).IsUnique();
        b.HasOne(x => x.Submission)
            .WithMany(s => s.Answers)
            .HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Field)
            .WithMany()
            .HasForeignKey(x => x.CompetitionFieldId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.ReplacedByHash).HasMaxLength(64);
        b.Property(x => x.CreatedByIp).HasMaxLength(64);
        b.HasIndex(x => x.FamilyId);
        b.HasOne(x => x.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReferenceCounterConfiguration : IEntityTypeConfiguration<ReferenceCounter>
{
    public void Configure(EntityTypeBuilder<ReferenceCounter> b)
    {
        b.HasKey(x => new { x.Prefix, x.Year });
        b.Property(x => x.Prefix).HasMaxLength(10);
    }
}

internal sealed class SeedHistoryConfiguration : IEntityTypeConfiguration<SeedHistory>
{
    public void Configure(EntityTypeBuilder<SeedHistory> b)
    {
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(100);
    }
}

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> b)
    {
        b.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(80).IsRequired();
        b.Property(x => x.PhoneNumber).HasMaxLength(32);
    }
}

internal sealed class PlatformSettingsConfiguration : IEntityTypeConfiguration<PlatformSettings>
{
    public void Configure(EntityTypeBuilder<PlatformSettings> b)
    {
        // One row, its key written by the code rather than by a sequence.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PlatformName).HasMaxLength(50).IsRequired();
        b.Property(x => x.HomeHeroTitle).HasMaxLength(120).IsRequired();
        b.Property(x => x.HomeHeroSubtitle).HasMaxLength(300).IsRequired();
        b.Property(x => x.AuthPromoTitle).HasMaxLength(120).IsRequired();
        b.Property(x => x.AuthPromoImage).HasMaxLength(500);
    }
}
