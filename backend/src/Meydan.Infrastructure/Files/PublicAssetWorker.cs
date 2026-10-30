using System.Threading.Channels;
using Meydan.Domain.Entities;
using Meydan.Domain.Enums;
using Meydan.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meydan.Infrastructure.Files;

/// <summary>Requests a public-safe derivative for an uploaded file.</summary>
public interface IPublicAssetQueue
{
    void Enqueue(int submissionFileId);
}

/// <summary>
/// Builds public-safe derivatives in the background, one file at a time. Public requests never trigger a conversion —
/// they only ever serve what is ready, so anonymous traffic cannot be used to burn CPU or memory. The private original
/// is read, never modified, and never served in place of a derivative.
///
/// Two sources of work: new uploads (a bounded in-memory queue, always served first) and a backfill that walks pending
/// files in the database in small keyset batches with a pause after each file — at startup, then every
/// Uploads:DerivativeSweepMinutes. A large backlog therefore neither delays startup nor starves new uploads, and a
/// notification lost to a full queue or a restart is picked up by the next sweep.
///
/// A file that cannot be converted — corrupt, unsupported, original missing, or an unexpected decoder error — is
/// marked Failed (placeholder shown) so it is not retried forever; only infrastructure errors (storage I/O, database)
/// leave it Pending for the next sweep.
/// </summary>
public sealed class PublicAssetWorker(
    IServiceScopeFactory scopes,
    PublicAssetProcessor processor,
    IOptions<UploadOptions> options,
    ILogger<PublicAssetWorker> logger) : BackgroundService, IPublicAssetQueue
{
    private readonly Channel<int> _queue = Channel.CreateBounded<int>(
        new BoundedChannelOptions(Math.Max(10, options.Value.DerivativeQueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite, // still Pending in the database: the next sweep finds it
        });

    public void Enqueue(int submissionFileId) => _queue.Writer.TryWrite(submissionFileId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never block host start-up
        var o = options.Value;
        var sweepEvery = TimeSpan.FromMinutes(Math.Max(1, o.DerivativeSweepMinutes));
        var pause = TimeSpan.FromMilliseconds(Math.Max(0, o.DerivativeBackfillPauseMs));
        var batchSize = Math.Clamp(o.DerivativeBackfillBatchSize, 1, 500);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await BackfillAsync(batchSize, pause, stoppingToken);
                await BackfillThumbnailsAsync(batchSize, pause, stoppingToken);
                await DrainQueueAsync(stoppingToken);

                // Sleep until a new upload arrives or the next sweep is due.
                var sweepDue = DateTime.UtcNow + sweepEvery;
                while (DateTime.UtcNow < sweepDue && !stoppingToken.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(sweepDue - DateTime.UtcNow);
                    try
                    {
                        await _queue.Reader.WaitToReadAsync(wait.Token);
                        await DrainQueueAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        break; // sweep due
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down; unfinished files stay Pending
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (_queue.Reader.TryRead(out var id))
            await SafeProcessAsync(id, ct);
    }

    /// <summary>Walks pending files by id in batches; new uploads are served between every backfilled file.</summary>
    private async Task BackfillAsync(int batchSize, TimeSpan pause, CancellationToken ct)
    {
        var lastId = 0;
        var processed = 0;
        while (true)
        {
            List<int> batch;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
                batch = await db.SubmissionFiles.AsNoTracking()
                    .Where(f => f.Id > lastId && f.PublicAssetStatus == PublicAssetStatus.Pending
                                && f.Submission.Status != SubmissionStatus.Draft)
                    .OrderBy(f => f.Id)
                    .Select(f => f.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database not reachable: new uploads still enqueue themselves; the next sweep retries.
                logger.LogWarning("Public derivative backfill skipped: {ErrorType}", ex.GetType().Name);
                return;
            }
            if (batch.Count == 0) break;

            foreach (var id in batch)
            {
                await DrainQueueAsync(ct);
                await SafeProcessAsync(id, ct);
                lastId = id;
                processed++;
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
            }
        }
        if (processed > 0) logger.LogInformation("Public derivative backfill handled {Count} pending files", processed);
    }

    private async Task SafeProcessAsync(int id, CancellationToken ct)
    {
        try
        {
            await processor.ProcessAsync(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Storage or database trouble: the file stays Pending and the next sweep tries again.
            logger.LogError("Public derivative for file {FileId} postponed: {ErrorType}", id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Files whose derivative was made before thumbnails existed get their card-size copy from that derivative
    /// (small, already metadata-free) — the original is not read again. Paced like the main backfill.
    /// </summary>
    private async Task BackfillThumbnailsAsync(int batchSize, TimeSpan pause, CancellationToken ct)
    {
        var lastId = 0;
        var processed = 0;
        while (true)
        {
            List<int> batch;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MeydanDbContext>();
                batch = await db.SubmissionFiles.AsNoTracking()
                    .Where(f => f.Id > lastId && f.PublicAssetStatus == PublicAssetStatus.Ready
                                && f.ThumbnailStatus == PublicAssetStatus.Pending)
                    .OrderBy(f => f.Id)
                    .Select(f => f.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Thumbnail backfill skipped: {ErrorType}", ex.GetType().Name);
                return;
            }
            if (batch.Count == 0) break;

            foreach (var id in batch)
            {
                await DrainQueueAsync(ct);
                try
                {
                    await processor.ThumbnailFromDerivativeAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("Thumbnail for file {FileId} postponed: {ErrorType}", id, ex.GetType().Name);
                }
                lastId = id;
                processed++;
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
            }
        }
        if (processed > 0) logger.LogInformation("Thumbnail backfill handled {Count} files", processed);
    }

}
