using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Features.WorkOrders;

public sealed record WorkOrderListQuery(int Page, int PageSize, IReadOnlyList<WorkOrderStatus> Statuses);

/// <summary>
/// Application service for work orders: loads data the user may see, asks
/// <see cref="WorkOrderWorkflow"/> whether a change is allowed, and saves the change together
/// with its audit event. HTTP concerns stay in <see cref="WorkOrderEndpoints"/>.
/// </summary>
public sealed class WorkOrderService(AppDbContext db, TimeProvider clock, ILogger<WorkOrderService> logger)
{
    private static readonly Failure NotFound = Failure.NotFound("Work order not found.");

    private static readonly Failure StaleVersion = Failure.Conflict(
        "Someone else updated this work order after you loaded it, so your change was not saved. " +
        "Load the latest version and try again.");

    public async Task<PagedResponse<WorkOrderListItem>> ListAsync(
        CurrentUser user, WorkOrderListQuery query, CancellationToken ct)
    {
        var workOrders = db.WorkOrders.AsNoTracking().VisibleTo(user);
        if (query.Statuses.Count > 0)
        {
            workOrders = workOrders.Where(w => query.Statuses.Contains(w.Status));
        }

        var totalCount = await workOrders.CountAsync(ct);

        // Newest first, with Id as a tie-breaker so paging is deterministic.
        var items = await workOrders
            .OrderByDescending(w => w.CreatedAt)
            .ThenByDescending(w => w.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(w => new WorkOrderListItem(
                w.Id,
                w.Number,
                w.Title,
                w.Status,
                w.Priority,
                w.Category,
                w.Property.Name,
                w.Requester.DisplayName,
                w.AssignedTechnician != null ? w.AssignedTechnician.DisplayName : null,
                w.CreatedAt,
                w.UpdatedAt,
                w.DueAt,
                false))
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var withOverdue = items
            .Select(i => i with { IsOverdue = WorkOrderDeadlines.IsOverdue(i.Status, i.DueAt, now) })
            .ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)query.PageSize);
        return new PagedResponse<WorkOrderListItem>(withOverdue, query.Page, query.PageSize, totalCount, totalPages);
    }

    public async Task<Result<WorkOrderDetailResponse>> GetAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var row = await db.WorkOrders
            .AsNoTracking()
            .VisibleTo(user)
            .Where(w => w.Id == id)
            .Select(w => new
            {
                WorkOrder = w,
                Property = new PropertySummary(w.Property.Id, w.Property.Name, w.Property.AddressLine, w.Property.Community),
                Requester = new PersonSummary(w.Requester.Id, w.Requester.DisplayName),
                Technician = w.AssignedTechnician != null
                    ? new PersonSummary(w.AssignedTechnician.Id, w.AssignedTechnician.DisplayName)
                    : null,
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return NotFound;
        }

        var w = row.WorkOrder;
        return new WorkOrderDetailResponse(
            w.Id, w.Number, w.Title, w.Description, w.Category, w.Priority, w.Status,
            row.Property, row.Requester, row.Technician,
            w.CreatedAt, w.UpdatedAt, w.DueAt,
            WorkOrderDeadlines.IsOverdue(w.Status, w.DueAt, clock.GetUtcNow()),
            w.ResolutionNote, w.ResolvedAt, w.ClosedAt, w.Version,
            WorkOrderWorkflow.AllowedActions(w, user));
    }

    public async Task<Result<ActivityEventResponse[]>> GetActivityAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        if (!await db.WorkOrders.VisibleTo(user).AnyAsync(w => w.Id == id, ct))
        {
            return NotFound;
        }

        return await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.OrganizationId == user.OrganizationId && e.WorkOrderId == id)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new ActivityEventResponse(
                e.Id, e.Type, e.FromStatus, e.ToStatus, e.Summary, e.Note,
                new PersonSummary(e.Actor.Id, e.Actor.DisplayName), e.Actor.Role, e.OccurredAt))
            .ToArrayAsync(ct);
    }

    public async Task<Result<WorkOrderDetailResponse>> CreateAsync(
        CurrentUser user, CreateWorkOrderRequest request, CancellationToken ct)
    {
        if (!WorkOrderWorkflow.CanCreate(user))
        {
            return Failure.Forbidden("Technicians cannot create requests.");
        }

        var errors = request.Validate();
        if (!errors.HasErrors)
        {
            // The property must belong to the caller's own organization.
            var propertyIsEligible = await db.Properties.AnyAsync(
                p => p.Id == request.PropertyId && p.OrganizationId == user.OrganizationId && p.IsActive, ct);
            if (!propertyIsEligible)
            {
                errors.Add("propertyId", "Choose a property from your organization.");
            }
        }

        if (errors.HasErrors)
        {
            return errors.ToFailure();
        }

        var created = WorkOrderWorkflow.Create(
            user, request.PropertyId!.Value, request.Title!, request.Description!, request.Category!.Value,
            clock.GetUtcNow());
        if (created.Failure is { } failure)
        {
            return failure;
        }

        var (workOrder, auditEvent) = created.Value;
        db.WorkOrders.Add(workOrder);
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Work order {WorkOrderId} created by user {UserId} in organization {OrganizationId}",
            workOrder.Id, user.Id, user.OrganizationId);

        return await GetAsync(user, workOrder.Id, ct);
    }

    public Task<Result<WorkOrderDetailResponse>> SetPriorityAsync(
        CurrentUser user, Guid id, SetPriorityRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.SetPriority, (workOrder, now) =>
        {
            if (WorkOrderWorkflow.CheckAllowed(WorkOrderAction.SetPriority, workOrder, user) is { } denied)
            {
                return Task.FromResult<Result<AuditEvent>>(denied);
            }

            return Task.FromResult(request.Priority is { } priority
                ? WorkOrderWorkflow.SetPriority(workOrder, user, priority, now)
                : Failure.Validation("priority", "Choose a priority."));
        }, ct);

    public Task<Result<WorkOrderDetailResponse>> AssignAsync(
        CurrentUser user, Guid id, AssignTechnicianRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.Assign, async (workOrder, now) =>
        {
            // Check permission before looking anything up, so non-managers learn nothing about technicians.
            if (WorkOrderWorkflow.CheckAllowed(WorkOrderAction.Assign, workOrder, user) is { } denied)
            {
                return denied;
            }

            if (request.TechnicianId is not { } technicianId)
            {
                return Failure.Validation("technicianId", "Choose a technician.");
            }

            // The assignee must be an active Technician in the caller's own organization.
            var technician = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == technicianId
                    && u.OrganizationId == user.OrganizationId
                    && u.Role == UserRole.Technician
                    && u.IsActive)
                .Select(u => new TechnicianChoice(u.Id, u.DisplayName))
                .SingleOrDefaultAsync(ct);

            return technician is null
                ? Failure.Validation("technicianId", "Choose an active technician from your organization.")
                : WorkOrderWorkflow.Assign(workOrder, user, technician, now);
        }, ct);

    public Task<Result<WorkOrderDetailResponse>> StartAsync(
        CurrentUser user, Guid id, WorkOrderCommandRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.Start,
            (workOrder, now) => Task.FromResult(WorkOrderWorkflow.Start(workOrder, user, now)), ct);

    public Task<Result<WorkOrderDetailResponse>> ResolveAsync(
        CurrentUser user, Guid id, ResolveWorkOrderRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.Resolve,
            (workOrder, now) => Task.FromResult(WorkOrderWorkflow.Resolve(workOrder, user, request.ResolutionNote, now)), ct);

    public Task<Result<WorkOrderDetailResponse>> CloseAsync(
        CurrentUser user, Guid id, WorkOrderCommandRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.Close,
            (workOrder, now) => Task.FromResult(WorkOrderWorkflow.Close(workOrder, user, now)), ct);

    public Task<Result<WorkOrderDetailResponse>> ReopenAsync(
        CurrentUser user, Guid id, ReopenWorkOrderRequest request, CancellationToken ct) =>
        ApplyAsync(user, id, request, WorkOrderAction.Reopen,
            (workOrder, now) => Task.FromResult(WorkOrderWorkflow.Reopen(workOrder, user, request.Reason, now)), ct);

    /// <summary>
    /// The shared path for every workflow command:
    /// 1. Load the work order through <see cref="WorkOrderQueries.VisibleTo"/> (404 if not visible).
    /// 2. Compare the client's version with the stored one (409 if the client is out of date).
    /// 3. Apply the workflow rule, which updates the entity and returns an audit event.
    /// 4. Save both in one transaction. PostgreSQL re-checks the version during the UPDATE, so a
    ///    change that lands between steps 2 and 4 is also detected and reported as 409.
    /// </summary>
    private async Task<Result<WorkOrderDetailResponse>> ApplyAsync(
        CurrentUser user,
        Guid id,
        IVersionedRequest request,
        WorkOrderAction action,
        Func<WorkOrder, DateTimeOffset, Task<Result<AuditEvent>>> change,
        CancellationToken ct)
    {
        if (request.Version is not { } expectedVersion)
        {
            return Failure.Validation("version", "The work order version is required.");
        }

        var workOrder = await db.WorkOrders.VisibleTo(user).SingleOrDefaultAsync(w => w.Id == id, ct);
        if (workOrder is null)
        {
            return NotFound;
        }

        if (workOrder.Version != expectedVersion)
        {
            return StaleVersion;
        }

        var outcome = await change(workOrder, clock.GetUtcNow());
        if (outcome.Failure is { } failure)
        {
            return failure;
        }

        db.AuditEvents.Add(outcome.Value!);
        try
        {
            // A single SaveChanges runs in a single database transaction: the UPDATE of the work order
            // (guarded by "WHERE xmin = <version>") and the INSERT of its audit event succeed or fail together.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "Stale {Action} rejected for work order {WorkOrderId} by user {UserId}", action, id, user.Id);
            return StaleVersion;
        }

        logger.LogInformation(
            "Work order {WorkOrderId}: {Action} by user {UserId} in organization {OrganizationId}",
            id, action, user.Id, user.OrganizationId);

        return await GetAsync(user, id, ct);
    }
}
