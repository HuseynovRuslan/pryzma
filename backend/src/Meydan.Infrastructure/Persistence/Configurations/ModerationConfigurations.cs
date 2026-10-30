using Meydan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meydan.Infrastructure.Persistence.Configurations;

internal sealed class SubmissionNoteConfiguration : IEntityTypeConfiguration<SubmissionNote>
{
    public void Configure(EntityTypeBuilder<SubmissionNote> b)
    {
        b.Property(x => x.Text).HasMaxLength(4000).IsRequired();
        b.HasIndex(x => new { x.SubmissionId, x.CreatedAt });
        b.HasOne(x => x.Submission)
            .WithMany(s => s.Notes)
            .HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Author)
            .WithMany()
            .HasForeignKey(x => x.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubmissionStatusChangeConfiguration : IEntityTypeConfiguration<SubmissionStatusChange>
{
    public void Configure(EntityTypeBuilder<SubmissionStatusChange> b)
    {
        b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Comment).HasMaxLength(1000);
        b.HasIndex(x => new { x.SubmissionId, x.ChangedAt });
        b.HasOne(x => x.Submission)
            .WithMany(s => s.StatusChanges)
            .HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.ChangedBy)
            .WithMany()
            .HasForeignKey(x => x.ChangedById)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(x => x.Action).HasMaxLength(40).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(40).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(500);
        b.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class CompetitionResultConfiguration : IEntityTypeConfiguration<CompetitionResult>
{
    public void Configure(EntityTypeBuilder<CompetitionResult> b)
    {
        // One result per competition — held by the database, not only the code.
        b.HasIndex(x => x.CompetitionId).IsUnique();
        b.HasOne(x => x.Competition)
            .WithOne(c => c.Result)
            .HasForeignKey<CompetitionResult>(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // The overall winner must be a submission of this same competition: the foreign key covers both columns
        // and points at the (Id, CompetitionId) pair of Submissions, so a winner from another competition cannot
        // be stored even by hand.
        b.HasOne(x => x.OverallWinner)
            .WithMany()
            .HasForeignKey(x => new { x.OverallWinnerSubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);

        // Second and third place: bound to the competition the same way, and never a work that already holds
        // another place. Filling them from the top is the code's rule, not the database's: a permanently deleted
        // work can leave a gap, which the admin is then shown instead of a deletion that fails.
        b.HasOne(x => x.SecondPlace)
            .WithMany()
            .HasForeignKey(x => new { x.SecondPlaceSubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ThirdPlace)
            .WithMany()
            .HasForeignKey(x => new { x.ThirdPlaceSubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_CompetitionResults_DistinctPlaces",
            "(\"SecondPlaceSubmissionId\" IS NULL OR \"SecondPlaceSubmissionId\" IS DISTINCT FROM \"OverallWinnerSubmissionId\")"
            + " AND (\"ThirdPlaceSubmissionId\" IS NULL OR (\"ThirdPlaceSubmissionId\" IS DISTINCT FROM \"OverallWinnerSubmissionId\""
            + " AND \"ThirdPlaceSubmissionId\" IS DISTINCT FROM \"SecondPlaceSubmissionId\"))"));

        b.Property(x => x.Announcement).HasMaxLength(2000);
        b.Property(x => x.CoverImage).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OverallWinnerSelectedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SecondPlaceSelectedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ThirdPlaceSelectedById).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.PublishedById).OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>
/// The two podiums of a two-choice result. Held by the database as well as the code: three places at most per podium,
/// a work once per podium, the work bound to this very competition, points never negative. Deleting the competition
/// takes its podiums; a work on a podium cannot be deleted.
/// </summary>
internal sealed class CompetitionAwardConfiguration : IEntityTypeConfiguration<CompetitionAward>
{
    public void Configure(EntityTypeBuilder<CompetitionAward> b)
    {
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ParticipantName).HasMaxLength(CompetitionAward.NameMax).IsRequired();
        b.Property(x => x.Source).HasMaxLength(CompetitionAward.SourceMax).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.CompetitionId, x.Category, x.Place }).IsUnique();
        b.HasIndex(x => new { x.CompetitionId, x.Category, x.SubmissionId }).IsUnique();
        b.HasOne(x => x.Competition).WithMany().HasForeignKey(x => x.CompetitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Submission)
            .WithMany()
            .HasForeignKey(x => new { x.SubmissionId, x.CompetitionId })
            .HasPrincipalKey(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecordedById).OnDelete(DeleteBehavior.SetNull);
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_CompetitionAwards_Place", "\"Place\" BETWEEN 1 AND 3");
            t.HasCheckConstraint("CK_CompetitionAwards_Points", "\"Points\" >= 0");
        });
    }
}

