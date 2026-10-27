using HarbourOps.Api.Common;
using HarbourOps.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace HarbourOps.Tests.Workflow;

/// <summary>The guard that stops a hosted public deployment from starting with an unsafe configuration.</summary>
public sealed class HostingConfigurationTests
{
    private static readonly Dictionary<string, string?> Secure = new()
    {
        ["DataProtection:KeyVaultKeyUri"] = "https://example-vault.vault.azure.net/keys/dataprotection",
        ["Security:CookieSecurePolicy"] = "Always",
        ["DemoData:Enabled"] = "false",
        ["Database:ApplyMigrationsOnStartup"] = "false",
        ["Attachments:Storage:Provider"] = "AzureBlob",
        ["Attachments:Storage:AzureBlob:ServiceUri"] = "https://examplestorage.blob.core.windows.net",
    };

    private static IEnumerable<string> ProblemsWith(params (string Key, string? Value)[] changes)
    {
        var settings = new Dictionary<string, string?>(Secure);
        foreach (var (key, value) in changes)
        {
            settings[key] = value;
        }

        return HostingConfiguration.SecureConfigurationProblems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    [Fact]
    public void A_complete_hosted_configuration_has_no_problems() => Assert.Empty(ProblemsWith());

    [Theory]
    [InlineData("DataProtection:KeyVaultKeyUri", "", "KeyVaultKeyUri")]
    [InlineData("DataProtection:KeyVaultKeyUri", "http://example-vault.vault.azure.net/keys/dp", "KeyVaultKeyUri")]
    [InlineData("Security:CookieSecurePolicy", "SameAsRequest", "CookieSecurePolicy")]
    [InlineData("DemoData:Enabled", "true", "DemoData:Enabled")]
    [InlineData("Database:ApplyMigrationsOnStartup", "true", "ApplyMigrationsOnStartup")]
    [InlineData("Attachments:Storage:Provider", "Local", "AzureBlob")]
    [InlineData("Attachments:Storage:AzureBlob:ConnectionString", "UseDevelopmentStorage=true", "no connection string")]
    [InlineData("Azure:UseDeveloperCredential", "true", "UseDeveloperCredential")]
    public void Each_unsafe_setting_is_reported(string key, string value, string expected) =>
        Assert.Contains(ProblemsWith((key, value)), problem => problem.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void The_committed_public_demo_configuration_is_secure_once_azure_supplies_its_endpoints()
    {
        var api = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HarbourOps.Api");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(api, "appsettings.json"))
            .AddJsonFile(Path.Combine(api, "appsettings.PublicDemo.json"))
            // What infra/main.bicep sets as App Service settings (no secrets: the connection string is a Key Vault reference).
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeyVaultKeyUri"] = "https://example-kv.vault.azure.net/keys/dataprotection",
                ["Attachments:Storage:AzureBlob:ServiceUri"] = "https://examplestorage.blob.core.windows.net/",
            })
            .Build();

        Assert.True(configuration.GetValue<bool>("Hosting:RequireSecureConfiguration"));
        Assert.True(configuration.GetValue<bool>("DemoSandbox:Enabled"));
        Assert.Empty(HostingConfiguration.SecureConfigurationProblems(configuration));
    }

    [Fact]
    public void The_app_refuses_to_start_when_a_hosted_configuration_is_unsafe()
    {
        // The usual test settings (shared demo accounts, local storage, no Key Vault) are exactly what a public host must not run with.
        using var app = new HarbourOpsFactory("Host=localhost;Database=never_used",
            new Dictionary<string, string?> { ["Hosting:RequireSecureConfiguration"] = "true" });

        var refused = Assert.Throws<InvalidOperationException>(() => app.Server);

        Assert.Contains("not safe for a hosted deployment", refused.Message);
        Assert.Contains("KeyVaultKeyUri", refused.Message);
        Assert.Contains("DemoData:Enabled", refused.Message);
    }
}
