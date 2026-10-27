using System.Security.Claims;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Common;

/// <summary>
/// The signed-in account as the server knows it. Built from the database on every API request,
/// never from anything the browser sends in a body or query string.
/// </summary>
public sealed record CurrentUser(
    Guid Id,
    string Email,
    string DisplayName,
    UserRole Role,
    Guid OrganizationId,
    string OrganizationName,
    Guid? DemoSandboxId = null,
    DateTimeOffset? DemoExpiresAt = null)
{
    public bool IsManager => Role == UserRole.Manager;
    public bool IsTechnician => Role == UserRole.Technician;
    public bool IsRequester => Role == UserRole.Requester;

    /// <summary>A persona inside a visitor's demo sandbox (subject to the sandbox's lifetime and limits).</summary>
    public bool IsDemo => DemoSandboxId is not null;
}

/// <summary>
/// Runs after cookie authentication. The cookie only proves *who* the caller is; this middleware
/// loads their current organization, role, and active flag from the database. A deleted or
/// deactivated account is signed out immediately instead of waiting for the cookie to expire.
///
/// For demo personas it also checks the sandbox on every request. Once the sandbox has expired, been
/// reset or ended, or demo mode is switched off, the cookie authorizes nothing: the caller continues as
/// anonymous (so "start a new demo" still works) and protected endpoints answer 401 "demo_expired".
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    /// <summary>Marks a request whose cookie belonged to a demo sandbox that has ended.</summary>
    public const string DemoEndedItem = "HarbourOps.DemoEnded";

    public async Task InvokeAsync(HttpContext context, AppDbContext db, TimeProvider clock, IOptions<DemoSandboxOptions> demo)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/api"))
        {
            var row = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                ? await db.Users
                    .AsNoTracking()
                    .Where(u => u.Id == userId && u.IsActive)
                    .Select(u => new
                    {
                        User = new CurrentUser(
                            u.Id, u.Email!, u.DisplayName, u.Role, u.OrganizationId, u.Organization.Name,
                            u.Organization.DemoSandboxId,
                            u.Organization.DemoSandbox != null ? u.Organization.DemoSandbox.ExpiresAt : null),
                        SandboxRevokedAt = u.Organization.DemoSandbox != null ? u.Organization.DemoSandbox.RevokedAt : null,
                    })
                    .SingleOrDefaultAsync(context.RequestAborted)
                : null;

            if (row is null)
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                await ProblemWriter.WriteAsync(context, StatusCodes.Status401Unauthorized,
                    "Not signed in", "Your session is no longer valid. Please sign in again.", "unauthenticated");
                return;
            }

            if (row.User.IsDemo
                && (!demo.Value.Enabled || row.SandboxRevokedAt is not null || row.User.DemoExpiresAt <= clock.GetUtcNow()))
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                context.Items[DemoEndedItem] = true;
            }
            else
            {
                context.Items[typeof(CurrentUser)] = row.User;
            }
        }

        await next(context);
    }
}

public static class CurrentUserServiceCollectionExtensions
{
    /// <summary>
    /// Makes <see cref="CurrentUser"/> injectable into endpoint handlers. Only endpoints that
    /// require authorization may ask for it; the authorization middleware guarantees a signed-in caller.
    /// </summary>
    public static IServiceCollection AddCurrentUser(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items[typeof(CurrentUser)] as CurrentUser
            ?? throw new InvalidOperationException(
                "CurrentUser is only available on endpoints that require an authenticated user."));
        return services;
    }
}
