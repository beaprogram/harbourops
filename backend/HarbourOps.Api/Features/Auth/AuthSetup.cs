using System.Threading.RateLimiting;
using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Users;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Auth;

public static class AuthSetup
{
    public const string LoginRateLimitPolicy = "login";

    /// <summary>
    /// ASP.NET Core Identity (password hashing, lockout) with cookie authentication.
    /// No tokens are handed to JavaScript: the session lives in an HttpOnly cookie.
    /// </summary>
    public static IServiceCollection AddHarbourAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddAuthorization();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<SecurityOptions>>((options, security) =>
            {
                options.Cookie.Name = "HarbourOps.Auth";
                options.Cookie.HttpOnly = true;
                // Strict: browsers don't attach the cookie to requests that another site starts ("site" includes
                // sibling subdomains, so this complements, not replaces, the antiforgery check).
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = security.Value.CookieSecurePolicy;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // This is a JSON API: answer 401/403 with Problem Details instead of redirecting to an HTML login page.
                options.Events.OnRedirectToLogin = context =>
                    context.HttpContext.Items.ContainsKey(CurrentUserMiddleware.DemoEndedItem)
                        ? ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
                            "Demo session ended", "This demo session has ended. Start a new demo to keep exploring.", "demo_expired")
                        : ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
                            "Not signed in", "Sign in to continue.", "unauthenticated");
                options.Events.OnRedirectToAccessDenied = context => ProblemWriter.WriteAsync(
                    context.HttpContext, StatusCodes.Status403Forbidden,
                    "Not allowed", "You do not have permission to do that.", "forbidden");
            });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryMiddleware.HeaderName;
            options.Cookie.Name = "HarbourOps.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.SuppressXFrameOptionsHeader = true; // set globally by the security headers middleware
            // The token is only ever accepted from the X-XSRF-TOKEN header. Without this, validating a
            // multipart photo upload would make the framework read (and buffer) the whole form body first.
            options.SuppressReadingTokenFromFormBody = true;
        });
        services.AddOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>()
            .Configure<IOptions<SecurityOptions>>((options, security) =>
                options.Cookie.SecurePolicy = security.Value.CookieSecurePolicy);

        // Sign-in attempts are limited per client IP address, in addition to Identity's account lockout.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                var http = context.HttpContext;
                var isDemo = http.Request.Path.StartsWithSegments("/api/demo");
                http.Response.Headers.RetryAfter =
                    context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "60";
                await ProblemWriter.WriteAsync(http, StatusCodes.Status429TooManyRequests, "Too many attempts",
                    isDemo
                        ? "Too many demo sessions were started or switched from this network. Please wait a while and try again."
                        : "Too many sign-in attempts. Wait a minute and try again.",
                    "rate_limited");
            };
            options.AddPolicy(LoginRateLimitPolicy, httpContext =>
            {
                var limit = httpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value
                    .LoginPermitLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });

        return services;
    }
}
