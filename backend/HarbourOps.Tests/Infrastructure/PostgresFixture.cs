using Testcontainers.PostgreSql;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// One real PostgreSQL 18 container shared by all integration tests (same image as docker-compose).
/// Booting the app runs the real migrations and demo seeder against it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine").Build();

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
