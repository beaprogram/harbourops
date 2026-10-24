using System.Text.Json.Serialization;
using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Auth;
using HarbourOps.Api.Features.Demo;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuration ----------
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
builder.Services.AddOptions<DemoDataOptions>().BindConfiguration(DemoDataOptions.SectionName);

// Reject oversized request bodies early. Our JSON payloads are small text fields.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

// ---------- Persistence ----------
builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured. See README.md for local setup.");
    }

    options.UseNpgsql(connectionString);
});

// Cookie encryption keys are stored in PostgreSQL so sessions survive restarts and work across instances.
builder.Services.AddDataProtection()
    .SetApplicationName("HarbourOps")
    .PersistKeysToDbContext<AppDbContext>();

// An injectable clock: production uses the system clock, tests substitute a fake one.
builder.Services.AddSingleton(TimeProvider.System);

// ---------- Security ----------
builder.Services.AddHarbourAuthentication();
builder.Services.AddCurrentUser();

// ---------- Application services ----------
builder.Services.AddScoped<WorkOrderService>();
builder.Services.AddScoped<DemoDataSeeder>();

// ---------- HTTP plumbing ----------
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

// Request logging records method, path, status, and duration only: never bodies, cookies, or headers.
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath |
                            HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

builder.Services.Configure<StaticFileOptions>(options =>
    options.OnPrepareResponse = context =>
    {
        // Vite fingerprints files in /assets, so they can be cached forever; index.html must always revalidate.
        context.Context.Response.Headers.CacheControl =
            context.Context.Request.Path.StartsWithSegments("/assets")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    });

var app = builder.Build();

await app.InitializeDatabaseAsync();

// ---------- Middleware pipeline (order matters) ----------
app.UseExceptionHandler();          // Unhandled exceptions become 500 Problem Details, without stack traces.
app.UseStatusCodePages();           // Empty 4xx/5xx responses get a Problem Details body.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpLogging();
app.UseDefaultFiles();
app.UseStaticFiles();               // The built React app (wwwroot) in production.

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();
app.UseMiddleware<AntiforgeryMiddleware>();

// ---------- Endpoints ----------
var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapDemoEndpoints();
api.MapPropertyEndpoints();
api.MapTechnicianEndpoints();
api.MapWorkOrderEndpoints();
// Unknown /api routes return a JSON 404 instead of the SPA's index.html.
api.MapFallback(() => Failure.NotFound("No API endpoint matches this route.").ToProblem());

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar
}

// Client-side routes (/work-orders/123, /login, ...) are served the SPA shell.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
