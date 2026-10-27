using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using HarbourOps.Api.Features.Attachments;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HarbourOps.Api.Common;

/// <summary>Bound from the "Azure" section: how the app authenticates to Azure services (no secrets here).</summary>
public sealed class AzureOptions
{
    public const string SectionName = "Azure";

    /// <summary>Client ID of a user-assigned managed identity. Empty: the app's system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Developer machines only: use DefaultAzureCredential (Azure CLI, Visual Studio, environment variables)
    /// instead of a managed identity. Refused by the secure hosted configuration.
    /// </summary>
    public bool UseDeveloperCredential { get; set; }

    public TokenCredential CreateCredential() =>
        UseDeveloperCredential
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(string.IsNullOrWhiteSpace(ManagedIdentityClientId)
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(ManagedIdentityClientId));
}

/// <summary>Bound from the "DataProtection" section (cookie-encryption keys; see decisions §41).</summary>
public sealed class KeyProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Isolates this app's keys and payloads. It must stay the same for the life of a deployment: changing it
    /// makes every existing cookie unreadable (everyone is signed out once). Give each environment its own value.
    /// </summary>
    public string ApplicationName { get; set; } = "HarbourOps";

    /// <summary>
    /// Key Vault key that wraps (encrypts) new keys in the key ring, for example
    /// https://&lt;vault&gt;.vault.azure.net/keys/dataprotection. Empty: keys are stored unencrypted (local development).
    /// </summary>
    public string? KeyVaultKeyUri { get; set; }

    /// <summary>
    /// For hosts without Key Vault (the free Render deployment): a base64 PKCS#12 certificate with its private key.
    /// New keys are encrypted to it before they are stored, so the database alone never reveals them. The host
    /// supplies it as a secret environment variable. Use either this or <see cref="KeyVaultKeyUri"/>, not both.
    /// </summary>
    public string? CertificateBase64 { get; set; }

    /// <summary>The previous certificate during a rotation: still accepted for decrypting older keys, never for new ones.</summary>
    public string? PreviousCertificateBase64 { get; set; }
}

/// <summary>Bound from the "Hosting" section.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    /// <summary>Set by the hosted public demo. Startup then refuses any configuration that isn't safe to expose.</summary>
    public bool RequireSecureConfiguration { get; set; }
}

