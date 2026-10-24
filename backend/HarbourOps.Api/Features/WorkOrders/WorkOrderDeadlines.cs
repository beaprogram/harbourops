namespace HarbourOps.Api.Features.WorkOrders;

/// <summary>
/// Demonstration deadline rules (documented in docs/decisions.md):
/// High = 24 elapsed hours, Normal = 72 elapsed hours, always measured from creation.
/// No business calendars, holidays, or pauses in this release.
/// </summary>
public static class WorkOrderDeadlines
{
    public static TimeSpan ResponseWindow(WorkOrderPriority priority) => priority switch
    {
        WorkOrderPriority.High => TimeSpan.FromHours(24),
        WorkOrderPriority.Normal => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    /// <summary>Priority changes recalculate from the original creation time, not from "now".</summary>
    public static DateTimeOffset CalculateDueAt(DateTimeOffset createdAt, WorkOrderPriority priority) =>
        createdAt + ResponseWindow(priority);

    /// <summary>Only open work (New, Assigned, InProgress) can be overdue.</summary>
    public static bool IsOverdue(WorkOrderStatus status, DateTimeOffset dueAt, DateTimeOffset now) =>
        (status is WorkOrderStatus.New or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress) && now > dueAt;
}
