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
}
