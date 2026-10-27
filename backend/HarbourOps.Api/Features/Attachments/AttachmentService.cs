using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Attachments.Processing;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Attachments;

public enum AttachmentRendition
{
    Preview,
    Full,
}

public sealed record AttachmentContent(Stream Content, string MediaType, string FileName, long Length);

/// <summary>
/// Photo uploads, listings, and authorized delivery. Visibility is always the work order's own rule
/// (<see cref="WorkOrderQueries.VisibleTo"/>): managers see their organization, technicians only work currently
/// assigned to them, requesters only their own requests. Anything else is a 404 that reveals nothing.
/// </summary>
public sealed class AttachmentService(
    AppDbContext db,
    IAttachmentStorage storage,
    PhotoProcessor processor,
    TimeProvider clock,
    DemoQuotas demoQuotas,
    IOptions<AttachmentOptions> options,
    ILogger<AttachmentService> logger)
{
    private static readonly Failure NotFound = Failure.NotFound("Work order not found.");
    private static readonly Failure AttachmentNotFound = Failure.NotFound("Photo not found.");

    private AttachmentOptions Options => options.Value;

    public async Task<Result<AttachmentList>> ListAsync(CurrentUser user, Guid workOrderId, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.AsNoTracking().VisibleTo(user)
            .Where(w => w.Id == workOrderId)
            .Select(w => new { w.Status })
            .SingleOrDefaultAsync(ct);
        if (workOrder is null)
        {
            return NotFound;
        }

        var items = await db.Attachments.AsNoTracking()
            .Where(a => a.OrganizationId == user.OrganizationId && a.WorkOrderId == workOrderId)
            .OrderBy(a => a.UploadedAt).ThenBy(a => a.Id)
            .Select(a => new AttachmentResponse(
                a.Id, a.FileName, a.MediaType, a.SizeBytes, a.Width, a.Height, a.PreviewWidth, a.PreviewHeight,
                new PersonSummary(a.UploadedBy.Id, a.UploadedBy.DisplayName), a.UploadedBy.Role, a.UploadedAt,
                $"/api/work-orders/{workOrderId}/attachments/{a.Id}/preview",
                $"/api/work-orders/{workOrderId}/attachments/{a.Id}/content"))
            .ToListAsync(ct);

        var reason = CannotUploadReason(workOrder.Status, items.Count);
        return new AttachmentList(items, Limits(), reason is null, reason?.Message);
    }

    /// <summary>
    /// A cheap check before reading or decoding an upload: is the work order visible, open, and below the
    /// photo limit? It saves work for requests that would fail anyway. It is not the authoritative check;
    /// that happens again under a row lock in <see cref="UploadAsync"/>.
    /// </summary>
    public async Task<Failure?> PrecheckUploadAsync(CurrentUser user, Guid workOrderId, CancellationToken ct)
    {
        var workOrder = await db.WorkOrders.AsNoTracking().VisibleTo(user)
            .Where(w => w.Id == workOrderId)
            .Select(w => new { w.Status, Count = db.Attachments.Count(a => a.WorkOrderId == w.Id) })
            .SingleOrDefaultAsync(ct);
        return workOrder is null ? NotFound : CannotUploadReason(workOrder.Status, workOrder.Count);
    }

    /// <summary>
    /// Validates, stores, and records one photo. Storage and the database can't share a transaction, so the
    /// order is chosen so that a photo becomes visible only after everything has succeeded:
    ///  1. Decode and re-encode the image (invalid files never reach storage).
    ///  2. Write both renditions under fresh random keys. Nothing references them yet, so nobody can see them.
    ///  3. In one transaction: lock the work-order row, re-check visibility, status, and the photo count,
    ///     insert the metadata row, and commit. The photo exists from this moment.
    ///  4. If anything fails after step 2 (validation under the lock, database error, cancellation), delete
    ///     the stored objects. If even that fails (or the process crashes), the objects are unreferenced
    ///     and <see cref="AttachmentReconciler"/> removes them later.
    /// </summary>
    public async Task<Result<AttachmentResponse>> UploadAsync(
        CurrentUser user, Guid workOrderId, UploadedFile file, CancellationToken ct)
    {
        var processed = await processor.ProcessAsync(file.Content, ct);
        if (processed.Failure is { } invalid)
        {
            return invalid;
        }

        var photo = processed.Value!;
        var fullKey = StorageKeys.New();
        var previewKey = StorageKeys.New();
        var storedKeys = new List<string>(2);
        var committed = false;
        try
        {
            try
            {
                await storage.SaveAsync(fullKey, photo.Full.Bytes, ct);
                storedKeys.Add(fullKey);
                await storage.SaveAsync(previewKey, photo.Preview.Bytes, ct);
                storedKeys.Add(previewKey);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Photo storage failed for work order {WorkOrderId}", workOrderId);
                return new Failure(FailureKind.Unavailable,
                    "Photos can't be saved right now. Please try again in a few minutes.", Code: "storage_unavailable");
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // FOR NO KEY UPDATE conflicts with itself, with the FOR SHARE taken when commenting, and with the
            // UPDATE that closes a work order. Uploads to one work order therefore run one at a time, and an
            // upload either commits before a close or sees the Closed status afterwards.
            var target = await db.WorkOrders
                .FromSql($"""SELECT * FROM "WorkOrders" WHERE "Id" = {workOrderId} FOR NO KEY UPDATE""")
                .VisibleTo(user)
                .Select(w => new { w.Status })
                .SingleOrDefaultAsync(ct);
            if (target is null)
            {
                return NotFound;
            }

            // Counted in a separate statement *after* the lock is held. Under READ COMMITTED each statement sees
            // data committed before it started, so this count includes any upload that held the lock before us.
            // (Counting inside the locking statement would use a snapshot taken before we waited for the lock.)
            var count = await db.Attachments.CountAsync(a => a.WorkOrderId == workOrderId, ct);
            if (CannotUploadReason(target.Status, count) is { } refused)
            {
                return refused;
            }

            // Demo personas: the sandbox's photo and byte allowance, checked after the lock and in this transaction.
            // It also re-checks that the sandbox is still active, so an upload that finishes processing after the
            // demo ended never gains a row; its stored files are deleted below (decisions §38).
            if (await demoQuotas.TryStorePhotoAsync(user, photo.Full.Bytes.Length + photo.Preview.Bytes.Length, ct) is { } limited)
            {
                return limited;
            }

            var now = clock.GetUtcNow();
            var attachment = new Attachment
            {
                Id = Guid.CreateVersion7(now),
                OrganizationId = user.OrganizationId,
                WorkOrderId = workOrderId,
                UploadedById = user.Id,
                UploadedAt = now,
                FileName = DisplayFileName.Sanitize(file.SubmittedFileName),
                MediaType = PhotoProcessor.JpegMediaType,
                SourceMediaType = photo.SourceMediaType,
                StorageKey = fullKey,
                SizeBytes = photo.Full.Bytes.Length,
                Width = photo.Full.Width,
                Height = photo.Full.Height,
                PreviewStorageKey = previewKey,
                PreviewSizeBytes = photo.Preview.Bytes.Length,
                PreviewWidth = photo.Preview.Width,
                PreviewHeight = photo.Preview.Height,
            };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            committed = true;

            logger.LogInformation(
                "Photo {AttachmentId} added to work order {WorkOrderId} by user {UserId} ({SizeBytes} bytes)",
                attachment.Id, workOrderId, user.Id, attachment.SizeBytes);

            return new AttachmentResponse(
                attachment.Id, attachment.FileName, attachment.MediaType, attachment.SizeBytes, attachment.Width,
                attachment.Height, attachment.PreviewWidth, attachment.PreviewHeight,
                new PersonSummary(user.Id, user.DisplayName), user.Role, now,
                $"/api/work-orders/{workOrderId}/attachments/{attachment.Id}/preview",
                $"/api/work-orders/{workOrderId}/attachments/{attachment.Id}/content");
        }
        finally
        {
            if (!committed)
            {
                await DeleteQuietlyAsync(storedKeys);
            }
        }
    }

    /// <summary>Opens a photo for delivery after checking that the caller can currently see its work order.</summary>
    public async Task<Result<AttachmentContent>> OpenAsync(
        CurrentUser user, Guid workOrderId, Guid attachmentId, AttachmentRendition rendition, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == attachmentId
                && a.WorkOrderId == workOrderId
                && a.OrganizationId == user.OrganizationId
                && db.WorkOrders.VisibleTo(user).Any(w => w.Id == a.WorkOrderId))
            .Select(a => new { a.StorageKey, a.PreviewStorageKey, a.MediaType, a.FileName, a.SizeBytes, a.PreviewSizeBytes })
            .SingleOrDefaultAsync(ct);
        if (attachment is null)
        {
            return AttachmentNotFound;
        }

        var key = rendition == AttachmentRendition.Full ? attachment.StorageKey : attachment.PreviewStorageKey;
        var stream = await storage.OpenReadAsync(key, ct)
            ?? throw new InvalidOperationException($"Photo {attachmentId} has metadata but its stored object is missing.");
        var length = rendition == AttachmentRendition.Full ? attachment.SizeBytes : attachment.PreviewSizeBytes;
        return new AttachmentContent(stream, attachment.MediaType, attachment.FileName, length);
    }

    public AttachmentLimits Limits() => new(
        Options.MaxPerWorkOrder, Options.MaxFileSizeBytes, Options.MaxDimension, Options.MaxPixels,
        [PhotoProcessor.JpegMediaType, PhotoProcessor.PngMediaType]);

    private Failure? CannotUploadReason(WorkOrderStatus status, int count) =>
        status == WorkOrderStatus.Closed
            ? Failure.InvalidState("This work order is closed, so photos can't be added.", "work_order_closed")
            : count >= Options.MaxPerWorkOrder
                ? Failure.InvalidState($"This work order already has the maximum of {Options.MaxPerWorkOrder} photos.", "attachment_limit_reached")
                : null;

    private async Task DeleteQuietlyAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                // Not cancellable on purpose: cleanup must run even when the request was aborted.
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete unreferenced photo object {StorageKey}; reconciliation will remove it", key);
            }
        }
    }
}
