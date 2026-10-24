using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Organizations;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo;

/// <summary>
/// Creates the fictional demo organizations, accounts, properties, and work orders.
/// Only runs when DemoData:Enabled is true. Every record has a fixed ID and is only inserted
/// when missing, so rerunning the seeder never duplicates data.
/// </summary>
public sealed class DemoDataSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    TimeProvider clock,
    IOptions<DemoDataOptions> options,
    ILogger<DemoDataSeeder> logger)
{
    // Arbitrary constant; serializes seeding if several app instances start at once.
    private const long SeedLockKey = 724_001;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var password = options.Value.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("DemoData:Password must be set when DemoData:Enabled is true.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SeedLockKey})", ct);

        var now = clock.GetUtcNow();
        var created = 0;

        foreach (var org in DemoData.Organizations)
        {
            if (!await db.Organizations.AnyAsync(o => o.Id == org.Id, ct))
            {
                db.Organizations.Add(new Organization { Id = org.Id, Name = org.Name, Slug = org.Slug, CreatedAt = now });
                created++;
            }
        }

        foreach (var property in DemoData.Properties)
        {
            if (!await db.Properties.AnyAsync(p => p.Id == property.Id, ct))
            {
                db.Properties.Add(new Property
                {
                    Id = property.Id,
                    OrganizationId = property.OrganizationId,
                    Name = property.Name,
                    AddressLine = property.AddressLine,
                    Community = property.Community,
                });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var demoUser in DemoData.Users)
        {
            if (await userManager.FindByIdAsync(demoUser.Id.ToString()) is not null)
            {
                continue;
            }

            var user = new ApplicationUser
            {
                Id = demoUser.Id,
                OrganizationId = demoUser.OrganizationId,
                UserName = demoUser.Email,
                Email = demoUser.Email,
                EmailConfirmed = true,
                DisplayName = demoUser.DisplayName,
                Role = demoUser.Role,
            };

            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create demo user {demoUser.Email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }

            created++;
        }

        foreach (var demo in DemoData.WorkOrders)
        {
            if (await db.WorkOrders.AnyAsync(w => w.Id == demo.Id, ct))
            {
                continue;
            }

            AddWorkOrderWithHistory(demo, now);
            created++;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("Demo data seeding finished: {CreatedCount} records created", created);
    }

    /// <summary>
    /// Replays the demo request through <see cref="WorkOrderWorkflow"/> so seeded records follow the
    /// same rules as real ones and get a realistic activity history.
    /// </summary>
    private void AddWorkOrderWithHistory(DemoWorkOrder demo, DateTimeOffset now)
    {
        var property = DemoData.Properties.Single(p => p.Id == demo.PropertyId);
        var requester = Actor(demo.RequesterId);
        var manager = Actor(DemoData.Users.Single(u => u.OrganizationId == property.OrganizationId && u.Role == UserRole.Manager).Id);
        var createdAt = now.AddHours(-demo.CreatedHoursAgo);
        DateTimeOffset Step(double fraction) => createdAt.AddHours(demo.CreatedHoursAgo * fraction);

        var events = new List<AuditEvent>();
        var (workOrder, createdEvent) = Expect(
            WorkOrderWorkflow.Create(requester, demo.PropertyId, demo.Title, demo.Description, demo.Category, createdAt));
        workOrder.Id = demo.Id;
        events.Add(createdEvent);

        if (demo.Priority != WorkOrderPriority.Normal)
        {
            events.Add(Expect(WorkOrderWorkflow.SetPriority(workOrder, manager, demo.Priority, Step(0.05))));
        }

        if (demo.Status != WorkOrderStatus.New)
        {
            var technician = DemoData.Users.Single(u => u.Id == demo.TechnicianId);
            events.Add(Expect(WorkOrderWorkflow.Assign(
                workOrder, manager, new TechnicianChoice(technician.Id, technician.DisplayName), Step(0.1))));
        }

        if (demo.Status is WorkOrderStatus.InProgress or WorkOrderStatus.Resolved or WorkOrderStatus.Closed)
        {
            var technician = Actor(demo.TechnicianId!.Value);
            events.Add(Expect(WorkOrderWorkflow.Start(workOrder, technician, Step(0.25))));

            if (demo.ResolutionNote is not null)
            {
                events.Add(Expect(WorkOrderWorkflow.Resolve(workOrder, technician, demo.ResolutionNote, Step(0.5))));
            }

            if (demo.ReopenReason is not null)
            {
                events.Add(Expect(WorkOrderWorkflow.Reopen(workOrder, requester, demo.ReopenReason, Step(0.7))));
            }
        }

        if (demo.Status == WorkOrderStatus.Closed)
        {
            events.Add(Expect(WorkOrderWorkflow.Close(workOrder, manager, Step(0.8))));
        }

        if (workOrder.Status != demo.Status)
        {
            throw new InvalidOperationException($"Demo work order {demo.Id} ended as {workOrder.Status}, expected {demo.Status}.");
        }

        foreach (var auditEvent in events)
        {
            auditEvent.WorkOrderId = workOrder.Id;
        }

        db.WorkOrders.Add(workOrder);
        db.AuditEvents.AddRange(events);
    }

    private static CurrentUser Actor(Guid userId)
    {
        var user = DemoData.Users.Single(u => u.Id == userId);
        var organization = DemoData.Organizations.Single(o => o.Id == user.OrganizationId);
        return new CurrentUser(user.Id, user.Email, user.DisplayName, user.Role, organization.Id, organization.Name);
    }

    private static T Expect<T>(Result<T> result) =>
        result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException($"Demo data violates a workflow rule: {result.Failure!.Message}");
}
