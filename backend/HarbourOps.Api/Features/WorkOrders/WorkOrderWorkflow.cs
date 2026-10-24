using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;

namespace HarbourOps.Api.Features.WorkOrders;

/// <summary>The commands a user can perform on an existing work order.</summary>
public enum WorkOrderAction
{
    SetPriority,
    Assign,
    Start,
    Resolve,
    Close,
    Reopen,
}

public sealed record TechnicianChoice(Guid Id, string DisplayName);

/// <summary>
/// The work-order state machine and permission rules, in one place.
///
///   New ──assign──▶ Assigned ──start──▶ InProgress ──resolve──▶ Resolved ──close──▶ Closed
///                                            ▲                       │
///                                            └────────reopen─────────┘
///
/// Every method here is a pure function: it checks the rule, changes the entity in memory, and
/// returns the audit event describing the change. It never touches HTTP or the database, which is
/// why these rules are unit-tested exhaustively without either. <see cref="WorkOrderService"/>
/// handles loading, saving, and concurrency.
/// </summary>
public static class WorkOrderWorkflow
{
    /// <summary>Which actions <paramref name="actor"/> may take right now. Sent to the UI to decide which buttons to show.</summary>
    public static IReadOnlyList<WorkOrderAction> AllowedActions(WorkOrder workOrder, CurrentUser actor) =>
        Enum.GetValues<WorkOrderAction>().Where(action => CheckAllowed(action, workOrder, actor) is null).ToArray();

    /// <summary>Returns <c>null</c> when the action is allowed, otherwise the reason it is not.</summary>
    public static Failure? CheckAllowed(WorkOrderAction action, WorkOrder workOrder, CurrentUser actor)
    {
        // Defense in depth: queries are already organization-scoped before we get here.
        if (workOrder.OrganizationId != actor.OrganizationId)
        {
            return Failure.NotFound("Work order not found.");
        }

        if (workOrder.Status == WorkOrderStatus.Closed)
        {
            return Failure.InvalidState("Closed work orders cannot be changed.");
        }

        return action switch
        {
            WorkOrderAction.SetPriority =>
                RequireManager(actor, "change priority")
                ?? RequireStatus(workOrder, "Priority can only change while work is open.",
                    WorkOrderStatus.New, WorkOrderStatus.Assigned, WorkOrderStatus.InProgress),
            WorkOrderAction.Assign =>
                RequireManager(actor, "assign technicians")
                ?? RequireStatus(workOrder, "Only new, assigned, or in-progress work can be assigned.",
                    WorkOrderStatus.New, WorkOrderStatus.Assigned, WorkOrderStatus.InProgress),
            WorkOrderAction.Start =>
                RequireAssignedTechnician(workOrder, actor, "start")
                ?? RequireStatus(workOrder, "Only assigned work can be started.", WorkOrderStatus.Assigned),
            WorkOrderAction.Resolve =>
                RequireAssignedTechnician(workOrder, actor, "resolve")
                ?? RequireStatus(workOrder, "Only work in progress can be resolved.", WorkOrderStatus.InProgress),
            WorkOrderAction.Close =>
                RequireManager(actor, "close work orders")
                ?? RequireStatus(workOrder, "Only resolved work can be closed.", WorkOrderStatus.Resolved),
            WorkOrderAction.Reopen =>
                RequireOwnRequest(workOrder, actor)
                ?? RequireStatus(workOrder, "Only resolved requests can be reopened.", WorkOrderStatus.Resolved),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    public static bool CanCreate(CurrentUser actor) => actor.IsManager || actor.IsRequester;

    /// <summary>
    /// Creates a request. The requester is always the authenticated actor and new work always
    /// starts as New with Normal priority, regardless of what a client might try to send.
    /// </summary>
    public static Result<(WorkOrder WorkOrder, AuditEvent Event)> Create(
        CurrentUser actor, Guid propertyId, string title, string description, WorkOrderCategory category, DateTimeOffset now)
    {
        if (!CanCreate(actor))
        {
            return Failure.Forbidden("Technicians cannot create requests.");
        }

        var workOrder = new WorkOrder
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = actor.OrganizationId,
            PropertyId = propertyId,
            RequesterId = actor.Id,
            Title = title.Trim(),
            Description = description.Trim(),
            Category = category,
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            CreatedAt = now,
            UpdatedAt = now,
            DueAt = WorkOrderDeadlines.CalculateDueAt(now, WorkOrderPriority.Normal),
        };

        var auditEvent = NewEvent(workOrder, actor, AuditEventType.Created, null, WorkOrderStatus.New,
            "Request submitted.", null, now);
        return (workOrder, auditEvent);
    }

    public static Result<AuditEvent> SetPriority(
        WorkOrder workOrder, CurrentUser actor, WorkOrderPriority priority, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.SetPriority, workOrder, actor) is { } failure)
        {
            return failure;
        }

        if (workOrder.Priority == priority)
        {
            return Failure.Validation("priority", $"Priority is already {priority}.");
        }

        var previous = workOrder.Priority;
        workOrder.Priority = priority;
        workOrder.DueAt = WorkOrderDeadlines.CalculateDueAt(workOrder.CreatedAt, priority);
        workOrder.UpdatedAt = now;

