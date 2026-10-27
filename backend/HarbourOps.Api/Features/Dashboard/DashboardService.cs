using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.Dashboard;

/// <summary>
/// The manager dashboard. Every number is computed from real rows with the same
/// <see cref="WorkOrderQueries.VisibleTo"/> + <see cref="WorkOrderQueries.ApplyFilter"/> path as the list,
/// using one captured "now". A card and the list it links to apply the same definition, so they match when
/// evaluated over the same data and the same "now" (data or time can change between the two requests).
/// </summary>
public sealed class DashboardService(AppDbContext db, TimeProvider clock)
{
    public const int OldestOverdueLimit = 5;

    private static readonly WorkOrderFilter Active = new() { Statuses = WorkOrderFilter.ActiveStatuses };

    private static readonly WorkOrderFilter UnassignedActive = new()
    {
        Statuses = WorkOrderFilter.ActiveStatuses,
        Unassigned = true,
    };

    private static readonly WorkOrderFilter Overdue = new()
    {
        Deadline = DeadlineFilter.Overdue,
        Sort = WorkOrderSortField.Due,
        Descending = false,
    };

    private static readonly WorkOrderFilter DueSoon = new()
    {
        Deadline = DeadlineFilter.DueSoon,
        Sort = WorkOrderSortField.Due,
        Descending = false,
    };

    public async Task<Result<DashboardResponse>> GetAsync(CurrentUser user, CancellationToken ct)
    {
        if (!user.IsManager)
        {
            return Failure.Forbidden("Only managers can view the dashboard.");
        }

        var now = clock.GetUtcNow();
        var visible = db.WorkOrders.AsNoTracking().VisibleTo(user);

        Task<int> CountAsync(WorkOrderFilter filter) => visible.ApplyFilter(filter, now).CountAsync(ct);

        // Sequential awaits: a DbContext runs one query at a time.
        var cards = new List<DashboardCard>
        {
            new("active", "Active requests", "New, assigned, or in progress.", await CountAsync(Active), Active.ToQueryString()),
            new("unassigned", "Unassigned", "Active requests with no technician yet.", await CountAsync(UnassignedActive), UnassignedActive.ToQueryString()),
            new("overdue", "Overdue", "Active requests past their due time.", await CountAsync(Overdue), Overdue.ToQueryString()),
            new("dueSoon", "Due within 24 hours", "Active requests due in the next 24 hours.", await CountAsync(DueSoon), DueSoon.ToQueryString()),
        };

        var workload = await GetWorkloadAsync(user, visible, now, ct);

        var oldestOverdue = await visible
            .ApplyFilter(Overdue, now)
            .ApplySort(Overdue)
            .Take(OldestOverdueLimit)
            .Select(w => new OverdueWorkOrder(
                w.Id, w.Number, w.Title, w.Property.Name, w.Priority, w.Status,
                w.AssignedTechnician != null ? w.AssignedTechnician.DisplayName : null, w.DueAt))
            .ToListAsync(ct);

        return new DashboardResponse(now, cards, workload, oldestOverdue, Overdue.ToQueryString());
    }

    private async Task<List<TechnicianWorkload>> GetWorkloadAsync(
        CurrentUser user, IQueryable<WorkOrder> visible, DateTimeOffset now, CancellationToken ct)
    {
        var byStatus = await visible
            .WhereActive()
            .Where(w => w.AssignedTechnicianId != null)
            .GroupBy(w => new { TechnicianId = w.AssignedTechnicianId!.Value, w.Status })
            .Select(g => new { g.Key.TechnicianId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var overdue = await visible
            .WhereOverdue(now)
            .Where(w => w.AssignedTechnicianId != null)
            .GroupBy(w => w.AssignedTechnicianId!.Value)
            .Select(g => new { TechnicianId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TechnicianId, x => x.Count, ct);

        var technicianIdsWithWork = byStatus.Select(x => x.TechnicianId).Distinct().ToList();

        // Active technicians, plus any deactivated technician who still holds active work, so the
        // table always adds up to every assigned active request.
        var technicians = await db.Users
            .AsNoTracking()
            .Where(u => u.OrganizationId == user.OrganizationId
                && u.Role == UserRole.Technician
                && (u.IsActive || technicianIdsWithWork.Contains(u.Id)))
            .Select(u => new { u.Id, u.DisplayName, u.IsActive })
            .ToListAsync(ct);

        return technicians
            .Select(t =>
            {
                var notStarted = byStatus.Where(x => x.TechnicianId == t.Id && x.Status == WorkOrderStatus.Assigned).Sum(x => x.Count);
                var inProgress = byStatus.Where(x => x.TechnicianId == t.Id && x.Status == WorkOrderStatus.InProgress).Sum(x => x.Count);
                var activeFilter = new WorkOrderFilter
                {
                    TechnicianId = t.Id,
                    Statuses = [WorkOrderStatus.Assigned, WorkOrderStatus.InProgress],
                    Sort = WorkOrderSortField.Due,
                    Descending = false,
                };
                var overdueFilter = Overdue with { TechnicianId = t.Id };
                return new TechnicianWorkload(
                    t.Id, t.DisplayName, t.IsActive, notStarted, inProgress, notStarted + inProgress,
                    overdue.GetValueOrDefault(t.Id), activeFilter.ToQueryString(), overdueFilter.ToQueryString());
            })
            .OrderByDescending(w => w.ActiveCount)
            .ThenByDescending(w => w.OverdueCount)
            .ThenBy(w => w.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}
