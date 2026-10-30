using System.Threading.Channels;
using MailKit.Net.Smtp;
using Meydan.Domain.Entities;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Email;

/// <summary>Durable e-mail queue: a row in PostgreSQL, delivered by <see cref="EmailOutboxWorker"/>.</summary>
public interface IEmailOutbox
{
    /// <param name="validFor">After this long the message is dropped (Expired) rather than sent late; null = default.</param>
    Task EnqueueAsync(string kind, Guid userId, int? subjectId, TimeSpan? validFor, CancellationToken ct);

    /// <summary>A mail to an email-first registration that has no account yet (its verification link).</summary>
    Task EnqueueForPendingRegistrationAsync(string kind, Guid pendingRegistrationId, TimeSpan? validFor, CancellationToken ct);
}

/// <summary>Builds the actual message at delivery time from the references in the outbox row (null = nothing to send).</summary>
public interface IEmailComposer
{
    Task<EmailMessage?> ComposeAsync(EmailOutboxMessage message, CancellationToken ct);
}

/// <summary>In-process nudge so a new row is picked up at once instead of at the next poll.</summary>
public sealed class EmailOutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _channel.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* poll interval elapsed */ }
    }
}

public sealed class EmailOutbox(MeydanDbContext db, TimeProvider clock, EmailOutboxSignal signal, IOptions<EmailOptions> options) : IEmailOutbox
{
    public Task EnqueueAsync(string kind, Guid userId, int? subjectId, TimeSpan? validFor, CancellationToken ct) =>
        AddAsync(kind, userId, null, subjectId, validFor, ct);

    public Task EnqueueForPendingRegistrationAsync(string kind, Guid pendingRegistrationId, TimeSpan? validFor, CancellationToken ct) =>
        AddAsync(kind, null, pendingRegistrationId, null, validFor, ct);

    private async Task AddAsync(string kind, Guid? userId, Guid? pendingId, int? subjectId, TimeSpan? validFor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var entry = db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Kind = kind,
            UserId = userId,
            PendingRegistrationId = pendingId,
            SubjectId = subjectId,
            Status = EmailOutboxStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
            ExpiresAt = now + (validFor ?? TimeSpan.FromDays(Math.Max(1, options.Value.DefaultValidityDays))),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // The caller's unit of work is shared: never leave a row behind that a later save would retry.
            entry.State = EntityState.Detached;
            throw;
        }
        signal.Wake();
    }
}

