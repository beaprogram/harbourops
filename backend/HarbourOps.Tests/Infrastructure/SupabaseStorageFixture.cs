using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using Testcontainers.PostgreSql;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// Supabase's open-source Storage API server in a container (file backend, its own PostgreSQL 17 for metadata),
/// for testing the Supabase Storage adapter locally. Like Azurite for Azure, this is emulator evidence: the hosted
/// platform adds an API gateway (secret keys in the apikey header) and its own limits, which are verified after
/// deployment.
/// </summary>
public sealed class SupabaseStorageFixture : IAsyncLifetime
{
    public const string Image = "supabase/storage-api:v1.81.4";
    public const string DatabaseImage = "postgres:17.11-alpine";

    // Test-only signing secret for the local server's tokens; it protects nothing outside this container.
    private const string JwtSecret = "harbourops-tests-only-storage-jwt-secret-0123456789";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly PostgreSqlContainer _database;
    private readonly IContainer _storage;

    public SupabaseStorageFixture()
    {
        _database = new PostgreSqlBuilder(DatabaseImage)
            .WithNetwork(_network)
            .WithNetworkAliases("storage-db")
            .Build();
        _storage = new ContainerBuilder(Image)
            .WithNetwork(_network)
            .WithPortBinding(5000, assignRandomHostPort: true)
            .WithEnvironment(new Dictionary<string, string>
            {
                ["SERVER_PORT"] = "5000",
                ["SERVER_REGION"] = "local",
                ["AUTH_JWT_SECRET"] = JwtSecret,
                ["AUTH_JWT_ALGORITHM"] = "HS256",
                ["DATABASE_URL"] = "postgresql://postgres:postgres@storage-db:5432/postgres",
                ["DB_INSTALL_ROLES"] = "true",
                ["DB_SUPER_USER"] = "postgres",
                ["STORAGE_BACKEND"] = "file",
                ["STORAGE_FILE_BACKEND_PATH"] = "/tmp/storage",
                ["STORAGE_S3_BUCKET"] = "unused",
                ["TENANT_ID"] = "tests",
                ["PG_QUEUE_ENABLE"] = "false",
                ["IMAGE_TRANSFORMATION_ENABLED"] = "false",
                ["UPLOAD_FILE_SIZE_LIMIT"] = "52428800",
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(5000).ForPath("/status")))
            .Build();
    }

    /// <summary>The server's base URL, standing in for https://&lt;project-ref&gt;.supabase.co/storage/v1.</summary>
    public string StorageUrl => $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(5000)}";

    /// <summary>A service_role token for the local server: what a secret key maps to on the hosted platform.</summary>
    public string ServiceKey { get; } = Token("service_role");

    /// <summary>An anon token: what a browser's publishable key maps to. It must not reach private photos.</summary>
    public string AnonKey { get; } = Token("anon");

    public SupabaseStorageOptions Options(string? bucket = null) => new()
    {
        Url = StorageUrl,
        ServiceKey = ServiceKey,
        Bucket = bucket ?? NewBucketName(),
        CreateBucketIfMissing = true,
    };

    public SupabaseStorageAttachmentStorage NewStorage(string? bucket = null, int listPageSize = 1000) =>
        SupabaseStorageAttachmentStorage.Create(Options(bucket), listPageSize);

    /// <summary>A client for the server's raw API, authenticated with <paramref name="key"/> (or anonymous).</summary>
    public HttpClient RawClient(string? key = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(StorageUrl + "/") };
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    public static string NewBucketName() => "test-" + Guid.NewGuid().ToString("N")[..20];

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        await _database.StartAsync();
        await _storage.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _storage.DisposeAsync();
        await _database.DisposeAsync();
        await _network.DisposeAsync();
    }

    private static string Token(string role)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { role, iss = "supabase", iat = now, exp = now + 24 * 3600 }));
        var signature = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(JwtSecret), Encoding.UTF8.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}
