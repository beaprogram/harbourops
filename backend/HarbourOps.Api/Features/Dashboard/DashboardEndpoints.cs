using HarbourOps.Api.Common;

namespace HarbourOps.Api.Features.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/dashboard", async (CurrentUser user, DashboardService service, CancellationToken ct) =>
                (await service.GetAsync(user, ct)).ToOk())
            .WithTags("Dashboard")
            .WithName("GetDashboard")
            .WithSummary("Manager: what needs attention now, deadline pressure, and technician workload.")
            .Produces<DashboardResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        return api;
    }
}
