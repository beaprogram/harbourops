using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record OrganizationSummary(Guid Id, string Name);

public sealed record CurrentUserResponse(
    Guid Id, string Email, string DisplayName, UserRole Role, OrganizationSummary Organization);

public sealed record AntiforgeryTokenResponse(string Token, string HeaderName);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        // Issues the HttpOnly antiforgery cookie and returns the matching request token.
        // The frontend keeps the token in memory (never localStorage) and calls this again after
        // sign-in and sign-out, because the token is bound to the current user identity.
        api.MapGet("/antiforgery/token", (HttpContext context, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!, AntiforgeryMiddleware.HeaderName));
            })
            .WithTags("Auth")
            .WithName("GetAntiforgeryToken")
            .AllowAnonymous();

        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Sign in with email and password. Sets an HttpOnly session cookie.")
            .Produces<CurrentUserResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(AuthSetup.LoginRateLimitPolicy)
            .AllowAnonymous();

        auth.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
            {
                await signInManager.SignOutAsync();
                return TypedResults.NoContent();
            })
            .WithName("Logout")
            .AllowAnonymous();

        auth.MapGet("/me", (CurrentUser user) => TypedResults.Ok(ToResponse(user)))
            .WithName("GetCurrentUser")
            .Produces<CurrentUserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return api;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        AppDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("HarbourOps.Auth");
        var errors = new ValidationErrors();
        errors.RequireText("email", request.Email, 256, "Email");
        errors.RequireText("password", request.Password, 256, "Password");
        if (errors.HasErrors)
        {
            return errors.ToFailure().ToProblem();
        }

        // One generic message for every failure, so responses don't reveal which accounts exist.
        var invalidCredentials = TypedResults.Problem(
            detail: "Email or password is incorrect. After repeated failures, sign-in is paused for a few minutes.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Sign-in failed",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_credentials" });

        var user = await userManager.FindByEmailAsync(request.Email!.Trim());
        if (user is null || !user.IsActive)
        {
            logger.LogInformation("Sign-in failed: unknown or inactive account");
            return invalidCredentials;
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password!, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            logger.LogWarning("Sign-in failed for user {UserId} (locked out: {IsLockedOut})", user.Id, result.IsLockedOut);
            return invalidCredentials;
        }

        var organizationName = await db.Organizations
            .Where(o => o.Id == user.OrganizationId)
            .Select(o => o.Name)
            .SingleAsync(ct);

        logger.LogInformation("User {UserId} signed in to organization {OrganizationId}", user.Id, user.OrganizationId);
        return TypedResults.Ok(new CurrentUserResponse(
            user.Id, user.Email!, user.DisplayName, user.Role, new OrganizationSummary(user.OrganizationId, organizationName)));
    }

    private static CurrentUserResponse ToResponse(CurrentUser user) =>
        new(user.Id, user.Email, user.DisplayName, user.Role, new OrganizationSummary(user.OrganizationId, user.OrganizationName));
}
