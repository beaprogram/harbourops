using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HarbourOps.Tests.Api;

/// <summary>
/// The app reads time only through the injected <see cref="TimeProvider"/>, so deadlines can be
/// tested deterministically by swapping in a fake clock and moving it forward.
/// </summary>
public sealed class DeadlineApiTests(PostgresFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    // Start at the real current time so cookie and key lifetimes behave normally.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private HarbourOpsFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HarbourOpsFactory(Fixture.ConnectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(_clock));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Due_dates_follow_priority_from_creation_time_and_overdue_uses_the_clock()
    {
        using var riley = await _factory.SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await _factory.SignInAsync(DemoAccounts.MorganManager);
        var createdAt = _clock.GetUtcNow();

        var workOrder = await CreateRequestAsync(riley);
        Assert.Equal(createdAt, workOrder.CreatedAt);
        Assert.Equal(createdAt.AddHours(72), workOrder.DueAt);

        // Changing priority later still measures from the original creation time.
        _clock.Advance(TimeSpan.FromHours(5));
        workOrder = await morgan.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/priority", new { priority = "High", version = workOrder.Version });
        Assert.Equal(createdAt.AddHours(24), workOrder.DueAt);
        Assert.False(workOrder.IsOverdue);

        // 25 hours after creation the High-priority request is overdue. (Sessions last 8 hours, so sign in again.)
        _clock.Advance(TimeSpan.FromHours(20));
        using var morganLater = await _factory.SignInAsync(DemoAccounts.MorganManager);
        var later = await morganLater.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        Assert.True(later.IsOverdue);
    }
}
