using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class PendingRegistrationConfiguration : IEntityTypeConfiguration<PendingRegistration>
{
    public void Configure(EntityTypeBuilder<PendingRegistration> b)
    {
        b.ToTable("PendingRegistrations");
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
        // One registration in flight per address: two at once could otherwise both end in an account.
        b.HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Property(x => x.ProofHash).HasMaxLength(64);
        b.HasIndex(x => x.ProofHash).IsUnique().HasFilter("\"ProofHash\" IS NOT NULL");
        // The sweep of stale rows.
        b.HasIndex(x => x.ExpiresAt);
        b.HasMany(x => x.Links).WithOne().HasForeignKey(x => x.PendingRegistrationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PendingRegistrationLinkConfiguration : IEntityTypeConfiguration<PendingRegistrationLink>
{
    public void Configure(EntityTypeBuilder<PendingRegistrationLink> b)
    {
        b.ToTable("PendingRegistrationLinks");
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
    }
}

internal sealed class QrlogLoginTicketConfiguration : IEntityTypeConfiguration<QrlogLoginTicket>
{
    public void Configure(EntityTypeBuilder<QrlogLoginTicket> b)
    {
        b.ToTable("QrlogLoginTickets");
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.ClaimSecretHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReturnTo).HasMaxLength(512).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(64);
        b.Property(x => x.FirstName).HasMaxLength(120);
        b.Property(x => x.LastName).HasMaxLength(120);
        b.Property(x => x.Email).HasMaxLength(256);
        // The daily sweep of finished rows.
        b.HasIndex(x => x.ExpiresAt);
        // A link ticket dies with the account it was started for.
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.LinkUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
