using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// Hosts the real application in memory against the test PostgreSQL database.
/// Only configuration and (optionally) the clock are changed; all middleware, auth,
/// antiforgery, EF Core, and SQL run exactly as in production.
/// </summary>
public sealed class HarbourOpsFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("DemoData:Enabled", "true");
        builder.UseSetting("DemoData:Password", DemoAccounts.Password);
        builder.UseSetting("Security:LoginPermitLimitPerMinute", "1000");
        // Each app instance gets its own private photo directory; the scheduled cleanup is tested directly instead.
        builder.UseSetting("Attachments:Storage:LocalPath", StoragePath);
        builder.UseSetting("Attachments:Cleanup:Enabled", "false");
        // Demo sandboxes are off unless a test enables them; their cleanup loop is driven by tests directly
        // (an app instance with demo mode off would otherwise end other tests' sandboxes in the shared database).
        builder.UseSetting("DemoSandbox:CleanupEnabled", "false");
        // Keep test output readable: configuration filters override SetMinimumLevel, so set them here.
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Critical");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Error");
        builder.UseSetting("Logging:Console:FormatterName", "simple");
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "harbourops-tests", Guid.NewGuid().ToString("N"));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(StoragePath))
        {
            Directory.Delete(StoragePath, recursive: true);
        }
    }

    /// <summary>A client that keeps cookies like a browser. HTTPS so Secure cookies are sent back.</summary>
    public ApiClient CreateApiClient() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    /// <summary>A browser-like client whose cookies the test can inspect, copy, and replay.</summary>
    public ApiClient CreateApiClient(CookieContainer cookies)
    {
        var client = CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        return new ApiClient(client);
    }

    public async Task<ApiClient> SignInAsync(string email)
    {
        var client = CreateApiClient();
        var response = await client.LoginAsync(email);
        response.EnsureSuccessStatusCode();
        return client;
    }
}
