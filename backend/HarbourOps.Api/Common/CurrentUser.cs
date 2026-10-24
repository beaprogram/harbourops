using System.Security.Claims;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
    string OrganizationName)
{
    public bool IsManager => Role == UserRole.Manager;
    public bool IsTechnician => Role == UserRole.Technician;
    public bool IsRequester => Role == UserRole.Requester;
}

/// <summary>
/// Runs after cookie authentication. The cookie only proves *who* the caller is; this middleware
/// loads their current organization, role, and active flag from the database. A deleted or
/// deactivated account is signed out immediately instead of waiting for the cookie to expire.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments("/api"))
        {
            var currentUser = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                ? await db.Users
                    .AsNoTracking()
                    .Where(u => u.Id == userId && u.IsActive)
                    .Select(u => new CurrentUser(
                        u.Id, u.Email!, u.DisplayName, u.Role, u.OrganizationId, u.Organization.Name))
                    .SingleOrDefaultAsync(context.RequestAborted)
                : null;

            if (currentUser is null)
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                await ProblemWriter.WriteAsync(context, StatusCodes.Status401Unauthorized,
                    "Not signed in", "Your session is no longer valid. Please sign in again.", "unauthenticated");
                return;
            }

            context.Items[typeof(CurrentUser)] = currentUser;
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
