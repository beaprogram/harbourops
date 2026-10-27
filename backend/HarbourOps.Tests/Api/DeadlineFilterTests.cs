using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Overdue and due-soon filters at their exact boundaries, one microsecond either side
/// (PostgreSQL's timestamp precision). A Normal request created at T is due at T + 72 h.
/// </summary>
public sealed class DeadlineFilterTests(PostgresFixture fixture) : FakeClockIntegrationTest(fixture)
{
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    [Fact]
    public async Task Overdue_and_due_soon_boundaries_are_exact()
    {
        var tag = UniqueTag();
        var createdAt = Clock.GetUtcNow();
        using (var riley = await SignInClockedAsync(DemoAccounts.RileyRequester))
        {
            await CreateRequestAsync(riley, title: $"Boundary {tag}");
        }

        // Due in 24 h + 1 µs: just outside the due-soon window.
        await AssertDeadlineState(tag, createdAt.AddHours(48) - Microsecond, overdue: false, dueSoon: false);
        // Due in exactly 24 h: the window's far edge is inclusive.
        await AssertDeadlineState(tag, createdAt.AddHours(48), overdue: false, dueSoon: true);
        // Due exactly now: still due soon, not yet overdue.
        await AssertDeadlineState(tag, createdAt.AddHours(72), overdue: false, dueSoon: true);
        // One microsecond after the due time: overdue.
        await AssertDeadlineState(tag, createdAt.AddHours(72) + Microsecond, overdue: true, dueSoon: false);
    }

    [Fact]
    public async Task Resolved_work_is_never_overdue_or_due_soon()
    {
        var tag = UniqueTag();
        var createdAt = Clock.GetUtcNow();
        using (var riley = await SignInClockedAsync(DemoAccounts.RileyRequester))
        using (var morgan = await SignInClockedAsync(DemoAccounts.MorganManager))
        using (var sam = await SignInClockedAsync(DemoAccounts.SamTechnician))
        {
            var workOrder = await CreateRequestAsync(riley, title: $"Resolved {tag}");
            workOrder = await StartAsync(sam, await AssignAsync(morgan, workOrder, DemoAccounts.SamId));
            await ResolveAsync(sam, workOrder);
        }

        await AssertDeadlineState(tag, createdAt.AddHours(72), overdue: false, dueSoon: false);
        await AssertDeadlineState(tag, createdAt.AddHours(100), overdue: false, dueSoon: false);
    }

    private async Task AssertDeadlineState(string tag, DateTimeOffset now, bool overdue, bool dueSoon)
    {
        Clock.SetUtcNow(now);
        using var morgan = await SignInClockedAsync(DemoAccounts.MorganManager);

        var item = Assert.Single((await ListAsync(morgan, $"q={tag}")).Items);
        var overdueFilter = await ListAsync(morgan, $"q={tag}&deadline=overdue");
        var dueSoonFilter = await ListAsync(morgan, $"q={tag}&deadline=dueSoon");

        // The flag on the row and the database filter must agree at every boundary.
        Assert.Equal(overdue, item.IsOverdue);
        Assert.Equal(dueSoon, item.IsDueSoon);
        Assert.Equal(overdue ? 1 : 0, overdueFilter.TotalCount);
        Assert.Equal(dueSoon ? 1 : 0, dueSoonFilter.TotalCount);
    }
}
