using HarbourOps.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Cookie-encryption key configuration. Key Vault itself can't be emulated locally, so these tests check the
/// wiring (which encryptor wraps new keys, which name isolates them); real Key Vault access is verified in Azure.
/// </summary>
public sealed class DataProtectionTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task New_keys_are_wrapped_by_the_configured_key_vault_key_and_keys_stay_in_postgresql()
    {
        await using var app = new HarbourOpsFactory(Fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["DataProtection:KeyVaultKeyUri"] = "https://example-vault.vault.azure.net/keys/dataprotection",
            ["Azure:ManagedIdentityClientId"] = "00000000-0000-0000-0000-000000000000",
        });

        var keyManagement = app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        Assert.Equal("AzureKeyVaultXmlEncryptor", keyManagement.XmlEncryptor?.GetType().Name);
        Assert.Equal("EntityFrameworkCoreXmlRepository`1", keyManagement.XmlRepository?.GetType().Name);
    }

    [Fact]
    public async Task Local_development_keeps_the_existing_key_ring_and_application_name()
    {
        // Without Key Vault configured nothing changes from milestone 1: same repository, same name, so existing
        // cookies stay readable.
        var keyManagement = Factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var dataProtection = Factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

        Assert.Null(keyManagement.XmlEncryptor);
        Assert.Equal("HarbourOps", dataProtection.ApplicationDiscriminator);
        await Task.CompletedTask;
    }
}
