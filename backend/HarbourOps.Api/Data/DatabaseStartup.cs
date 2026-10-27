using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Data;

public static class DatabaseStartup
{
    /// <summary>
    /// Optionally applies migrations and seeds demo data, depending on explicit configuration.
    /// Both are off by default, so an ordinary production deployment does neither.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HarbourOps.Database");

        if (services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ApplyMigrationsOnStartup)
        {
            logger.LogInformation("Applying database migrations");
            await services.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        if (services.GetRequiredService<IOptions<DemoDataOptions>>().Value.Enabled)
        {
            if (app.Environment.IsProduction())
            {
                logger.LogWarning(
                    "DemoData:Enabled is true in Production. Publicly documented demo accounts will exist. " +
                    "Only use this for an isolated demo deployment.");
            }

            await services.GetRequiredService<DemoDataSeeder>().SeedAsync();
        }

        await ReportKeyRingAsync(services, logger);
    }

    /// <summary>
    /// Logs how many cookie-encryption keys exist and how many are stored without encryption (never the keys).
    /// Enabling Key Vault protection only wraps keys created from then on, so older keys stay readable, and
    /// existing sessions keep working, until an operator retires them (docs/deployment.md, "Data Protection keys").
    /// </summary>
    private static async Task ReportKeyRingAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            var keys = await services.GetRequiredService<AppDbContext>().DataProtectionKeys.AsNoTracking()
                .Select(k => k.Xml)
                .ToListAsync();
            var unencrypted = keys.Count(xml => xml is not null && !xml.Contains("<encryptedSecret", StringComparison.Ordinal));
            var keyVault = !string.IsNullOrWhiteSpace(
                services.GetRequiredService<IConfiguration>()[$"{KeyProtectionOptions.SectionName}:KeyVaultKeyUri"]);
            if (keyVault && unencrypted > 0)
            {
                logger.LogWarning(
                    "Data Protection key ring: {KeyCount} keys, {UnencryptedCount} created before Key Vault protection and stored unencrypted. " +
                    "They are kept so existing sessions stay valid; retire them as described in docs/deployment.md once they have expired.",
                    keys.Count, unencrypted);
            }
            else
            {
                logger.LogInformation("Data Protection key ring: {KeyCount} keys, {UnencryptedCount} stored unencrypted", keys.Count, unencrypted);
            }
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            // Database not reachable or not migrated yet: only this report is skipped; /health/ready reports the cause.
            logger.LogInformation("Data Protection key ring not readable yet ({Reason})", exception.GetType().Name);
        }
    }
}
