using System.Text.Json.Serialization;
using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Processing;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Auth;
using HarbourOps.Api.Features.Comments;
using HarbourOps.Api.Features.Dashboard;
using HarbourOps.Api.Features.Demo;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Api.Features.WorkOrders.Export;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Configuration ----------
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
builder.Services.AddOptions<DemoDataOptions>().BindConfiguration(DemoDataOptions.SectionName);
builder.Services.AddOptions<ExportOptions>().BindConfiguration(ExportOptions.SectionName);
builder.Services.AddOptions<AttachmentOptions>()
    .BindConfiguration(AttachmentOptions.SectionName)
    .Validate(o => !o.Validate().Any(), "Invalid Attachments configuration (see AttachmentOptions.Validate).")
    .ValidateOnStart();
builder.Services.AddOptions<AzureOptions>().BindConfiguration(AzureOptions.SectionName);

// The hosted public demo sets Hosting:RequireSecureConfiguration. Refuse to start rather than run it exposed
// (Key Vault key protection, HTTPS-only cookies, no shared accounts, managed-identity storage, no auto-migrate).
if (builder.Configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>()?.RequireSecureConfiguration == true)
{
    var problems = HostingConfiguration.SecureConfigurationProblems(builder.Configuration).ToList();
    if (problems.Count > 0)
    {
        throw new InvalidOperationException(
            "This configuration is not safe for a hosted deployment:" + string.Concat(problems.Select(p => "\n - " + p)));
    }
}

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
// In Azure, each new key is also wrapped by a Key Vault key (decisions §41).
builder.Services.AddHarbourDataProtection<AppDbContext>(builder.Configuration);

// An injectable clock: production uses the system clock, tests substitute a fake one.
builder.Services.AddSingleton(TimeProvider.System);

// ---------- Security ----------
builder.Services.AddHarbourAuthentication();
builder.Services.AddCurrentUser();

// ---------- Application services ----------
builder.Services.AddScoped<WorkOrderService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<WorkOrderExportService>();
// Photo attachments: a private local directory, or a private Azure Blob container (managed identity).
builder.Services.AddSingleton<IAttachmentStorage>(services =>
{
    var storage = services.GetRequiredService<IOptions<AttachmentOptions>>().Value.Storage;
    return storage.Provider == AttachmentStorageOptions.AzureBlobProvider
        ? AzureBlobAttachmentStorage.Create(storage.AzureBlob, () => services.GetRequiredService<IOptions<AzureOptions>>().Value.CreateCredential())
        : ActivatorUtilities.CreateInstance<LocalFileAttachmentStorage>(services);
});
builder.Services.AddSingleton<PhotoProcessor>();
builder.Services.AddSingleton<UploadAdmission>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<AttachmentReconciler>();
builder.Services.AddHostedService<AttachmentCleanupService>();
builder.Services.AddScoped<DemoDataSeeder>();
// Private, temporary per-visitor demo sandboxes (off unless DemoSandbox:Enabled is true).
builder.Services.AddDemoSandboxes();

// ---------- HTTP plumbing ----------
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
    .AddCheck<DataProtectionHealthCheck>("data-protection", tags: ["ready"])
    .AddCheck<AttachmentStorageHealthCheck>("photo-storage", failureStatus: HealthStatus.Degraded, tags: ["ready"]);

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
api.MapDemoSandboxEndpoints();
api.MapPropertyEndpoints();
api.MapTechnicianEndpoints();
api.MapWorkOrderEndpoints();
api.MapCommentEndpoints();
api.MapAttachmentEndpoints();
api.MapDashboardEndpoints();
// Unknown /api routes return a JSON 404 instead of the SPA's index.html.
api.MapFallback(() => Failure.NotFound("No API endpoint matches this route.").ToProblem());

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// The commit this image was built from (Docker build argument SOURCE_VERSION), so a release can wait for exactly
// the new version instead of the old container that keeps serving until the new one is ready.
app.MapGet("/health/version", (IConfiguration configuration, HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return TypedResults.Ok(new { version = configuration["HARBOUROPS_VERSION"] ?? "unknown" });
});

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
