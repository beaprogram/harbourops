using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo.Sandbox;

public enum DemoQuota
{
    WorkOrder,
    Comment,
    Action,
    Upload,
    Export,
}

/// <summary>
/// Per-sandbox limits for demo personas. Ordinary users are never limited here.
///
/// Each check is a single conditional UPDATE ("add one if still active and below the limit"). PostgreSQL
/// re-evaluates the WHERE clause after waiting for any concurrent update of the same row, so parallel requests
/// can't overshoot a limit, and none of this depends on per-process counters. Callers run it inside the same
/// transaction as the write it allows, so a write that fails doesn't use up the allowance. The same UPDATE
/// also re-checks that the sandbox is still active: once a sandbox is revoked, no request can add to it, even
/// one that passed the per-request check moments earlier (decisions §38).
/// </summary>
public sealed class DemoQuotas(AppDbContext db, TimeProvider clock, IOptions<DemoSandboxOptions> options)
{
    public static readonly Failure Ended = new(FailureKind.Unauthenticated,
        "This demo session has ended. Start a new demo to keep exploring.", Code: "demo_expired");

    private DemoSandboxOptions Options => options.Value;

    /// <summary>Uses one unit of <paramref name="quota"/>, or explains why not. Always succeeds for non-demo users.</summary>
    public async Task<Failure?> TryUseAsync(CurrentUser user, DemoQuota quota, CancellationToken ct)
    {
        if (user.DemoSandboxId is not { } sandboxId)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var updated = quota switch
        {
            DemoQuota.WorkOrder => await db.Database.ExecuteSqlAsync($"""
                UPDATE "DemoSandboxes" SET "WorkOrderCount" = "WorkOrderCount" + 1
                WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now} AND "WorkOrderCount" < {Options.MaxWorkOrders}
                """, ct),
            DemoQuota.Comment => await db.Database.ExecuteSqlAsync($"""
                UPDATE "DemoSandboxes" SET "CommentCount" = "CommentCount" + 1
                WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now} AND "CommentCount" < {Options.MaxComments}
                """, ct),
            DemoQuota.Action => await db.Database.ExecuteSqlAsync($"""
                UPDATE "DemoSandboxes" SET "ActionCount" = "ActionCount" + 1
                WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now} AND "ActionCount" < {Options.MaxActions}
                """, ct),
            DemoQuota.Upload => await db.Database.ExecuteSqlAsync($"""
                UPDATE "DemoSandboxes" SET "UploadCount" = "UploadCount" + 1
                WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now} AND "UploadCount" < {Options.MaxUploads}
                """, ct),
            DemoQuota.Export => await db.Database.ExecuteSqlAsync($"""
                UPDATE "DemoSandboxes" SET "ExportCount" = "ExportCount" + 1
                WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now} AND "ExportCount" < {Options.MaxExports}
                """, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(quota)),
        };

        return updated == 1 ? null : await ExplainAsync(sandboxId, now, quota switch
        {
            DemoQuota.WorkOrder => $"This demo session can create up to {Options.MaxWorkOrders} requests.",
            DemoQuota.Comment => $"This demo session can post up to {Options.MaxComments} comments.",
            DemoQuota.Action => $"This demo session can make up to {Options.MaxActions} workflow changes.",
            DemoQuota.Upload => $"This demo session can try up to {Options.MaxUploads} photo uploads.",
            _ => $"This demo session can export up to {Options.MaxExports} times.",
        }, ct);
    }

    /// <summary>
    /// Records a stored photo against the sandbox's photo count and byte allowance. Runs inside the upload's
    /// transaction, after the work-order lock, so a revoked sandbox can never gain a photo row.
    /// </summary>
    public async Task<Failure?> TryStorePhotoAsync(CurrentUser user, long bytes, CancellationToken ct)
    {
        if (user.DemoSandboxId is not { } sandboxId)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE "DemoSandboxes" SET "AttachmentCount" = "AttachmentCount" + 1, "StoredBytes" = "StoredBytes" + {bytes}
            WHERE "Id" = {sandboxId} AND "RevokedAt" IS NULL AND "ExpiresAt" > {now}
              AND "AttachmentCount" < {Options.MaxAttachments} AND "StoredBytes" + {bytes} <= {Options.MaxStoredBytes}
            """, ct);
        return updated == 1 ? null : await ExplainAsync(sandboxId, now,
            $"This demo session can keep up to {Options.MaxAttachments} photos ({Options.MaxStoredBytes / 1048576.0:0.#} MB in total).", ct);
    }

    private async Task<Failure> ExplainAsync(Guid sandboxId, DateTimeOffset now, string limitMessage, CancellationToken ct)
    {
        var state = await db.DemoSandboxes.AsNoTracking()
            .Where(s => s.Id == sandboxId)
            .Select(s => new { s.RevokedAt, s.ExpiresAt })
            .SingleOrDefaultAsync(ct);
        return state is null || state.RevokedAt is not null || state.ExpiresAt <= now
            ? Ended
            : Failure.InvalidState(limitMessage + " Reset the demo to start again with fresh data.", "demo_limit_reached");
    }
}
