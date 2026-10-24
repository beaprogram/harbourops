using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>Within one organization, requesters and technicians only reach their own work.</summary>
public sealed class RoleVisibilityTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Requester_cannot_read_another_requesters_work()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var detail = await riley.GetAsync($"/api/work-orders/{DemoAccounts.AlexHeatingRequest}");
        var activity = await riley.GetAsync($"/api/work-orders/{DemoAccounts.AlexHeatingRequest}/activity");

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activity.StatusCode);
    }

    [Fact]
    public async Task Requester_list_contains_only_their_own_requests()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var list = await riley.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");

        var ids = list.Items.Select(i => i.Id).ToList();
        Assert.NotEmpty(ids);
        var requesterIds = await QueryDbAsync(db =>
            db.WorkOrders.Where(w => ids.Contains(w.Id)).Select(w => w.RequesterId).Distinct().ToListAsync());
        Assert.Equal([DemoAccounts.RileyId], requesterIds);
    }

    [Fact]
    public async Task Technician_list_contains_only_work_assigned_to_them()
    {
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var list = await sam.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");

        var ids = list.Items.Select(i => i.Id).ToList();
        Assert.NotEmpty(ids);
        var assignees = await QueryDbAsync(db =>
            db.WorkOrders.Where(w => ids.Contains(w.Id)).Select(w => w.AssignedTechnicianId).Distinct().ToListAsync());
        Assert.Equal([DemoAccounts.SamId], assignees);
    }

    [Fact]
    public async Task Technician_cannot_update_another_technicians_job()
    {
        var samsJob = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var jordan = await SignInAsync(DemoAccounts.JordanTechnician);

        var start = await jordan.PostAsync($"/api/work-orders/{samsJob.Id}/start", new { version = samsJob.Version });
        var resolve = await jordan.PostAsync($"/api/work-orders/{samsJob.Id}/resolve",
            new { resolutionNote = "Not my job", version = samsJob.Version });

        // Jordan cannot even see Sam's job, so the response is the same as for a missing record.
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode);
        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == samsJob.Id));
        Assert.Equal(WorkOrderStatus.Assigned, stored.Status);
    }

    [Fact]
    public async Task Requester_cannot_perform_manager_actions_on_their_own_request()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);

        var assign = await riley.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
            new { technicianId = DemoAccounts.SamId, version = workOrder.Version });
        var priority = await riley.PostAsync($"/api/work-orders/{workOrder.Id}/priority",
            new { priority = "High", version = workOrder.Version });

        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, priority.StatusCode);
        Assert.Equal("forbidden", (await ApiClient.ReadProblemAsync(assign)).Code);
    }

    [Fact]
    public async Task Technicians_cannot_create_requests_or_browse_the_technician_list()
    {
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var create = await sam.PostAsync("/api/work-orders", new
        {
            propertyId = DemoAccounts.QuaysideLofts,
            title = "Technician request",
            description = "Should be rejected",
            category = "General",
        });
        var technicians = await sam.GetAsync("/api/technicians");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, technicians.StatusCode);
    }

    [Fact]
    public async Task Allowed_actions_reflect_the_callers_role()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var asManager = await morgan.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        var asTechnician = await sam.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");
        var asRequester = await riley.GetJsonAsync<WorkOrderDetailResponse>($"/api/work-orders/{workOrder.Id}");

        Assert.Equal([WorkOrderAction.SetPriority, WorkOrderAction.Assign], asManager.AllowedActions);
        Assert.Equal([WorkOrderAction.Start], asTechnician.AllowedActions);
        Assert.Empty(asRequester.AllowedActions);
    }
}
