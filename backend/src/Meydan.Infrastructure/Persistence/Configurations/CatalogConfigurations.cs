using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.ShortDescription).HasMaxLength(400).IsRequired();
        b.Property(x => x.CoverImage).HasMaxLength(500);
        b.Property(x => x.AccentColor).HasMaxLength(7);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.Status, x.SortOrder });
    }
}

internal sealed class CompetitionConfiguration : IEntityTypeConfiguration<Competition>
{
    public void Configure(EntityTypeBuilder<Competition> b)
    {
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.ShortDescription).HasMaxLength(500).IsRequired();
        b.Property(x => x.CoverImage).HasMaxLength(500);
        b.Property(x => x.Prize).HasMaxLength(1000);
        b.Property(x => x.CategoryLabel).HasMaxLength(80);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.ProjectId, x.Status });

        b.HasOne(x => x.Project)
            .WithMany(p => p.Competitions)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompetitionCategoryConfiguration : IEntityTypeConfiguration<CompetitionCategory>
{
    public void Configure(EntityTypeBuilder<CompetitionCategory> b)
    {
        b.Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.Image).HasMaxLength(500);
        b.HasIndex(x => new { x.CompetitionId, x.Slug }).IsUnique();

        b.HasOne(x => x.Competition)
            .WithMany(c => c.Categories)
            .HasForeignKey(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CompetitionFieldConfiguration : IEntityTypeConfiguration<CompetitionField>
{
    public void Configure(EntityTypeBuilder<CompetitionField> b)
    {
        b.Property(x => x.Key).HasMaxLength(60).IsRequired();
        b.Property(x => x.Label).HasMaxLength(200).IsRequired();
        b.Property(x => x.HelpText).HasMaxLength(1000);
        b.Property(x => x.Placeholder).HasMaxLength(200);
        b.Property(x => x.OptionsJson).HasMaxLength(4000);
        b.Property(x => x.FieldType).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.CompetitionId, x.Key }).IsUnique();

        b.HasOne(x => x.Competition)
            .WithMany(c => c.Fields)
            .HasForeignKey(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
