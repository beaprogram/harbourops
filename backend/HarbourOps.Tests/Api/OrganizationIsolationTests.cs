using System.Net;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Organization A (Harbourview) and organization B (Cove Commons) must never see or change each
/// other's data, and cross-organization records must look exactly like records that do not exist.
/// </summary>
public sealed class OrganizationIsolationTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Lists_only_contain_the_callers_organization()
    {
        using var dana = await SignInAsync(DemoAccounts.DanaManager);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var coveList = await dana.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");
        var harbourList = await morgan.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");

        var coveIds = coveList.Items.Select(i => i.Id).ToHashSet();
        var harbourIds = harbourList.Items.Select(i => i.Id).ToHashSet();
        Assert.NotEmpty(coveIds);
        Assert.NotEmpty(harbourIds);

        var organizationsById = await QueryDbAsync(db => db.WorkOrders
            .Where(w => coveIds.Contains(w.Id) || harbourIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.OrganizationId));
        Assert.All(coveIds, id => Assert.Equal(DemoAccounts.CoveCommonsId, organizationsById[id]));
        Assert.All(harbourIds, id => Assert.Equal(DemoAccounts.HarbourviewId, organizationsById[id]));
    }

    [Theory]
    [InlineData("/api/work-orders/{0}")]
    [InlineData("/api/work-orders/{0}/activity")]
    public async Task Reading_another_organizations_record_looks_like_it_does_not_exist(string urlFormat)
    {
        using var dana = await SignInAsync(DemoAccounts.DanaManager);

        var crossOrganization = await dana.GetAsync(string.Format(urlFormat, DemoAccounts.AlexHeatingRequest));
        var nonexistent = await dana.GetAsync(string.Format(urlFormat, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, crossOrganization.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(
            (await ApiClient.ReadProblemAsync(nonexistent)).Detail,
            (await ApiClient.ReadProblemAsync(crossOrganization)).Detail);
    }

    [Fact]
    public async Task Another_organizations_manager_cannot_modify_a_record()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);
        using var dana = await SignInAsync(DemoAccounts.DanaManager);

        var priority = await dana.PostAsync($"/api/work-orders/{workOrder.Id}/priority",
            new { priority = "High", version = workOrder.Version });
        var assign = await dana.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
            new { technicianId = DemoAccounts.CaseyId, version = workOrder.Version });

        Assert.Equal(HttpStatusCode.NotFound, priority.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, assign.StatusCode);
        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id));
        Assert.Equal(WorkOrderStatus.New, stored.Status);
        Assert.Equal(WorkOrderPriority.Normal, stored.Priority);
        Assert.Equal(workOrder.Version, stored.Version);
    }

    [Fact]
    public async Task Creating_a_request_for_another_organizations_property_is_rejected()
    {
        using var taylor = await SignInAsync(DemoAccounts.TaylorRequester);

        var response = await taylor.PostAsync("/api/work-orders", new
        {
            propertyId = DemoAccounts.QuaysideLofts, // belongs to Harbourview
            title = "Cross-organization property",
            description = "Should be rejected",
            category = "General",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Contains("propertyId", problem.Errors!.Keys);
    }

    [Fact]
    public async Task Assigning_another_organizations_technician_is_rejected()
    {
        using var taylor = await SignInAsync(DemoAccounts.TaylorRequester);
        using var dana = await SignInAsync(DemoAccounts.DanaManager);
        var workOrder = await CreateRequestAsync(taylor, DemoAccounts.CommonsWalk);

        var response = await dana.PostAsync($"/api/work-orders/{workOrder.Id}/assign",
            new { technicianId = DemoAccounts.SamId, version = workOrder.Version }); // Sam works for Harbourview

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("technicianId", (await ApiClient.ReadProblemAsync(response)).Errors!.Keys);
        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id));
        Assert.Null(stored.AssignedTechnicianId);
    }

    [Fact]
    public async Task Property_and_technician_pickers_only_offer_the_callers_organization()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var properties = await morgan.GetJsonAsync<PropertySummary[]>("/api/properties");
        var technicians = await morgan.GetJsonAsync<TechnicianResponse[]>("/api/technicians");

        Assert.DoesNotContain(properties, p => p.Id == DemoAccounts.CommonsWalk);
        Assert.Contains(properties, p => p.Id == DemoAccounts.QuaysideLofts);
        Assert.Equal([DemoAccounts.SamId, DemoAccounts.JordanId], technicians.Select(t => t.Id).Order());
    }

    [Fact]
    public async Task Client_supplied_organization_and_requester_ids_are_ignored()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var created = await riley.PostJsonAsync<WorkOrderDetailResponse>("/api/work-orders", new
        {
            propertyId = DemoAccounts.QuaysideLofts,
            title = "Attempt to spoof ownership",
            description = "Extra fields should have no effect.",
            category = "General",
            organizationId = DemoAccounts.CoveCommonsId,
            requesterId = DemoAccounts.AlexId,
            status = "Closed",
            priority = "High",
        });

        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == created.Id));
        Assert.Equal(DemoAccounts.HarbourviewId, stored.OrganizationId);
        Assert.Equal(DemoAccounts.RileyId, stored.RequesterId);
        Assert.Equal(WorkOrderStatus.New, stored.Status);
        Assert.Equal(WorkOrderPriority.Normal, stored.Priority);
    }

    [Fact]
    public async Task Database_constraints_reject_cross_organization_references_even_if_app_checks_were_bypassed()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);

        // Bypass the API and try to point a Harbourview work order at a Cove Commons property.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => QueryDbAsync(async db =>
        {
            var tracked = await db.WorkOrders.SingleAsync(w => w.Id == workOrder.Id);
            tracked.PropertyId = DemoAccounts.CommonsWalk;
            return await db.SaveChangesAsync();
        }));

        Assert.Contains("FK_WorkOrders_Properties_OrganizationId_PropertyId", exception.InnerException!.Message);
    }
}