public static class HostingConfiguration
{
    /// <summary>
    /// What would make this configuration unsafe for a public, hosted deployment. Checked at startup when
    /// Hosting:RequireSecureConfiguration is true; the app refuses to start with any problem listed.
    /// </summary>
    public static IEnumerable<string> SecureConfigurationProblems(IConfiguration configuration)
    {
        var keys = configuration.GetSection(KeyProtectionOptions.SectionName).Get<KeyProtectionOptions>() ?? new();
        var keyVault = Uri.TryCreate(keys.KeyVaultKeyUri, UriKind.Absolute, out var keyUri) && keyUri.Scheme == Uri.UriSchemeHttps;
        if (!keyVault && !KeyProtectionCertificates.IsUsable(keys.CertificateBase64))
            yield return "DataProtection needs KeyVaultKeyUri (the https URL of a Key Vault key) or CertificateBase64 (a PKCS#12 " +
                         "certificate with its private key), so cookie-encryption keys are encrypted at rest.";

        if (!RequiresVerifiedTls(configuration.GetConnectionString("Default")))
            yield return "ConnectionStrings:Default must use SSL Mode=VerifyFull, so the database connection is encrypted and the server is verified.";

        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new();
        if (security.CookieSecurePolicy != CookieSecurePolicy.Always)
            yield return "Security:CookieSecurePolicy must be Always (cookies only over HTTPS).";

        if (configuration.GetValue<bool>("DemoData:Enabled"))
            yield return "DemoData:Enabled must be false: shared demo accounts with a published password must not exist on a public host.";

        if (configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
            yield return "Database:ApplyMigrationsOnStartup must be false: migrations run as a separate, controlled release step.";

        var storage = configuration.GetSection("Attachments:Storage").Get<AttachmentStorageOptions>() ?? new();
        var privateStorage = storage.Provider switch
        {
            AttachmentStorageOptions.AzureBlobProvider =>
                !string.IsNullOrWhiteSpace(storage.AzureBlob.ServiceUri) && string.IsNullOrWhiteSpace(storage.AzureBlob.ConnectionString),
            AttachmentStorageOptions.SupabaseProvider =>
                Uri.TryCreate(storage.Supabase.Url, UriKind.Absolute, out var supabaseUri) && supabaseUri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrWhiteSpace(storage.Supabase.ServiceKey) && !storage.Supabase.CreateBucketIfMissing,
            _ => false,
        };
        if (!privateStorage)
            yield return "Attachments:Storage must use AzureBlob with a ServiceUri (managed identity) and no connection string, " +
                         "or Supabase with an https Url, a secret ServiceKey, and a bucket created private in advance.";

        if (configuration.GetValue<bool>("Azure:UseDeveloperCredential"))
            yield return "Azure:UseDeveloperCredential must be false: hosted environments authenticate with a managed identity.";
    }

    private static bool RequiresVerifiedTls(string? connectionString)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(connectionString)
                   && new NpgsqlConnectionStringBuilder(connectionString).SslMode == SslMode.VerifyFull;
        }
        catch (ArgumentException)
        {
            return false; // unparseable: reported as a problem, without echoing the string (it holds a password)
        }
    }

    /// <summary>
    /// Persistent keys in PostgreSQL (unchanged since milestone 1), a stable application name, and, when
    /// configured, every new key wrapped by a Key Vault key through the app's managed identity, or encrypted to
    /// a certificate supplied by the host.
    /// </summary>
    public static IServiceCollection AddHarbourDataProtection<TContext>(this IServiceCollection services, IConfiguration configuration)
        where TContext : Microsoft.EntityFrameworkCore.DbContext, Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext
    {
        var options = configuration.GetSection(KeyProtectionOptions.SectionName).Get<KeyProtectionOptions>() ?? new();
        var builder = services.AddDataProtection()
            .SetApplicationName(options.ApplicationName)
            .PersistKeysToDbContext<TContext>();
        var keyVault = !string.IsNullOrWhiteSpace(options.KeyVaultKeyUri);
        var certificate = !string.IsNullOrWhiteSpace(options.CertificateBase64);
        if (keyVault && certificate)
        {
            throw new InvalidOperationException("Configure DataProtection:KeyVaultKeyUri or DataProtection:CertificateBase64, not both.");
        }

        if (keyVault)
        {
            var azure = configuration.GetSection(AzureOptions.SectionName).Get<AzureOptions>() ?? new();
            builder.ProtectKeysWithAzureKeyVault(new Uri(options.KeyVaultKeyUri!), azure.CreateCredential());
        }
        else if (certificate)
        {
            var current = KeyProtectionCertificates.Load(options.CertificateBase64!, "DataProtection:CertificateBase64");
            var decryptors = string.IsNullOrWhiteSpace(options.PreviousCertificateBase64)
                ? [current]
                : new[] { current, KeyProtectionCertificates.Load(options.PreviousCertificateBase64, "DataProtection:PreviousCertificateBase64") };
            builder.ProtectKeysWithCertificate(current).UnprotectKeysWithAnyCertificate(decryptors);
        }

        return services;
    }
}

/// <summary>Loads the certificates that protect the Data Protection key ring when Key Vault isn't available.</summary>
public static class KeyProtectionCertificates
{
    /// <summary>Loads a base64 PKCS#12 certificate that has an RSA private key of at least 2048 bits.</summary>
    public static X509Certificate2 Load(string base64, string settingName)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(base64.Trim()), password: null);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // The setting's name only: the value is key material.
            throw new InvalidOperationException($"{settingName} is not a base64 PKCS#12 certificate without a password.", exception);
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{settingName} must include an RSA private key of at least 2048 bits.");
        }

        return certificate;
    }

    public static bool IsUsable(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        try
        {
            using var certificate = Load(base64, "certificate");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Ready only if cookies can be protected and unprotected. With Key Vault wrapping, this fails when the app
/// can't unwrap its keys (vault unreachable, permission removed), so the host stops sending it traffic instead
/// of signing everyone out. The key ring is cached, so this does not call Key Vault on every probe.
/// </summary>
public sealed class DataProtectionHealthCheck(IDataProtectionProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var protector = provider.CreateProtector("HarbourOps.HealthCheck");
            var ok = protector.Unprotect(protector.Protect("ready")) == "ready";
            return Task.FromResult(ok ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Data Protection round trip failed."));
        }
        catch (Exception exception)
        {
            // The exception type is enough; key material and vault responses are never logged.
            return Task.FromResult(HealthCheckResult.Unhealthy($"Data Protection unavailable ({exception.GetType().Name})."));
        }
    }
}
