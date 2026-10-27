using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace HarbourOps.Tests.Workflow;

/// <summary>The guard that stops a hosted public deployment from starting with an unsafe configuration.</summary>
public sealed class HostingConfigurationTests
{
    private const string VerifiedConnection = "Host=db.example;Database=harbourops;Username=harbourops_app;Password=unused;SSL Mode=VerifyFull";

    private static readonly Dictionary<string, string?> Secure = new()
    {
        ["DataProtection:KeyVaultKeyUri"] = "https://example-vault.vault.azure.net/keys/dataprotection",
        ["ConnectionStrings:Default"] = VerifiedConnection,
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
    [InlineData("ConnectionStrings:Default", "Host=db.example;Database=harbourops;SSL Mode=Require", "VerifyFull")]
    [InlineData("ConnectionStrings:Default", "Host=db.example;Database=harbourops", "VerifyFull")]
    [InlineData("ConnectionStrings:Default", "not a connection string", "VerifyFull")]
    public void Each_unsafe_setting_is_reported(string key, string value, string expected) =>
        Assert.Contains(ProblemsWith((key, value)), problem => problem.Contains(expected, StringComparison.Ordinal));

    // The free Render + Supabase deployment: no Key Vault or managed identity, so a certificate protects the key
    // ring and a server-side secret key reaches the private bucket.
    private static readonly (string, string?)[] RenderAndSupabase =
    [
        ("DataProtection:KeyVaultKeyUri", null),
        ("DataProtection:CertificateBase64", TestCertificates.Pkcs12Base64()),
        ("Attachments:Storage:Provider", "Supabase"),
        ("Attachments:Storage:Supabase:Url", "https://exampleprojectref.supabase.co/storage/v1"),
        ("Attachments:Storage:Supabase:ServiceKey", "sb_secret_example"),
        ("Attachments:Storage:Supabase:Bucket", "demo-photos"),
    ];

    [Fact]
    public void A_render_and_supabase_configuration_with_certificate_protection_has_no_problems() =>
        Assert.Empty(ProblemsWith(RenderAndSupabase));

    [Theory]
    [InlineData("DataProtection:CertificateBase64", "", "CertificateBase64")]
    [InlineData("DataProtection:CertificateBase64", "bm90IGEgY2VydGlmaWNhdGU=", "CertificateBase64")]
    [InlineData("Attachments:Storage:Supabase:Url", "http://exampleprojectref.supabase.co/storage/v1", "Supabase")]
    [InlineData("Attachments:Storage:Supabase:ServiceKey", "", "Supabase")]
    [InlineData("Attachments:Storage:Supabase:CreateBucketIfMissing", "true", "created private in advance")]
    public void Each_unsafe_render_and_supabase_setting_is_reported(string key, string value, string expected) =>
        Assert.Contains(ProblemsWith([.. RenderAndSupabase, (key, value)]), problem => problem.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void A_certificate_without_its_private_key_does_not_count_as_protection()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=public only", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var publicOnly = Convert.ToBase64String(withKey.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));

        Assert.Contains(ProblemsWith([.. RenderAndSupabase, ("DataProtection:CertificateBase64", publicOnly)]),
            problem => problem.Contains("CertificateBase64", StringComparison.Ordinal));
    }

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
                // The Key Vault secret's value (infra/main.bicep), resolved by App Service before the app starts.
                ["ConnectionStrings:Default"] = VerifiedConnection,
            })
            .Build();

        Assert.True(configuration.GetValue<bool>("Hosting:RequireSecureConfiguration"));
        Assert.True(configuration.GetValue<bool>("DemoSandbox:Enabled"));
        Assert.Empty(HostingConfiguration.SecureConfigurationProblems(configuration));
    }

    [Fact]
    public void The_committed_render_blueprint_is_secure_once_its_secrets_are_entered()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        var api = Path.Combine(root, "backend", "HarbourOps.Api");
        var blueprint = RenderBlueprintValues(Path.Combine(root, "render.yaml"));
        var secrets = blueprint.Where(entry => entry.Value is null).Select(entry => entry.Key).Order(StringComparer.Ordinal);
        Assert.Equal(
            ["Attachments__Storage__Supabase__ServiceKey", "Attachments__Storage__Supabase__Url", "ConnectionStrings__Default", "DataProtection__CertificateBase64"],
            secrets);
        Assert.Equal("PublicDemo", blueprint["ASPNETCORE_ENVIRONMENT"]);

        var settings = blueprint.Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key.Replace("__", ":", StringComparison.Ordinal), entry => entry.Value);
        // What the operator enters in Render for the four "sync: false" values: the right shape, no real secrets.
        settings["ConnectionStrings:Default"] = VerifiedConnection;
        settings["DataProtection:CertificateBase64"] = TestCertificates.Pkcs12Base64();
        settings["Attachments:Storage:Supabase:ServiceKey"] = "sb_secret_example";
        settings["Attachments:Storage:Supabase:Url"] = "https://exampleprojectref.supabase.co/storage/v1";
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(api, "appsettings.json"))
            .AddJsonFile(Path.Combine(api, "appsettings.PublicDemo.json"))
            .AddInMemoryCollection(settings)
            .Build();

        Assert.True(configuration.GetValue<bool>("Hosting:RequireSecureConfiguration"));
        Assert.Empty(HostingConfiguration.SecureConfigurationProblems(configuration));
        var attachments = configuration.GetSection(AttachmentOptions.SectionName).Get<AttachmentOptions>()!;
        Assert.Empty(attachments.Validate());
        // The limits measured at Render Free's 0.1 CPU and 512 MiB (docs/benchmarks/photo-memory).
        Assert.Equal((24_000_000L, 1, 1), (attachments.MaxPixels, attachments.MaxConcurrentProcessing, attachments.MaxQueuedUploads));
        Assert.Equal(20, configuration.GetValue<int>("DemoSandbox:MaxActiveSandboxes"));
    }

    /// <summary>The envVars of render.yaml: a value, or null for "sync: false" (entered in Render, never committed).</summary>
    private static Dictionary<string, string?> RenderBlueprintValues(string path)
    {
        var values = new Dictionary<string, string?>();
        string? key = null;
        foreach (var line in File.ReadLines(path).Select(line => line.Trim()))
        {
            if (line.StartsWith("- key: ", StringComparison.Ordinal))
            {
                key = line["- key: ".Length..].Trim();
            }
            else if (key is not null && line.StartsWith("value: ", StringComparison.Ordinal))
            {
                values[key] = line["value: ".Length..].Split(" #")[0].Trim().Trim('"');
                key = null;
            }
            else if (key is not null && line.StartsWith("sync: false", StringComparison.Ordinal))
            {
                values[key] = null;
                key = null;
            }
        }

        return values;
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
