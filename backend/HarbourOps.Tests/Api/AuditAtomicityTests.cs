using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>A workflow update and its audit event are committed together, or not at all.</summary>
public sealed class AuditAtomicityTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Successful_command_stores_the_change_and_exactly_one_matching_audit_event()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        var started = await StartAsync(sam, workOrder);

        var startedEvents = await QueryDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == workOrder.Id && e.Type == AuditEventType.Started).ToListAsync());
        var auditEvent = Assert.Single(startedEvents);
        Assert.Equal(WorkOrderStatus.Assigned, auditEvent.FromStatus);
        Assert.Equal(WorkOrderStatus.InProgress, auditEvent.ToStatus);
        Assert.Equal(DemoAccounts.SamId, auditEvent.ActorId);
        Assert.Equal(started.UpdatedAt, auditEvent.OccurredAt);
    }

    [Fact]
    public async Task If_the_audit_insert_fails_the_workflow_update_is_rolled_back()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Assigned);
        using var sam = await SignInAsync(DemoAccounts.SamTechnician);

        // Make PostgreSQL reject the audit row for this one work order. A *deferred* constraint trigger
        // fires at COMMIT, after both the work-order UPDATE and the audit INSERT have executed, so this
        // proves the two are rolled back together regardless of the order EF sends them in.
        var function = $"fail_audit_{workOrder.Id:N}";
        var createTrigger = $"""
            CREATE FUNCTION {function}() RETURNS trigger AS $$
            BEGIN
              IF NEW."WorkOrderId" = '{workOrder.Id}' THEN
                RAISE EXCEPTION 'simulated audit failure';
              END IF;
              RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER {function} AFTER INSERT ON "AuditEvents"
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION {function}();
            """;
        var dropTrigger = $"""DROP TRIGGER {function} ON "AuditEvents"; DROP FUNCTION {function}();""";
        await QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(createTrigger));

        try
        {
            var response = await sam.PostAsync($"/api/work-orders/{workOrder.Id}/start", new { version = workOrder.Version });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(dropTrigger));
        }

        var (stored, startedEvents) = await QueryDbAsync(async db => (
            await db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id),
            await db.AuditEvents.CountAsync(e => e.WorkOrderId == workOrder.Id && e.Type == AuditEventType.Started)));
        Assert.Equal(WorkOrderStatus.Assigned, stored.Status);
        Assert.Equal(workOrder.Version, stored.Version);
        Assert.Equal(0, startedEvents);
    }
}
