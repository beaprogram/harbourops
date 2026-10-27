using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Api;

/// <summary>
/// The app reads time only through the injected <see cref="TimeProvider"/>, so deadlines can be
/// tested deterministically by swapping in a fake clock and moving it forward. The base class starts
/// the clock on a whole second: PostgreSQL keeps microseconds, and on Linux the real clock has
/// sub-microsecond ticks that a stored timestamp would not round-trip.
/// </summary>
public sealed class DeadlineApiTests(PostgresFixture fixture) : FakeClockIntegrationTest(fixture)
{
    [Fact]
    public async Task Due_dates_follow_priority_from_creation_time_and_overdue_uses_the_clock()
    {
        using var riley = await SignInClockedAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInClockedAsync(DemoAccounts.MorganManager);
        var createdAt = Clock.GetUtcNow();

        var workOrder = await CreateRequestAsync(riley);
        Assert.Equal(createdAt, workOrder.CreatedAt);
        Assert.Equal(createdAt.AddHours(72), workOrder.DueAt);

        // Changing priority later still measures from the original creation time.
        Clock.Advance(TimeSpan.FromHours(5));
        workOrder = await morgan.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/priority", new { priority = "High", version = workOrder.Version });
        Assert.Equal(createdAt.AddHours(24), workOrder.DueAt);
        Assert.False(workOrder.IsOverdue);

        // 25 hours after creation the High-priority request is overdue. (Sessions last 8 hours, so sign in again.)
        Clock.Advance(TimeSpan.FromHours(20));
        using var morganLater = await SignInClockedAsync(DemoAccounts.MorganManager);
        var later = await morganLater.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        Assert.True(later.IsOverdue);
    }
}
