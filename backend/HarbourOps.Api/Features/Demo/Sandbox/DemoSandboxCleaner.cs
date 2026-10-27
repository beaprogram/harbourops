using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Attachments.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo.Sandbox;

public sealed record DemoCleanupReport(int Expired, int Cleaned, int Failed, int TombstonesPurged, int ObjectsDeleted);

/// <summary>Wakes the cleanup loop early, for example right after a visitor resets or ends their demo.</summary>
public sealed class DemoCleanupSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Request()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already requested; one pass handles every pending sandbox.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Removes ended sandboxes. Safe to run repeatedly and to retry after a partial failure (decisions §39):
///
/// 1. Revoke first. Access is already refused at expiry by CurrentUserMiddleware; revoking also makes every
///    quota UPDATE fail, so no request can add a row to the sandbox from now on.
/// 2. In one transaction, lock the sandbox's work orders FOR UPDATE. That waits for any comment, upload, or
///    workflow transaction still holding one of them, and blocks new ones until we're done.
/// 3. Delete the stored objects named by this sandbox's own photo rows. Storage keys are unique, so they can't
///    belong to live work elsewhere. Missing objects are fine (an earlier attempt may have removed them).
/// 4. Delete the rows, child tables first, and mark the sandbox cleaned. If anything fails, the transaction
///    rolls back and the next pass starts again from step 2.
///
/// It runs inside the app with the app's ordinary database rights. No public endpoint can trigger deletion
/// directly; reset and end only revoke and wake this loop.
/// </summary>
public sealed class DemoSandboxCleaner(
    AppDbContext db,
    IAttachmentStorage storage,
    TimeProvider clock,
    IOptions<DemoSandboxOptions> options,
    ILogger<DemoSandboxCleaner> logger)
{
    private const int BatchSize = 20;

    public async Task<DemoCleanupReport> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // When demo mode is switched off, every remaining sandbox is ended.
        var expired = await db.DemoSandboxes
            .Where(s => s.RevokedAt == null && (s.ExpiresAt <= now || !options.Value.Enabled))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, s => s.ExpiresAt <= now ? "expired" : "disabled"), ct);

        var pending = await db.DemoSandboxes.AsNoTracking()
            .Where(s => s.RevokedAt != null && s.CleanedAt == null)
            .OrderBy(s => s.RevokedAt)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        int cleaned = 0, failed = 0, objects = 0;
        foreach (var sandboxId in pending)
        {
            try
            {
                objects += await CleanAsync(sandboxId, ct);
                cleaned++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                db.ChangeTracker.Clear();
                logger.LogError(exception, "Cleaning demo sandbox {SandboxId} failed; it will be retried", sandboxId);
            }
        }

        var purgeBefore = now - options.Value.TombstoneRetention;
        var purged = await db.DemoSandboxes
            .Where(s => s.CleanedAt != null && s.CleanedAt < purgeBefore)
            .ExecuteDeleteAsync(ct);

        if (expired + cleaned + failed + purged > 0)
        {
            logger.LogInformation(
                "Demo cleanup: {Expired} expired, {Cleaned} cleaned ({Objects} stored objects deleted), {Failed} failed, {Purged} tombstones purged",
                expired, cleaned, objects, failed, purged);
        }

        return new DemoCleanupReport(expired, cleaned, failed, purged, objects);
    }

    /// <summary>Removes one sandbox's records and stored photos. Returns the number of stored objects deleted.</summary>
    public async Task<int> CleanAsync(Guid sandboxId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.DemoSandboxes.Where(s => s.Id == sandboxId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, "expired"), ct);

        var organizationIds = await db.Organizations
            .Where(o => o.DemoSandboxId == sandboxId)
            .Select(o => o.Id)
            .ToArrayAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (organizationIds.Length > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "WorkOrders" WHERE "OrganizationId" = ANY({organizationIds}) FOR UPDATE""", ct);
        }

        var photos = await db.Attachments
            .Where(a => organizationIds.Contains(a.OrganizationId))
            .Select(a => new { a.StorageKey, a.PreviewStorageKey })
            .ToListAsync(ct);
        foreach (var photo in photos)
        {
            await storage.DeleteAsync(photo.StorageKey, ct);
            await storage.DeleteAsync(photo.PreviewStorageKey, ct);
        }

        var userIds = await db.Users.Where(u => organizationIds.Contains(u.OrganizationId)).Select(u => u.Id).ToArrayAsync(ct);
        await db.Attachments.Where(a => organizationIds.Contains(a.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.Comments.Where(c => organizationIds.Contains(c.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.AuditEvents.Where(e => organizationIds.Contains(e.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.WorkOrders.Where(w => organizationIds.Contains(w.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.Properties.Where(p => organizationIds.Contains(p.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.DemoPersonas.Where(p => p.SandboxId == sandboxId).ExecuteDeleteAsync(ct);
        await db.UserClaims.Where(c => userIds.Contains(c.UserId)).ExecuteDeleteAsync(ct);
        await db.UserLogins.Where(l => userIds.Contains(l.UserId)).ExecuteDeleteAsync(ct);
        await db.UserTokens.Where(t => userIds.Contains(t.UserId)).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => organizationIds.Contains(u.OrganizationId)).ExecuteDeleteAsync(ct);
        await db.Organizations.Where(o => o.DemoSandboxId == sandboxId).ExecuteDeleteAsync(ct);
        await db.DemoSandboxes.Where(s => s.Id == sandboxId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.CleanedAt, now), ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Demo sandbox {SandboxId} cleaned: {Photos} photos removed", sandboxId, photos.Count);
        return photos.Count * 2;
    }
}

/// <summary>Runs <see cref="DemoSandboxCleaner"/> every CleanupInterval, or sooner when signalled.</summary>
public sealed class DemoSandboxCleanupService(
    IServiceScopeFactory scopes,
    DemoCleanupSignal signal,
    IOptions<DemoSandboxOptions> options,
    ILogger<DemoSandboxCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CleanupEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>().RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Demo cleanup pass failed; it will run again");
            }

            try
            {
                await signal.WaitAsync(options.Value.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
