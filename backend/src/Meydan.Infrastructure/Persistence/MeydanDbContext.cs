using Meydan.Domain.Common;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Meydan.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Meydan.Infrastructure.Persistence;

public class MeydanDbContext(DbContextOptions<MeydanDbContext> options, TimeProvider clock)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>ASP.NET Data Protection key ring (password-reset tokens): shared by all instances, backed up with the database.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<CompetitionCategory> CompetitionCategories => Set<CompetitionCategory>();
    public DbSet<CompetitionField> CompetitionFields => Set<CompetitionField>();
    public DbSet<Submission> Submissions => Set<Submission>();
    public DbSet<SubmissionFile> SubmissionFiles => Set<SubmissionFile>();
    public DbSet<SubmissionAnswer> SubmissionAnswers => Set<SubmissionAnswer>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ReferenceCounter> ReferenceCounters => Set<ReferenceCounter>();
    public DbSet<SeedHistory> SeedHistory => Set<SeedHistory>();
    public DbSet<SubmissionNote> SubmissionNotes => Set<SubmissionNote>();
    public DbSet<SubmissionStatusChange> SubmissionStatusChanges => Set<SubmissionStatusChange>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CompetitionResult> CompetitionResults => Set<CompetitionResult>();
    public DbSet<CompetitionCategoryWinner> CompetitionCategoryWinners => Set<CompetitionCategoryWinner>();
    public DbSet<CompetitionAward> CompetitionAwards => Set<CompetitionAward>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<QrlogLoginTicket> QrlogLoginTickets => Set<QrlogLoginTicket>();
    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();
    public DbSet<PendingRegistrationLink> PendingRegistrationLinks => Set<PendingRegistrationLink>();
    /// <summary>One row: the platform's own public words (see PlatformSettings).</summary>
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();

    /// <summary>Public votes. See <see cref="Vote"/>: the rule lives in a unique index, not in code.</summary>
    public DbSet<Vote> Votes => Set<Vote>();

    /// <summary>The jury final (D13): frozen finalists, the jury's marks and its tie decisions. Never touches Votes.</summary>
    public DbSet<CompetitionFinal> CompetitionFinals => Set<CompetitionFinal>();
    public DbSet<CompetitionFinalist> CompetitionFinalists => Set<CompetitionFinalist>();
    public DbSet<FinalJuror> FinalJurors => Set<FinalJuror>();
    public DbSet<JuryScore> JuryScores => Set<JuryScore>();
    public DbSet<FinalTieBreak> FinalTieBreaks => Set<FinalTieBreak>();
    public DbSet<JuryEvaluation> JuryEvaluations => Set<JuryEvaluation>();
    public DbSet<JuryRosterEntry> JuryRosterEntries => Set<JuryRosterEntry>();
    public DbSet<JuryAssignment> JuryAssignments => Set<JuryAssignment>();
    public DbSet<JurySession> JurySessions => Set<JurySession>();
    public DbSet<JuryWorkScore> JuryWorkScores => Set<JuryWorkScore>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(MeydanDbContext).Assembly);
    }

    /// <summary>
    /// Every DateTimeOffset is stored as PostgreSQL timestamptz in UTC. Npgsql refuses non-zero offsets, so values
    /// arriving with a local offset (e.g. an admin's "+04:00" deadline) are normalised to the same instant in UTC.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v.ToUniversalTime());
