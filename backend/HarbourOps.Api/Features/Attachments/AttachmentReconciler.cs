using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Attachments.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Attachments;

public sealed record ReconciliationReport(int PartialWritesDeleted, int OrphansDeleted, int ObjectsKept);

/// <summary>
/// Removes stored photo objects that no attachment row references: leftovers from crashes or failed cleanups
/// between "stored" and "recorded" in <see cref="AttachmentService.UploadAsync"/>. It is deliberately conservative:
///  • Only objects older than <see cref="AttachmentCleanupOptions.MinimumAge"/> (default 24 h) are candidates.
///    An upload stores its objects and commits its row within one request, so an object that old with no row
///    will never get one.
///  • Each batch of candidates is checked against the database immediately before deletion, and any key that
///    a row references (as full image or preview) is kept.
///  • It never deletes because a row is missing an object; a missing object is logged as an error by delivery.
/// </summary>
public sealed class AttachmentReconciler(
    AppDbContext db,
    IAttachmentStorage storage,
    TimeProvider clock,
    IOptions<AttachmentOptions> options,
    ILogger<AttachmentReconciler> logger)
{
    private const int BatchSize = 500;

    public async Task<ReconciliationReport> RunAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - options.Value.Cleanup.MinimumAge;
        var partialWrites = await storage.DeleteAbandonedPartialWritesAsync(cutoff, ct);

        var candidates = new List<string>();
        var kept = 0;
        await foreach (var stored in storage.ListAsync(ct))
        {
            if (stored.LastModified < cutoff)
            {
                candidates.Add(stored.Key);
            }
            else
            {
                kept++;
            }
        }

        var orphans = 0;
        foreach (var batch in candidates.Chunk(BatchSize))
        {
            var referenced = (await db.Attachments.AsNoTracking()
                    .Where(a => batch.Contains(a.StorageKey) || batch.Contains(a.PreviewStorageKey))
                    .Select(a => new { a.StorageKey, a.PreviewStorageKey })
                    .ToListAsync(ct))
                .SelectMany(a => new[] { a.StorageKey, a.PreviewStorageKey })
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in batch)
            {
                if (referenced.Contains(key))
                {
                    kept++;
                    continue;
                }

                await storage.DeleteAsync(key, ct);
                orphans++;
                logger.LogInformation("Deleted unreferenced photo object {StorageKey}", key);
            }
        }

        logger.LogInformation(
            "Photo storage reconciliation: {PartialWrites} partial writes and {Orphans} orphaned objects deleted, {Kept} kept",
            partialWrites, orphans, kept);
        return new ReconciliationReport(partialWrites, orphans, kept);
    }
}

/// <summary>Runs <see cref="AttachmentReconciler"/> on a schedule when Attachments:Cleanup:Enabled is true.</summary>
public sealed class AttachmentCleanupService(
    IServiceScopeFactory scopes,
    IOptions<AttachmentOptions> options,
    TimeProvider clock,
    ILogger<AttachmentCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Cleanup.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Cleanup.Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AttachmentReconciler>().RunAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Photo storage reconciliation failed; it will run again at the next interval");
            }
        }
    }
}
