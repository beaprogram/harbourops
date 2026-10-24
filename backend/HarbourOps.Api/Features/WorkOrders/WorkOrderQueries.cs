using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;

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
}
