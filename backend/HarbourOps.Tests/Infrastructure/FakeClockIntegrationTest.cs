using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// Integration tests that control time. The app reads time only through the injected
/// <see cref="TimeProvider"/>, so swapping in a <see cref="FakeTimeProvider"/> makes deadline
/// boundaries and timestamp ties deterministic.
/// </summary>
public abstract class FakeClockIntegrationTest(PostgresFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    /// <summary>
    /// Starts at the current time (so cookie and key lifetimes behave normally) rounded down to a
    /// whole second, so microsecond steps map exactly onto PostgreSQL's microsecond timestamps.
    /// </summary>
    protected FakeTimeProvider Clock { get; } = new(WholeSecond(DateTimeOffset.UtcNow));

    protected HarbourOpsFactory ClockedFactory { get; private set; } = null!;

    public virtual ValueTask InitializeAsync()
    {
        ClockedFactory = new HarbourOpsFactory(Fixture.ConnectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(Clock));
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask DisposeAsync() => ClockedFactory.DisposeAsync();

    /// <summary>Signs in through the fake-clock app. Sessions last 8 hours, so sign in again after big clock jumps.</summary>
    protected Task<ApiClient> SignInClockedAsync(string email) => ClockedFactory.SignInAsync(email);

    private static DateTimeOffset WholeSecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}
