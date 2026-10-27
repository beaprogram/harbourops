using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.WorkOrders;

public static class WorkOrderQueries
{
    /// <summary>
    /// The single definition of "which work orders can this user see". Every work-order read and
    /// write starts here, so organization isolation and role visibility cannot drift apart:
    ///   Manager    → every work order in their organization
    ///   Technician → only work assigned to them
    ///   Requester  → only requests they created
    /// Anything outside this set is reported as 404 Not Found, whether it belongs to another
    /// organization or to a colleague, so responses never reveal that a record exists.
    /// </summary>
    public static IQueryable<WorkOrder> VisibleTo(this IQueryable<WorkOrder> query, CurrentUser user)
    {
        var sameOrganization = query.Where(w => w.OrganizationId == user.OrganizationId);
        return user.Role switch
        {
            UserRole.Manager => sameOrganization,
            UserRole.Technician => sameOrganization.Where(w => w.AssignedTechnicianId == user.Id),
            UserRole.Requester => sameOrganization.Where(w => w.RequesterId == user.Id),
            _ => sameOrganization.Where(_ => false),
        };
    }

    /// <summary>
    /// New, Assigned, or InProgress. Written as literal comparisons (not a parameter array) so the SQL
    /// is <c>"Status" IN ('New', 'Assigned', 'InProgress')</c>, which matches the predicate of the
    /// partial deadline index and lets PostgreSQL use it.
    /// </summary>
    public static IQueryable<WorkOrder> WhereActive(this IQueryable<WorkOrder> query) =>
        query.Where(w => w.Status == WorkOrderStatus.New
            || w.Status == WorkOrderStatus.Assigned
            || w.Status == WorkOrderStatus.InProgress);

    /// <summary>SQL form of <see cref="WorkOrderDeadlines.IsOverdue"/>.</summary>
    public static IQueryable<WorkOrder> WhereOverdue(this IQueryable<WorkOrder> query, DateTimeOffset now) =>
        query.WhereActive().Where(w => w.DueAt < now);

    /// <summary>SQL form of <see cref="WorkOrderDeadlines.IsDueSoon"/>.</summary>
    public static IQueryable<WorkOrder> WhereDueSoon(this IQueryable<WorkOrder> query, DateTimeOffset now)
    {
        var limit = WorkOrderDeadlines.DueSoonLimit(now);
        return query.WhereActive().Where(w => w.DueAt >= now && w.DueAt <= limit);
    }

    /// <summary>
    /// Applies every filter in the database. Different filters combine with AND; multiple values of the
    /// same filter (status=New&amp;status=Assigned) combine with OR. Always call after
    /// <see cref="VisibleTo"/> so counts and results only ever include permitted records.
    /// </summary>
    public static IQueryable<WorkOrder> ApplyFilter(this IQueryable<WorkOrder> query, WorkOrderFilter filter, DateTimeOffset now)
    {
        if (filter.Search is { } search)
        {
            query = (search.TsQuery, search.Number) switch
            {
                ({ } tsQuery, { } number) => query.Where(w =>
                    w.Number == number || w.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery))),
                ({ } tsQuery, null) => query.Where(w => w.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery))),
                (null, { } number) => query.Where(w => w.Number == number),
                // Punctuation-only text has no searchable words, so nothing matches it.
                (null, null) => query.Where(_ => false),
            };
        }

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses;
            query = query.Where(w => statuses.Contains(w.Status));
        }

        if (filter.Priorities.Count > 0)
        {
            var priorities = filter.Priorities;
            query = query.Where(w => priorities.Contains(w.Priority));
        }

        if (filter.PropertyId is { } propertyId)
        {
            query = query.Where(w => w.PropertyId == propertyId);
        }

        if (filter.TechnicianId is { } technicianId)
        {
            query = query.Where(w => w.AssignedTechnicianId == technicianId);
        }

        if (filter.Unassigned)
        {
            query = query.Where(w => w.AssignedTechnicianId == null);
        }

        query = filter.Deadline switch
        {
            DeadlineFilter.Overdue => query.WhereOverdue(now),
            DeadlineFilter.DueSoon => query.WhereDueSoon(now),
            _ => query,
        };

        return query;
    }

    /// <summary>
    /// Sorts in the database from an allowlisted field, always ending with Id so rows with equal
    /// values keep a stable order across pages.
    /// Priority order is High above Normal (rank 1 vs 0); ties within a priority are broken by the
    /// earliest due date, then Id, whatever the chosen direction.
    /// </summary>
    public static IOrderedQueryable<WorkOrder> ApplySort(this IQueryable<WorkOrder> query, WorkOrderFilter filter) =>
        (filter.Sort, filter.Descending) switch
        {
            (WorkOrderSortField.Created, true) => query.OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id),
            (WorkOrderSortField.Created, false) => query.OrderBy(w => w.CreatedAt).ThenBy(w => w.Id),
            (WorkOrderSortField.Updated, true) => query.OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id),
            (WorkOrderSortField.Updated, false) => query.OrderBy(w => w.UpdatedAt).ThenBy(w => w.Id),
            (WorkOrderSortField.Due, true) => query.OrderByDescending(w => w.DueAt).ThenByDescending(w => w.Id),
            (WorkOrderSortField.Due, false) => query.OrderBy(w => w.DueAt).ThenBy(w => w.Id),
            (WorkOrderSortField.Priority, true) => query
                .OrderByDescending(w => w.Priority == WorkOrderPriority.High ? 1 : 0)
                .ThenBy(w => w.DueAt).ThenBy(w => w.Id),
            (WorkOrderSortField.Priority, false) => query
                .OrderBy(w => w.Priority == WorkOrderPriority.High ? 1 : 0)
                .ThenBy(w => w.DueAt).ThenBy(w => w.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
}
