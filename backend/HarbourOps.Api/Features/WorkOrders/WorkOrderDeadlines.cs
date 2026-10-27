namespace HarbourOps.Api.Features.WorkOrders;

/// <summary>
/// Demonstration deadline rules (documented in docs/decisions.md):
/// High = 24 elapsed hours, Normal = 72 elapsed hours, always measured from creation.
/// No business calendars, holidays, or pauses in this release.
///
/// Boundary definitions, shared by list flags, list filters, the dashboard, and the CSV export:
///   Active   = New, Assigned, or InProgress. Resolved and Closed work has no current deadline.
///   Overdue  = active and DueAt &lt; now                       (exactly at DueAt is not yet overdue)
///   Due soon = active and now &lt;= DueAt &lt;= now + 24 hours   (both ends inclusive)
/// Callers capture <c>now</c> once per operation so every calculation in a response uses the same boundary.
/// </summary>
public static class WorkOrderDeadlines
{
    public static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(24);

    public static TimeSpan ResponseWindow(WorkOrderPriority priority) => priority switch
    {
        WorkOrderPriority.High => TimeSpan.FromHours(24),
        WorkOrderPriority.Normal => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    /// <summary>Priority changes recalculate from the original creation time, not from "now".</summary>
    public static DateTimeOffset CalculateDueAt(DateTimeOffset createdAt, WorkOrderPriority priority) =>
        createdAt + ResponseWindow(priority);

    public static bool IsActive(WorkOrderStatus status) =>
        status is WorkOrderStatus.New or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress;

    /// <summary>Only active work can be overdue, and only strictly after its due time.</summary>
    public static bool IsOverdue(WorkOrderStatus status, DateTimeOffset dueAt, DateTimeOffset now) =>
        IsActive(status) && dueAt < now;

    /// <summary>Active work due between now and 24 hours from now, inclusive at both ends.</summary>
    public static bool IsDueSoon(WorkOrderStatus status, DateTimeOffset dueAt, DateTimeOffset now) =>
        IsActive(status) && dueAt >= now && dueAt <= DueSoonLimit(now);

    /// <summary>The latest due time that still counts as "due soon" at <paramref name="now"/>.</summary>
    public static DateTimeOffset DueSoonLimit(DateTimeOffset now) => now + DueSoonWindow;
}