/// <summary>
/// Delivers outbox rows. Rows are claimed atomically (UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)) with a
/// lease, so several instances never work on the same row at the same time; a lease that runs out (crash mid-send)
/// makes the row available again — delivery is at-least-once, duplicates are rare but possible. Retries are bounded
/// with a growing delay; final states are Sent, Failed (retries exhausted), Expired or Cancelled. Old finished rows are
/// deleted after Email:RetentionDays.
/// </summary>
public sealed class EmailOutboxWorker(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    EmailOutboxSignal signal,
    IOptions<EmailOptions> options,
    TimeProvider clock,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    /// <summary>Identifies this process's leases (host name trimmed, process id, random suffix).</summary>
    private readonly string _instance =
        $"{Environment.MachineName[..Math.Min(40, Environment.MachineName.Length)]}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.OutboxWorkerEnabled)
        {
            logger.LogWarning("E-mail outbox worker disabled (Email:OutboxWorkerEnabled=false); messages stay pending");
            return;
        }
        await Task.Yield(); // never block host start-up
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessBatchAsync(stoppingToken) > 0) { }
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database briefly unavailable etc.: back off to the next poll; nothing is lost.
                logger.LogWarning("E-mail outbox poll failed: {ErrorType}", ex.GetType().Name);
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, o.PollSeconds)), stoppingToken);
        }
    }

    /// <summary>Claims and delivers one batch; returns how many rows were claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        List<long> ids;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
            var lease = now.AddSeconds(Math.Max(30, o.LeaseSeconds));
            var batch = Math.Clamp(o.BatchSize, 1, 100);
            ids = await db.Database.SqlQuery<long>($"""
                UPDATE "EmailOutbox" SET "LockedUntil" = {lease}, "LockedBy" = {_instance}, "AttemptCount" = "AttemptCount" + 1
                WHERE "Id" IN (
                    SELECT "Id" FROM "EmailOutbox"
                    WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {now}
                      AND ("LockedUntil" IS NULL OR "LockedUntil" < {now})
                    ORDER BY "NextAttemptAt", "Id"
                    LIMIT {batch}
                    FOR UPDATE SKIP LOCKED)
                RETURNING "Id" AS "Value"
                """).ToListAsync(ct);
        }
        foreach (var id in ids)
            await DeliverAsync(id, ct);
        return ids.Count;
    }

    private async Task DeliverAsync(long id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var o = options.Value;
        var now = clock.GetUtcNow();

        // The batch was claimed with one lease; earlier rows may have used part of it. Renew it for this row only while
        // it is still ours, so the time left always covers one compose + send (SMTP timeout < lease).
        var renewedUntil = now.AddSeconds(Math.Max(30, o.LeaseSeconds));
        var renewed = await db.Database.ExecuteSqlAsync($"""
            UPDATE "EmailOutbox" SET "LockedUntil" = {renewedUntil}
            WHERE "Id" = {id} AND "LockedBy" = {_instance} AND "Status" = 'Pending' AND "LockedUntil" >= {now}
            """, ct);
        if (renewed == 0) return; // lease lost to another instance (or row finished): not ours to send

        var row = await db.EmailOutbox.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null || row.LockedBy != _instance || row.Status != EmailOutboxStatus.Pending) return;

        if (row.ExpiresAt is { } expires && expires < now)
        {
            Finish(row, EmailOutboxStatus.Expired);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("E-mail {Kind} #{Id} expired before delivery", row.Kind, row.Id);
            return;
        }

        EmailMessage? message = null;
        try
        {
            message = await scope.ServiceProvider.GetRequiredService<IEmailComposer>().ComposeAsync(row, ct);
            if (message is null)
            {
                Finish(row, EmailOutboxStatus.Cancelled);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("E-mail {Kind} #{Id} cancelled: nothing to send", row.Kind, row.Id);
                return;
            }
            await sender.SendAsync(message, ct);
            if (sender is DisabledEmailSender)
            {
                // Mail switched off on purpose: nothing went out, so the row must not claim it did.
                Finish(row, EmailOutboxStatus.Cancelled);
                row.LastError = "EmailModeDisabled";
                await db.SaveChangesAsync(ct);
                return;
            }
            Finish(row, EmailOutboxStatus.Sent);
            row.SentAt = clock.GetUtcNow();
            row.LastError = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("E-mail {Kind} #{Id} delivered to {Recipient} (attempt {Attempt})",
                row.Kind, row.Id, EmailAddresses.Mask(message.To), row.AttemptCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another start delivers it.
        }
        catch (Exception ex)
        {
            // Type and SMTP status only: exception text can echo server responses; never credentials or bodies.
            var status = ex is SmtpCommandException sce ? $":{(int)sce.StatusCode}" : "";
            row.LastError = (ex.GetType().Name + status)[..Math.Min(120, (ex.GetType().Name + status).Length)];
            row.LockedUntil = null;
            row.LockedBy = null;
            var recipient = message is null ? "?" : EmailAddresses.Mask(message.To);
            if (row.AttemptCount >= Math.Max(1, o.MaxAttempts))
            {
                row.Status = EmailOutboxStatus.Failed;
                logger.LogError("E-mail {Kind} #{Id} to {Recipient} given up after {Attempts} attempts: {Error}",
                    row.Kind, row.Id, recipient, row.AttemptCount, row.LastError);
            }
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, o.RetryDelaySeconds) * Math.Pow(3, row.AttemptCount - 1));
                row.NextAttemptAt = clock.GetUtcNow() + (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay);
                logger.LogWarning("E-mail {Kind} #{Id} to {Recipient} failed (attempt {Attempt}/{Max}): {Error}; retry at {Next:o}",
                    row.Kind, row.Id, recipient, row.AttemptCount, o.MaxAttempts, row.LastError, row.NextAttemptAt);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static void Finish(EmailOutboxMessage row, EmailOutboxStatus status)
    {
        row.Status = status;
        row.LockedUntil = null;
        row.LockedBy = null;
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now - _lastCleanup < TimeSpan.FromHours(1)) return;
        _lastCleanup = now;
        var cutoff = now.AddDays(-Math.Max(1, options.Value.RetentionDays));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
        var removed = await db.EmailOutbox
            .Where(m => m.Status != EmailOutboxStatus.Pending && m.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
        if (removed > 0) logger.LogInformation("Removed {Count} finished e-mail outbox rows", removed);
    }
}