        return NewEvent(workOrder, actor, AuditEventType.PriorityChanged, null, null,
            $"Priority changed from {previous} to {priority}.", null, now);
    }

    /// <summary>
    /// Assigns a New request (moving it to Assigned) or reassigns Assigned/InProgress work
    /// without changing its status. The caller must already have verified that the technician
    /// is an active Technician in the actor's organization.
    /// </summary>
    public static Result<AuditEvent> Assign(
        WorkOrder workOrder, CurrentUser actor, TechnicianChoice technician, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.Assign, workOrder, actor) is { } failure)
        {
            return failure;
        }

        if (workOrder.AssignedTechnicianId == technician.Id)
        {
            return Failure.Validation("technicianId", $"{technician.DisplayName} is already assigned.");
        }

        var previousStatus = workOrder.Status;
        var isReassignment = workOrder.AssignedTechnicianId is not null;

        workOrder.AssignedTechnicianId = technician.Id;
        if (workOrder.Status == WorkOrderStatus.New)
        {
            workOrder.Status = WorkOrderStatus.Assigned;
        }

        workOrder.UpdatedAt = now;

        return isReassignment
            ? NewEvent(workOrder, actor, AuditEventType.Reassigned, null, null,
                $"Reassigned to {technician.DisplayName}.", null, now)
            : NewEvent(workOrder, actor, AuditEventType.Assigned, previousStatus, workOrder.Status,
                $"Assigned to {technician.DisplayName}.", null, now);
    }

    public static Result<AuditEvent> Start(WorkOrder workOrder, CurrentUser actor, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.Start, workOrder, actor) is { } failure)
        {
            return failure;
        }

        return ChangeStatus(workOrder, actor, WorkOrderStatus.InProgress, AuditEventType.Started,
            "Work started.", null, now);
    }

    public static Result<AuditEvent> Resolve(WorkOrder workOrder, CurrentUser actor, string? resolutionNote, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.Resolve, workOrder, actor) is { } failure)
        {
            return failure;
        }

        if (ValidateNote("resolutionNote", resolutionNote, "A resolution note") is { } invalid)
        {
            return invalid;
        }

        var note = resolutionNote!.Trim();
        workOrder.ResolutionNote = note;
        workOrder.ResolvedAt = now;
        return ChangeStatus(workOrder, actor, WorkOrderStatus.Resolved, AuditEventType.Resolved,
            "Marked resolved.", note, now);
    }

    public static Result<AuditEvent> Close(WorkOrder workOrder, CurrentUser actor, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.Close, workOrder, actor) is { } failure)
        {
            return failure;
        }

        workOrder.ClosedAt = now;
        return ChangeStatus(workOrder, actor, WorkOrderStatus.Closed, AuditEventType.Closed,
            "Closed.", null, now);
    }

    /// <summary>
    /// The requester reports the problem is not fixed. Work returns to InProgress with the same
    /// technician and the original deadline. The previous resolution note stays in the history.
    /// </summary>
    public static Result<AuditEvent> Reopen(WorkOrder workOrder, CurrentUser actor, string? reason, DateTimeOffset now)
    {
        if (CheckAllowed(WorkOrderAction.Reopen, workOrder, actor) is { } failure)
        {
            return failure;
        }

        if (ValidateNote("reason", reason, "A reason") is { } invalid)
        {
            return invalid;
        }

        var note = reason!.Trim();
        workOrder.ResolutionNote = null;
        workOrder.ResolvedAt = null;
        return ChangeStatus(workOrder, actor, WorkOrderStatus.InProgress, AuditEventType.Reopened,
            "Reopened by requester.", note, now);
    }

    private static AuditEvent ChangeStatus(
        WorkOrder workOrder, CurrentUser actor, WorkOrderStatus to, AuditEventType type,
        string summary, string? note, DateTimeOffset now)
    {
        var from = workOrder.Status;
        workOrder.Status = to;
        workOrder.UpdatedAt = now;
        return NewEvent(workOrder, actor, type, from, to, summary, note, now);
    }

    private static AuditEvent NewEvent(
        WorkOrder workOrder, CurrentUser actor, AuditEventType type, WorkOrderStatus? from, WorkOrderStatus? to,
        string summary, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = workOrder.OrganizationId,
        WorkOrderId = workOrder.Id,
        ActorId = actor.Id,
        Type = type,
        FromStatus = from,
        ToStatus = to,
        Summary = summary,
        Note = note,
        OccurredAt = now,
    };

    private static Failure? ValidateNote(string field, string? value, string label)
    {
        var errors = new ValidationErrors();
        errors.RequireText(field, value, WorkOrderConfiguration.NoteMaxLength, label);
        return errors.HasErrors ? errors.ToFailure() : null;
    }

    private static Failure? RequireManager(CurrentUser actor, string verb) =>
        actor.Role == UserRole.Manager ? null : Failure.Forbidden($"Only managers can {verb}.");

    private static Failure? RequireAssignedTechnician(WorkOrder workOrder, CurrentUser actor, string verb) =>
        actor.Role == UserRole.Technician && workOrder.AssignedTechnicianId == actor.Id
            ? null
            : Failure.Forbidden($"Only the assigned technician can {verb} this work.");

    private static Failure? RequireOwnRequest(WorkOrder workOrder, CurrentUser actor) =>
        actor.Role == UserRole.Requester && workOrder.RequesterId == actor.Id
            ? null
            : Failure.Forbidden("Only the requester who submitted this request can reopen it.");

    private static Failure? RequireStatus(WorkOrder workOrder, string message, params WorkOrderStatus[] allowed) =>
        allowed.Contains(workOrder.Status) ? null : Failure.InvalidState(message);
}
