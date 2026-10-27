using System.Net;
using System.Security.Cryptography;
using HarbourOps.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Cookie-encryption key configuration. Key Vault itself can't be emulated locally, so its tests check the wiring
/// (which encryptor wraps new keys, which name isolates them). Certificate protection, used where Key Vault isn't
/// available, runs for real: keys are encrypted, survive a restart, rotate, and fail closed without the certificate.
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
    public async Task With_a_certificate_new_keys_are_stored_encrypted_and_survive_a_restart()
    {
        // A database of its own: certificate-encrypted keys must not reach the shared test database, whose other
        // app instances don't hold the certificate.
        var database = await NewDatabaseAsync();
        var certificate = TestCertificates.Pkcs12Base64();
        string payload;
        await using (var first = CertificateApp(database, certificate))
        {
            payload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("signed in");
        }

        var stored = await KeyXmlAsync(database);
        Assert.NotEmpty(stored);
        Assert.All(stored, xml =>
        {
            Assert.Contains("<encryptedSecret", xml);
            Assert.DoesNotContain("<value>", xml); // the plaintext key material element of an unprotected key
        });

        await using var restarted = CertificateApp(database, certificate);
        Assert.Equal("signed in", restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(payload));
        using var client = restarted.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_rotated_certificate_still_reads_old_keys_and_without_the_old_one_sessions_start_over()
    {
        var database = await NewDatabaseAsync();
        var oldCertificate = TestCertificates.Pkcs12Base64();
        var newCertificate = TestCertificates.Pkcs12Base64();
        string payload;
        await using (var before = CertificateApp(database, oldCertificate))
        {
            payload = before.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("signed in");
        }

        await using (var rotating = CertificateApp(database, newCertificate, previous: oldCertificate))
        {
            Assert.Equal("signed in", rotating.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(payload));
        }

        // Without the certificate that encrypted the old keys they stay unreadable: existing cookies are rejected
        // (everyone signs in again). Data Protection then creates a new key, encrypted to the current certificate,
        // so the app keeps working and nothing is ever stored in plaintext.
        await using var withoutOld = CertificateApp(database, newCertificate);
        Assert.ThrowsAny<CryptographicException>(() =>
            withoutOld.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(payload));
        using var client = withoutOld.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
        var stored = await KeyXmlAsync(database);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, xml => Assert.Contains("<encryptedSecret", xml));
    }

    [Fact]
    public void An_unusable_certificate_setting_stops_startup_without_echoing_its_value()
    {
        using var app = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?> { ["DataProtection:CertificateBase64"] = "not-a-certificate" });

        var refused = Assert.Throws<InvalidOperationException>(() => app.Server);

        Assert.Contains("DataProtection:CertificateBase64", refused.Message);
        Assert.DoesNotContain("not-a-certificate", refused.Message);
    }

    [Fact]
    public void Key_vault_and_certificate_protection_cannot_both_be_configured()
    {
        using var app = new HarbourOpsFactory(Fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["DataProtection:KeyVaultKeyUri"] = "https://example-vault.vault.azure.net/keys/dataprotection",
            ["DataProtection:CertificateBase64"] = TestCertificates.Pkcs12Base64(),
        });

        Assert.Contains("not both", Assert.Throws<InvalidOperationException>(() => app.Server).Message);
    }

    private static HarbourOpsFactory CertificateApp(string connectionString, string certificate, string? previous = null) =>
        new(connectionString, new Dictionary<string, string?>
        {
            ["DataProtection:CertificateBase64"] = certificate,
            ["DataProtection:PreviousCertificateBase64"] = previous,
        });

    private async Task<string> NewDatabaseAsync()
    {
        var name = "dp_" + Guid.NewGuid().ToString("N")[..16];
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new NpgsqlConnectionStringBuilder(Fixture.ConnectionString) { Database = name }.ConnectionString;
    }

    private static async Task<List<string>> KeyXmlAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var query = new NpgsqlCommand("""SELECT "Xml" FROM "DataProtectionKeys" """, connection);
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var keys = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
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
