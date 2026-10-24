using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.Users;

public sealed record TechnicianResponse(Guid Id, string DisplayName, int OpenAssignments);

public static class TechnicianEndpoints
{
    public static IEndpointRouteBuilder MapTechnicianEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/technicians", async (CurrentUser user, AppDbContext db, CancellationToken ct) =>
            {
                if (!user.IsManager)
                {
                    return Failure.Forbidden("Only managers can view the technician list.").ToProblem();
                }

                var technicians = await db.Users
                    .AsNoTracking()
                    .Where(u => u.OrganizationId == user.OrganizationId && u.Role == UserRole.Technician && u.IsActive)
                    .OrderBy(u => u.DisplayName)
                    .Select(u => new TechnicianResponse(
                        u.Id,
                        u.DisplayName,
                        db.WorkOrders.Count(w => w.OrganizationId == user.OrganizationId
                            && w.AssignedTechnicianId == u.Id
                            && (w.Status == WorkOrderStatus.Assigned || w.Status == WorkOrderStatus.InProgress))))
                    .ToArrayAsync(ct);

                return TypedResults.Ok(technicians);
            })
            .WithTags("Technicians")
            .WithName("ListTechnicians")
            .WithSummary("Manager: active technicians in the organization, with their open workload.")
            .Produces<TechnicianResponse[]>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        return api;
    }
}
