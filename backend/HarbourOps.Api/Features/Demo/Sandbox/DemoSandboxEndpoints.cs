using System.Threading.RateLimiting;
using HarbourOps.Api.Common;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo.Sandbox;

public static class DemoSandboxEndpoints
{
    public const string CreateRateLimitPolicy = "demo-create";
    public const string SwitchRateLimitPolicy = "demo-switch";

    public static IServiceCollection AddDemoSandboxes(this IServiceCollection services)
    {
        services.AddOptions<DemoSandboxOptions>()
            .BindConfiguration(DemoSandboxOptions.SectionName)
            .Validate(o => !o.Validate().Any(), "Invalid DemoSandbox configuration (see DemoSandboxOptions.Validate).")
            .ValidateOnStart();
        services.AddScoped<DemoSandboxService>();
        services.AddScoped<DemoQuotas>();
        services.AddScoped<DemoSandboxCleaner>();
        services.AddSingleton<DemoCleanupSignal>();
        services.AddHostedService<DemoSandboxCleanupService>();

        // Per client IP, in this process. The global caps (active sandboxes, creations per hour) are enforced in
        // the database instead, so they hold across concurrent requests and instances.
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(CreateRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<DemoSandboxOptions>>().Value.CreationsPerClientPerHour,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                }));
            options.AddPolicy(SwitchRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<DemoSandboxOptions>>().Value.PersonaSwitchesPerClientPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    /// <summary>
    /// Demo sandbox endpoints. All of them return 404 unless DemoSandbox:Enabled is true. POSTs need the antiforgery
    /// header like every other state-changing request (AntiforgeryMiddleware). None of them accepts a user,
    /// organization, role, or sandbox ID: the sandbox comes from the server-issued credential cookie.
    /// </summary>
    public static IEndpointRouteBuilder MapDemoSandboxEndpoints(this IEndpointRouteBuilder api)
    {
        var demo = api.MapGroup("/demo")
            .WithTags("Demo")
            .AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<DemoSandboxOptions>>().Value.Enabled
                    ? await next(context)
                    : Failure.NotFound("Demo mode is not enabled.").ToProblem());

        demo.MapGet("/session", async (HttpContext http, DemoSandboxService service, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await service.GetSessionAsync(http, ct));
            })
            .WithName("GetDemoSession")
            .WithSummary("This browser's demo status: none, active (with personas, expiry, and usage), or ended.")
            .Produces<DemoSessionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        demo.MapPost("/sessions", async (HttpContext http, DemoSandboxService service, CancellationToken ct) =>
                Respond(http, await service.StartAsync(http, ct)))
            .WithName("StartDemo")
            .WithSummary("Try the demo: creates a private, temporary sandbox (or resumes this browser's) and signs in as its manager.")
            .Produces<DemoSignInResponse>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireRateLimiting(CreateRateLimitPolicy);

        demo.MapPost("/persona", async (SwitchPersonaRequest request, HttpContext http, DemoSandboxService service, CancellationToken ct) =>
                Respond(http, await service.SwitchAsync(http, request.Persona, ct)))
            .WithName("SwitchDemoPersona")
            .WithSummary("Act as another of this sandbox's predefined people (manager, technician, requester, otherManager).")
            .Produces<DemoSignInResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireRateLimiting(SwitchRateLimitPolicy);

        demo.MapPost("/reset", async (HttpContext http, DemoSandboxService service, CancellationToken ct) =>
                Respond(http, await service.ResetAsync(http, ct)))
            .WithName("ResetDemo")
            .WithSummary("Replace this browser's sandbox with fresh data. Other visitors' sandboxes are never affected.")
            .Produces<DemoSignInResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireRateLimiting(CreateRateLimitPolicy);

        demo.MapPost("/end", async (HttpContext http, DemoSandboxService service, CancellationToken ct) =>
            {
                await service.EndAsync(http, ct);
                return TypedResults.NoContent();
            })
            .WithName("EndDemo")
            .WithSummary("End this browser's demo now: revoke the sandbox and sign out.");

        return api;
    }

    private static IResult Respond(HttpContext http, Result<DemoSignInResponse> result)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (result.Failure is { Code: "demo_capacity_reached" })
        {
            http.Response.Headers.RetryAfter = "60";
        }

        return result.ToOk();
    }
}