internal sealed class CompetitionCategoryWinnerConfiguration : IEntityTypeConfiguration<CompetitionCategoryWinner>
{
    public void Configure(EntityTypeBuilder<CompetitionCategoryWinner> b)
    {
        b.HasOne(x => x.Competition)
            .WithMany(c => c.CategoryWinners)
            .HasForeignKey(x => x.CompetitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One winner per category (the one-to-one makes (CompetitionCategoryId, CompetitionId) unique), and the
        // category must belong to this competition: the key points at the (Id, CompetitionId) pair of categories.
        b.HasOne(x => x.Category)
            .WithOne(c => c.Winner)
            .HasForeignKey<CompetitionCategoryWinner>(x => new { x.CompetitionCategoryId, x.CompetitionId })
            .HasPrincipalKey<CompetitionCategory>(c => new { c.Id, c.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);

        // A work wins at most once, and only within its own competition. That it wins only its OWN category is a
        // third key, (SubmissionId, CompetitionCategoryId) → Submissions (Id, CompetitionCategoryId), created in
        // the migration: EF cannot model a key over the submission's nullable category column.
        b.HasOne(x => x.Submission)
            .WithOne(s => s.CategoryWin)
            .HasForeignKey<CompetitionCategoryWinner>(x => new { x.SubmissionId, x.CompetitionId })
            .HasPrincipalKey<Submission>(s => new { s.Id, s.CompetitionId })
            .OnDelete(DeleteBehavior.Restrict);

        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SelectedById).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> b)
    {
        b.ToTable("EmailOutbox");
        b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LockedBy).HasMaxLength(120);
        b.Property(x => x.LastError).HasMaxLength(120);
        // The worker's claim query: pending rows that are due.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        // A deleted account takes its pending mail with it, and so does a pending registration once it is used.
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PendingRegistration>().WithMany().HasForeignKey(x => x.PendingRegistrationId).OnDelete(DeleteBehavior.Cascade);
        // Every mail is for someone: an account or a pending registration, never neither.
        b.ToTable(t => t.HasCheckConstraint("CK_EmailOutbox_HasRecipient",
            "\"UserId\" IS NOT NULL OR \"PendingRegistrationId\" IS NOT NULL"));
    }
}

/// <summary>
/// The voting rule, kept where it can actually be enforced.
///
/// <para>
/// A person votes once per nomination, and that is a unique index — not a check in the service. Two requests
/// that both looked before they leapt would both find nothing and both insert; the index is what makes the
/// second one fail, whichever of them it turns out to be.
/// </para>
/// <para>
/// The key that ties a vote's nomination to its work — (SubmissionId, CompetitionCategoryId) pointing at
/// Submissions (Id, CompetitionCategoryId) — is written by hand in the migration, for the same reason the
/// category winner's is: EF cannot model a key over the submission's nullable category column.
/// </para>
/// </summary>
internal sealed class VoteConfiguration : IEntityTypeConfiguration<Vote>
{
    public void Configure(EntityTypeBuilder<Vote> b)
    {
        // The rule.
        b.HasIndex(x => new { x.UserId, x.CompetitionCategoryId }).IsUnique();

        // Counting votes per work is the busiest read on the page.
        b.HasIndex(x => x.SubmissionId);

        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        // A vote set aside as fraudulent stays, and is read by nothing that does not ask for it by name
        // (IgnoreQueryFilters): counts, rankings and results see valid votes only. D12.
        b.HasQueryFilter(x => x.InvalidatedAt == null);
        b.Property(x => x.InvalidationReason).HasMaxLength(500);
        // Set aside means set aside for a stated reason; a valid vote carries none. Every term is NOT NULL-safe on
        // purpose: a CHECK that evaluates to NULL passes, so a missing reason must make it false, not unknown.
        b.ToTable(t => t.HasCheckConstraint("CK_Votes_InvalidationHasReason",
            "(\"InvalidatedAt\" IS NULL AND \"InvalidationReason\" IS NULL) OR (\"InvalidatedAt\" IS NOT NULL AND \"InvalidationReason\" IS NOT NULL AND length(btrim(\"InvalidationReason\")) > 0)"));
        b.HasOne(x => x.InvalidatedBy).WithMany().HasForeignKey(x => x.InvalidatedById).OnDelete(DeleteBehavior.SetNull);

        // A work and a nomination that still have votes cannot quietly disappear from under them.
        b.HasOne(x => x.Submission).WithMany(s => s.Votes).HasForeignKey(x => x.SubmissionId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Category).WithMany(c => c.Votes).HasForeignKey(x => x.CompetitionCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
