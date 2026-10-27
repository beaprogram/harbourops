using Testcontainers.PostgreSql;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// One real PostgreSQL container shared by all integration tests. PostgreSQL 18 by default (same image as
/// docker-compose); CI also runs the suite on PostgreSQL 17, the version Supabase hosts, by setting
/// HARBOUROPS_TEST_POSTGRES_IMAGE. Booting the app runs the real migrations and demo seeder against it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public static string Image =>
        Environment.GetEnvironmentVariable("HARBOUROPS_TEST_POSTGRES_IMAGE") is { Length: > 0 } image ? image : "postgres:18.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public HarbourOpsFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Factory = new HarbourOpsFactory(ConnectionString);
        _ = Factory.Server; // Start the app now: applies migrations and seeds demo data.
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL integration";
}
