using System.Data;
using System.Globalization;
using System.Text;
using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Net.Http.Headers;

namespace HarbourOps.Api.Features.WorkOrders.Export;

internal sealed record ExportRow(
    long Number,
    string Title,
    WorkOrderStatus Status,
    WorkOrderPriority Priority,
    WorkOrderCategory Category,
    string PropertyName,
    string RequesterName,
    string? TechnicianName,
    DateTimeOffset CreatedAt,
    DateTimeOffset DueAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    string Description);

/// <summary>
/// Manager-only CSV export of the work-order list, using exactly the list's search, filters, sort,
/// and organization scope, without paging.
/// </summary>
public sealed class WorkOrderExportService(AppDbContext db, TimeProvider clock, ILogger<WorkOrderExportService> logger)
{
    internal static readonly TimeZoneInfo Halifax = TimeZoneInfo.FindSystemTimeZoneById("America/Halifax");

    public async Task<IResult> ExportAsync(CurrentUser user, WorkOrderFilter filter, int maxRows, CancellationToken ct)
    {
        if (!user.IsManager)
        {
            return Failure.Forbidden("Only managers can export work orders.").ToProblem();
        }

        var now = clock.GetUtcNow();

        // REPEATABLE READ gives the count and the streamed rows the same snapshot, so the file always
        // contains exactly the number of rows that was checked against the limit.
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var matching = db.WorkOrders.AsNoTracking().VisibleTo(user).ApplyFilter(filter, now);
        var count = await matching.CountAsync(ct);

        if (count > maxRows)
        {
            await transaction.DisposeAsync();
            // Refuse rather than silently cut the file short.
            return new Failure(FailureKind.InvalidState,
                $"This export would contain {count:N0} work orders, more than the {maxRows:N0} limit. " +
                "Narrow the search or filters and try again.",
                Code: "export_too_large").ToProblem();
        }

        var rows = matching
            .ApplySort(filter)
            .Select(w => new ExportRow(
                w.Number, w.Title, w.Status, w.Priority, w.Category, w.Property.Name, w.Requester.DisplayName,
                w.AssignedTechnician != null ? w.AssignedTechnician.DisplayName : null,
                w.CreatedAt, w.DueAt, w.UpdatedAt, w.ResolvedAt, w.ClosedAt, w.Description))
            .AsAsyncEnumerable();

        logger.LogInformation(
            "User {UserId} exported {RowCount} work orders from organization {OrganizationId}",
            user.Id, count, user.OrganizationId);

        var fileName = $"harbourops-work-orders-{TimeZoneInfo.ConvertTime(now, Halifax):yyyyMMdd-HHmm}.csv";
        return new CsvExportResult(rows, transaction, count, fileName, now);
    }

    private sealed class CsvExportResult(
        IAsyncEnumerable<ExportRow> rows, IDbContextTransaction transaction, int count, string fileName, DateTimeOffset now)
        : IResult
    {
        private static readonly string[] Header =
        [
            "Reference", "Title", "Status", "Priority", "Category", "Property", "Requester", "Assigned technician",
            "Created (America/Halifax)", "Due (America/Halifax)", "Deadline", "Last updated (America/Halifax)",
            "Resolved (America/Halifax)", "Closed (America/Halifax)", "Description",
        ];

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            await using var _ = transaction;
            var response = httpContext.Response;
            var ct = httpContext.RequestAborted;

            response.ContentType = "text/csv; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Total-Count"] = count.ToString(CultureInfo.InvariantCulture);
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName,
                FileNameStar = fileName,
            }.ToString();

            // UTF-8 with a byte-order mark so Excel detects the encoding (accented names, emoji).
            await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            await writer.WriteAsync(Csv.Row(Header).AsMemory(), ct);

            var written = 0;
            // Stops as soon as the browser cancels the download (RequestAborted), freeing the DB connection.
            await foreach (var row in rows.WithCancellation(ct))
            {
                await writer.WriteAsync(Csv.Row(ToCells(row)).AsMemory(), ct);
                if (++written % 200 == 0)
                {
                    await writer.FlushAsync(ct);
                }
            }

            await writer.FlushAsync(ct);
        }

        private IEnumerable<string?> ToCells(ExportRow row) =>
        [
            $"WO-{row.Number}",
            row.Title,
            StatusLabel(row.Status),
            row.Priority.ToString(),
            row.Category.ToString(),
            row.PropertyName,
            row.RequesterName,
            row.TechnicianName ?? "Unassigned",
            Format(row.CreatedAt),
            Format(row.DueAt),
            WorkOrderDeadlines.IsOverdue(row.Status, row.DueAt, now) ? "Overdue"
                : WorkOrderDeadlines.IsDueSoon(row.Status, row.DueAt, now) ? "Due soon"
                : WorkOrderDeadlines.IsActive(row.Status) ? "On track" : "",
            Format(row.UpdatedAt),
            Format(row.ResolvedAt),
            Format(row.ClosedAt),
            row.Description,
        ];

        /// <summary>ISO 8601 with the Halifax offset, e.g. 2026-10-24T17:08:00-03:00: unambiguous and sortable.</summary>
        private static string Format(DateTimeOffset? value) =>
            value is { } v
                ? TimeZoneInfo.ConvertTime(v, Halifax).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
                : "";

        private static string StatusLabel(WorkOrderStatus status) =>
            status == WorkOrderStatus.InProgress ? "In progress" : status.ToString();
    }
}
