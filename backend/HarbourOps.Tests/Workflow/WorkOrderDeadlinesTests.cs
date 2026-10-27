using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Tests.Workflow;

public sealed class WorkOrderDeadlinesTests
{
    private static readonly DateTimeOffset Created = new(2026, 3, 7, 22, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(WorkOrderPriority.High, 24)]
    [InlineData(WorkOrderPriority.Normal, 72)]
    public void Due_date_is_elapsed_hours_from_creation(WorkOrderPriority priority, int hours)
    {
        Assert.Equal(Created.AddHours(hours), WorkOrderDeadlines.CalculateDueAt(Created, priority));
    }

    [Theory]
    [InlineData(WorkOrderStatus.New, true)]
    [InlineData(WorkOrderStatus.Assigned, true)]
    [InlineData(WorkOrderStatus.InProgress, true)]
    [InlineData(WorkOrderStatus.Resolved, false)]
    [InlineData(WorkOrderStatus.Closed, false)]
    public void Only_open_work_can_be_overdue(WorkOrderStatus status, bool expectedOverdue)
    {
        var dueAt = Created.AddHours(24);

        Assert.Equal(expectedOverdue, WorkOrderDeadlines.IsOverdue(status, dueAt, dueAt.AddSeconds(1)));
    }

    [Fact]
    public void Work_is_not_overdue_until_after_the_due_time()
    {
        var dueAt = Created.AddHours(24);

        Assert.False(WorkOrderDeadlines.IsOverdue(WorkOrderStatus.New, dueAt, dueAt));
        Assert.False(WorkOrderDeadlines.IsOverdue(WorkOrderStatus.New, dueAt, dueAt.AddTicks(-1)));
    }

    [Theory]
    [InlineData(-1, false)]          // due 1 tick before now: overdue, so not "due soon"
    [InlineData(0, true)]            // due exactly now
    [InlineData(24 * 3600 * 1000L * 10_000, true)]       // due in exactly 24 h (inclusive)
    [InlineData(24 * 3600 * 1000L * 10_000 + 1, false)]  // due 1 tick after the window
    public void Due_soon_window_is_inclusive_at_both_ends(long ticksUntilDue, bool expectedDueSoon)
    {
        var now = Created.AddHours(10);

        Assert.Equal(expectedDueSoon, WorkOrderDeadlines.IsDueSoon(WorkOrderStatus.Assigned, now.AddTicks(ticksUntilDue), now));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Resolved)]
    [InlineData(WorkOrderStatus.Closed)]
    public void Finished_work_is_never_due_soon(WorkOrderStatus status)
    {
        Assert.False(WorkOrderDeadlines.IsDueSoon(status, Created.AddHours(1), Created));
    }
}
