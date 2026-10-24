using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.Properties;

public static class PropertyEndpoints
{
    public static IEndpointRouteBuilder MapPropertyEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/properties", async (CurrentUser user, AppDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Properties
                    .AsNoTracking()
                    .Where(p => p.OrganizationId == user.OrganizationId && p.IsActive)
                    .OrderBy(p => p.Name)
                    .Select(p => new PropertySummary(p.Id, p.Name, p.AddressLine, p.Community))
                    .ToArrayAsync(ct)))
            .WithTags("Properties")
            .WithName("ListProperties")
            .WithSummary("Active properties in the current user's organization.")
            .RequireAuthorization();

        return api;
    }
}
