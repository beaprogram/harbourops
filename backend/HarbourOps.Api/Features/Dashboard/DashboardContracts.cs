using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Api.Features.Dashboard;

/// <summary>
/// One summary number. <c>ListQuery</c> is the work-order list query string that produced
/// <c>Count</c>, so the card links to exactly the records it counted.
/// </summary>
public sealed record DashboardCard(string Key, string Title, string Description, int Count, string ListQuery);

/// <summary>
/// A technician's active workload: assigned work in Assigned (not started) or InProgress.
/// Technicians with no active work are included because they are the obvious people to assign next.
/// </summary>
public sealed record TechnicianWorkload(
    Guid Id,
    string DisplayName,
    bool IsActive,
    int NotStartedCount,
    int InProgressCount,
    int ActiveCount,
    int OverdueCount,
    string ActiveListQuery,
    string OverdueListQuery);

public sealed record OverdueWorkOrder(
    Guid Id,
    long Number,
    string Title,
    string PropertyName,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    string? AssignedTechnicianName,
    DateTimeOffset DueAt);

public sealed record DashboardResponse(
    DateTimeOffset AsOf,
    IReadOnlyList<DashboardCard> Cards,
    IReadOnlyList<TechnicianWorkload> Workload,
    IReadOnlyList<OverdueWorkOrder> OldestOverdue,
    string OverdueListQuery);
