using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>Optimistic concurrency: a stale update gets 409 and never overwrites the winning change.</summary>
public sealed class ConcurrencyTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Stale_update_returns_409_and_the_winning_update_is_kept()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);
        // Two managers' browser tabs, both showing the same version.
        using var firstTab = await SignInAsync(DemoAccounts.MorganManager);
        using var secondTab = await SignInAsync(DemoAccounts.MorganManager);

        var winner = await AssignAsync(firstTab, workOrder, DemoAccounts.SamId);
        var stale = await secondTab.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
            new { technicianId = DemoAccounts.JordanId, version = workOrder.Version });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale_version", (await ApiClient.ReadProblemAsync(stale)).Code);

        var (stored, assignmentEvents) = await QueryDbAsync(async db => (
            await db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id),
            await db.AuditEvents.CountAsync(e => e.WorkOrderId == workOrder.Id
                && (e.Type == AuditEventType.Assigned || e.Type == AuditEventType.Reassigned))));
        Assert.Equal(DemoAccounts.SamId, stored.AssignedTechnicianId);
        Assert.Equal(winner.Version, stored.Version);
        Assert.Equal(1, assignmentEvents);
    }

    [Fact]
    public async Task Simultaneous_updates_with_the_same_version_let_exactly_one_win()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);
        using var firstTab = await SignInAsync(DemoAccounts.MorganManager);
        using var secondTab = await SignInAsync(DemoAccounts.MorganManager);

        var responses = await Task.WhenAll(
            firstTab.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
                new { technicianId = DemoAccounts.SamId, version = workOrder.Version }),
            secondTab.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
                new { technicianId = DemoAccounts.JordanId, version = workOrder.Version }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var assignmentEvents = await QueryDbAsync(db => db.AuditEvents.CountAsync(e =>
            e.WorkOrderId == workOrder.Id && (e.Type == AuditEventType.Assigned || e.Type == AuditEventType.Reassigned)));
        Assert.Equal(1, assignmentEvents);
    }

    [Fact]
    public async Task Missing_version_is_a_validation_error()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.PostAsync($"/api/work-orders/{workOrder.Id}/priority", new { priority = "High" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("version", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
    }
}
