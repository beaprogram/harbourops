using HarbourOps.Api.Data;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.Extensions.DependencyInjection;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// Base class for API tests against real PostgreSQL. Tests share one seeded database, so each test
/// creates the work orders it changes instead of relying on mutable shared records.
/// </summary>
[Collection(PostgresCollection.Name)]
public abstract class IntegrationTest(PostgresFixture fixture)
{
    protected PostgresFixture Fixture => fixture;

    protected HarbourOpsFactory Factory => fixture.Factory;

    protected Task<ApiClient> SignInAsync(string email) => Factory.SignInAsync(email);

    protected static Task<WorkOrderDetailResponse> CreateRequestAsync(
        ApiClient requester, Guid? propertyId = null, string title = "Leaking radiator valve") =>
        requester.PostJsonAsync<WorkOrderDetailResponse>("/api/work-orders", new
        {
            propertyId = propertyId ?? DemoAccounts.QuaysideLofts,
            title,
            description = "Created by an integration test.",
            category = "Plumbing",
        });

    protected static Task<WorkOrderDetailResponse> AssignAsync(ApiClient manager, WorkOrderDetailResponse workOrder, Guid technicianId) =>
        manager.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/assign", new { technicianId, version = workOrder.Version });

    protected static Task<WorkOrderDetailResponse> StartAsync(ApiClient technician, WorkOrderDetailResponse workOrder) =>
        technician.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/start", new { version = workOrder.Version });

    protected static Task<WorkOrderDetailResponse> ResolveAsync(
        ApiClient technician, WorkOrderDetailResponse workOrder, string note = "Replaced the valve washer.") =>
        technician.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/resolve", new { resolutionNote = note, version = workOrder.Version });

    /// <summary>Creates a Harbourview request (Riley → Morgan → Sam) and drives it to <paramref name="status"/>.</summary>
    protected async Task<WorkOrderDetailResponse> CreateInStatusAsync(WorkOrderStatus status)
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var workOrder = await CreateRequestAsync(riley);
        if (status == WorkOrderStatus.New)
        {
            return workOrder;
        }

        workOrder = await AssignAsync(morgan, workOrder, DemoAccounts.SamId);
        if (status == WorkOrderStatus.Assigned)
        {
            return workOrder;
        }

        workOrder = await StartAsync(sam, workOrder);
        if (status == WorkOrderStatus.InProgress)
        {
            return workOrder;
        }

        workOrder = await ResolveAsync(sam, workOrder);
        if (status == WorkOrderStatus.Resolved)
        {
            return workOrder;
        }

        return await morgan.PostJsonAsync<WorkOrderDetailResponse>(
            $"/api/work-orders/{workOrder.Id}/close", new { version = workOrder.Version });
    }

    /// <summary>Reads the database directly, bypassing the API, to check what was really stored.</summary>
    protected async Task<T> QueryDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
