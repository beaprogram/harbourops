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
    }
}
