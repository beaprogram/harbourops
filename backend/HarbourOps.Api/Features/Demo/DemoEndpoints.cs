using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo;

public sealed record DemoAccount(string Email, string DisplayName, UserRole Role, string OrganizationName);

public sealed record DemoAccountsResponse(string Password, IReadOnlyList<DemoAccount> Accounts);

public static class DemoEndpoints
{
    /// <summary>
    /// Lists the fictional demo accounts for the login screen, only when demo mode is enabled.
    /// In ordinary production configuration this endpoint returns 404 and no demo accounts exist.
    /// </summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/demo/accounts", (IOptions<DemoDataOptions> options) =>
            {
                if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.Password))
                {
                    return Failure.NotFound("Demo mode is not enabled.").ToProblem();
                }

                var accounts = DemoData.Users
                    .Select(u => new DemoAccount(
                        u.Email, u.DisplayName, u.Role,
                        DemoData.Organizations.Single(o => o.Id == u.OrganizationId).Name))
                    .ToArray();
                return TypedResults.Ok(new DemoAccountsResponse(options.Value.Password, accounts));
            })
            .WithTags("Demo")
            .WithName("ListDemoAccounts")
            .Produces<DemoAccountsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AllowAnonymous();

        return api;
    }
}
