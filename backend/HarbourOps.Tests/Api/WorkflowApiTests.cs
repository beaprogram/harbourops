using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

public sealed class WorkflowApiTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Full_journey_from_request_to_closure_records_the_history()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var workOrder = await CreateRequestAsync(riley, title: "Bathroom tap dripping");
        Assert.Equal(WorkOrderStatus.New, workOrder.Status);
        Assert.Equal(WorkOrderPriority.Normal, workOrder.Priority);
        Assert.Equal(DemoAccounts.RileyId, workOrder.Requester.Id);

        workOrder = await morgan.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/priority", new { priority = "High", version = workOrder.Version });
        workOrder = await AssignAsync(morgan, workOrder, DemoAccounts.SamId);
        Assert.Equal(WorkOrderStatus.Assigned, workOrder.Status);

        workOrder = await StartAsync(sam, workOrder);
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);

        workOrder = await ResolveAsync(sam, workOrder, "Replaced cartridge.");
        Assert.Equal(WorkOrderStatus.Resolved, workOrder.Status);
        Assert.Equal("Replaced cartridge.", workOrder.ResolutionNote);

        workOrder = await riley.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/reopen", new { reason = "Still dripping overnight.", version = workOrder.Version });
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Equal(DemoAccounts.SamId, workOrder.AssignedTechnician!.Id); // technician preserved

        workOrder = await ResolveAsync(sam, workOrder, "Replaced the valve seat as well.");
        workOrder = await morgan.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/close", new { version = workOrder.Version });
        Assert.Equal(WorkOrderStatus.Closed, workOrder.Status);
        Assert.NotNull(workOrder.ClosedAt);
        Assert.Empty(workOrder.AllowedActions);

        var activity = await riley.GetJsonAsync<ActivityEventResponse[]>($"/api/work-orders/{workOrder.Id}/activity");
        Assert.Equal(
            [
                AuditEventType.Created, AuditEventType.PriorityChanged, AuditEventType.Assigned, AuditEventType.Started,
                AuditEventType.Resolved, AuditEventType.Reopened, AuditEventType.Resolved, AuditEventType.Closed,
            ],
            activity.Select(a => a.Type));
        Assert.Equal("Still dripping overnight.", activity.Single(a => a.Type == AuditEventType.Reopened).Note);
    }

    [Fact]
    public async Task Closed_work_orders_are_immutable()
    {
        var closed = await CreateInStatusAsync(WorkOrderStatus.Closed);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var priority = await morgan.PostAsync($"/api/work-orders/{closed.Id}/priority",
            new { priority = "High", version = closed.Version });
        var assign = await morgan.PostAsync($"/api/work-orders/{closed.Id}/assign",
            new { technicianId = DemoAccounts.JordanId, version = closed.Version });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, priority.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, assign.StatusCode);
    }

    public static TheoryData<WorkOrderStatus, string, string> InvalidTransitions => new()
    {
        // status, actor, command
        { WorkOrderStatus.New, DemoAccounts.MorganManager, "close" },
        { WorkOrderStatus.Assigned, DemoAccounts.SamTechnician, "resolve" },
        { WorkOrderStatus.InProgress, DemoAccounts.SamTechnician, "start" },
        { WorkOrderStatus.InProgress, DemoAccounts.MorganManager, "close" },
        { WorkOrderStatus.InProgress, DemoAccounts.RileyRequester, "reopen" },
        { WorkOrderStatus.Resolved, DemoAccounts.MorganManager, "assign" },
        { WorkOrderStatus.Resolved, DemoAccounts.SamTechnician, "resolve" },
        { WorkOrderStatus.Closed, DemoAccounts.RileyRequester, "reopen" },
    };

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public async Task Invalid_status_transitions_are_rejected_and_nothing_changes(
        WorkOrderStatus status, string actorEmail, string command)
    {
        var workOrder = await CreateInStatusAsync(status);
        using var actor = await SignInAsync(actorEmail);

        var response = await actor.PostAsync($"/api/work-orders/{workOrder.Id}/{command}", new
        {
            version = workOrder.Version,
            technicianId = DemoAccounts.JordanId,
            resolutionNote = "note",
            reason = "reason",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_transition", (await ApiClient.ReadProblemAsync(response)).Code);
        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id));
        Assert.Equal(status, stored.Status);
        Assert.Equal(workOrder.Version, stored.Version);
    }

    [Fact]
    public async Task Only_the_assigned_technician_can_start_work_not_a_manager()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.PostAsync($"/api/work-orders/{workOrder.Id}/start", new { version = workOrder.Version });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolving_requires_a_resolution_note(string? note)
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.InProgress);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var response = await sam.PostAsync($"/api/work-orders/{workOrder.Id}/resolve",
            new { resolutionNote = note, version = workOrder.Version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Equal("validation_failed", problem.Code);
        Assert.Contains("resolutionNote", problem.Errors!.Keys);

        var (stored, resolvedEvents) = await QueryDbAsync(async db => (
            await db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id),
            await db.AuditEvents.CountAsync(e => e.WorkOrderId == workOrder.Id && e.Type == AuditEventType.Resolved)));
        Assert.Equal(WorkOrderStatus.InProgress, stored.Status);
        Assert.Equal(0, resolvedEvents);
    }

    [Fact]
    public async Task Reopening_requires_a_reason()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Resolved);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var response = await riley.PostAsync($"/api/work-orders/{workOrder.Id}/reopen",
            new { reason = " ", version = workOrder.Version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reason", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
    }

    [Fact]
    public async Task Reassigning_in_progress_work_keeps_its_status()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.InProgress);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var reassigned = await AssignAsync(morgan, workOrder, DemoAccounts.JordanId);

        Assert.Equal(WorkOrderStatus.InProgress, reassigned.Status);
        Assert.Equal(DemoAccounts.JordanId, reassigned.AssignedTechnician!.Id);
    }

    [Fact]
    public async Task Create_validates_input_on_the_server()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var response = await riley.PostAsync("/api/work-orders", new
        {
            propertyId = (Guid?)null,
            title = "",
            description = new string('x', 4001),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Equal(["category", "description", "propertyId", "title"], problem.Errors!.Keys.Order());
    }

    [Fact]
    public async Task List_paging_is_bounded_and_rejects_unknown_statuses()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var tooLarge = await morgan.GetAsync("/api/work-orders?pageSize=500");
        var badStatus = await morgan.GetAsync("/api/work-orders?status=Deleted");
        var filtered = await morgan.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?status=New&pageSize=2");

        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.True(filtered.Items.Count <= 2);
        Assert.All(filtered.Items, i => Assert.Equal(WorkOrderStatus.New, i.Status));
    }
}
