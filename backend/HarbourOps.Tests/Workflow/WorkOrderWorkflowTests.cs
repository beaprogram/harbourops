using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Tests.Workflow;

/// <summary>
/// Fast unit tests for the state machine. No database or HTTP: the workflow is plain C#.
/// </summary>
public sealed class WorkOrderWorkflowTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly DateTimeOffset Created = new(2026, 1, 15, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Created.AddHours(2);

    private static readonly CurrentUser Manager = User(UserRole.Manager);
    private static readonly CurrentUser AssignedTechnician = User(UserRole.Technician);
    private static readonly CurrentUser OtherTechnician = User(UserRole.Technician);
    private static readonly CurrentUser OwningRequester = User(UserRole.Requester);
    private static readonly CurrentUser OtherRequester = User(UserRole.Requester);

    public enum Actor
    {
        Manager,
        AssignedTechnician,
        OtherTechnician,
        OwningRequester,
        OtherRequester,
    }

    /// <summary>
    /// The complete permission table: for every status and every kind of actor, exactly which
    /// actions are allowed. Anything not listed must be rejected.
    /// </summary>
    public static TheoryData<WorkOrderStatus, Actor, WorkOrderAction[]> PermissionTable => new()
    {
        { WorkOrderStatus.New, Actor.Manager, [WorkOrderAction.SetPriority, WorkOrderAction.Assign] },
        { WorkOrderStatus.New, Actor.AssignedTechnician, [] },
        { WorkOrderStatus.New, Actor.OtherTechnician, [] },
        { WorkOrderStatus.New, Actor.OwningRequester, [] },
        { WorkOrderStatus.New, Actor.OtherRequester, [] },

        { WorkOrderStatus.Assigned, Actor.Manager, [WorkOrderAction.SetPriority, WorkOrderAction.Assign] },
        { WorkOrderStatus.Assigned, Actor.AssignedTechnician, [WorkOrderAction.Start] },
        { WorkOrderStatus.Assigned, Actor.OtherTechnician, [] },
        { WorkOrderStatus.Assigned, Actor.OwningRequester, [] },
        { WorkOrderStatus.Assigned, Actor.OtherRequester, [] },

        { WorkOrderStatus.InProgress, Actor.Manager, [WorkOrderAction.SetPriority, WorkOrderAction.Assign] },
        { WorkOrderStatus.InProgress, Actor.AssignedTechnician, [WorkOrderAction.Resolve] },
        { WorkOrderStatus.InProgress, Actor.OtherTechnician, [] },
        { WorkOrderStatus.InProgress, Actor.OwningRequester, [] },
        { WorkOrderStatus.InProgress, Actor.OtherRequester, [] },

        { WorkOrderStatus.Resolved, Actor.Manager, [WorkOrderAction.Close] },
        { WorkOrderStatus.Resolved, Actor.AssignedTechnician, [] },
        { WorkOrderStatus.Resolved, Actor.OtherTechnician, [] },
        { WorkOrderStatus.Resolved, Actor.OwningRequester, [WorkOrderAction.Reopen] },
        { WorkOrderStatus.Resolved, Actor.OtherRequester, [] },

        { WorkOrderStatus.Closed, Actor.Manager, [] },
        { WorkOrderStatus.Closed, Actor.AssignedTechnician, [] },
        { WorkOrderStatus.Closed, Actor.OtherTechnician, [] },
        { WorkOrderStatus.Closed, Actor.OwningRequester, [] },
        { WorkOrderStatus.Closed, Actor.OtherRequester, [] },
    };

    [Theory]
    [MemberData(nameof(PermissionTable))]
    public void Allowed_actions_match_the_permission_table(WorkOrderStatus status, Actor actor, WorkOrderAction[] expected)
    {
        var workOrder = WorkOrderIn(status);

        var allowed = WorkOrderWorkflow.AllowedActions(workOrder, Resolve(actor));

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public void Create_always_starts_new_with_normal_priority_for_the_signed_in_requester()
    {
        var result = WorkOrderWorkflow.Create(OwningRequester, Guid.NewGuid(), "  Leaky tap  ", " Drips ", WorkOrderCategory.Plumbing, Created);

        var (workOrder, auditEvent) = result.Value;
        Assert.Equal(WorkOrderStatus.New, workOrder.Status);
        Assert.Equal(WorkOrderPriority.Normal, workOrder.Priority);
        Assert.Equal(OwningRequester.Id, workOrder.RequesterId);
        Assert.Equal(OwningRequester.OrganizationId, workOrder.OrganizationId);
        Assert.Equal("Leaky tap", workOrder.Title);
        Assert.Equal(Created.AddHours(72), workOrder.DueAt);
        Assert.Equal(AuditEventType.Created, auditEvent.Type);
    }

    [Fact]
    public void Technicians_cannot_create_requests()
    {
        var result = WorkOrderWorkflow.Create(AssignedTechnician, Guid.NewGuid(), "Title", "Description", WorkOrderCategory.General, Created);

        Assert.Equal(FailureKind.Forbidden, result.Failure!.Kind);
    }

    [Fact]
    public void Assigning_new_work_moves_it_to_assigned()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.New);

        var result = WorkOrderWorkflow.Assign(workOrder, Manager, Technician(AssignedTechnician), Now);

        Assert.Equal(WorkOrderStatus.Assigned, workOrder.Status);
        Assert.Equal(AssignedTechnician.Id, workOrder.AssignedTechnicianId);
        Assert.Equal(AuditEventType.Assigned, result.Value!.Type);
        Assert.Equal(WorkOrderStatus.New, result.Value.FromStatus);
        Assert.Equal(WorkOrderStatus.Assigned, result.Value.ToStatus);
    }

    [Theory]
    [InlineData(WorkOrderStatus.Assigned)]
    [InlineData(WorkOrderStatus.InProgress)]
    public void Reassigning_keeps_the_current_status(WorkOrderStatus status)
    {
        var workOrder = WorkOrderIn(status);

        var result = WorkOrderWorkflow.Assign(workOrder, Manager, Technician(OtherTechnician), Now);

        Assert.Equal(status, workOrder.Status);
        Assert.Equal(OtherTechnician.Id, workOrder.AssignedTechnicianId);
        Assert.Equal(AuditEventType.Reassigned, result.Value!.Type);
    }

    [Fact]
    public void Assigning_the_same_technician_again_is_rejected()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Assigned);

        var result = WorkOrderWorkflow.Assign(workOrder, Manager, Technician(AssignedTechnician), Now);

        Assert.Equal(FailureKind.Validation, result.Failure!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Resolving_requires_a_note_and_leaves_the_work_order_untouched(string? note)
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.InProgress);

        var result = WorkOrderWorkflow.Resolve(workOrder, AssignedTechnician, note, Now);

        Assert.Equal(FailureKind.Validation, result.Failure!.Kind);
        Assert.Contains("resolutionNote", result.Failure.Errors!.Keys);
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Null(workOrder.ResolvedAt);
    }

    [Fact]
    public void Resolution_note_has_a_maximum_length()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.InProgress);

        var result = WorkOrderWorkflow.Resolve(workOrder, AssignedTechnician, new string('a', 2001), Now);

        Assert.Equal(FailureKind.Validation, result.Failure!.Kind);
    }

    [Fact]
    public void Resolving_records_the_trimmed_note_and_time()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.InProgress);

        var result = WorkOrderWorkflow.Resolve(workOrder, AssignedTechnician, "  Replaced fuse.  ", Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.Resolved, workOrder.Status);
        Assert.Equal("Replaced fuse.", workOrder.ResolutionNote);
        Assert.Equal(Now, workOrder.ResolvedAt);
    }

    [Fact]
    public void Reopening_keeps_the_technician_and_original_deadline()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Resolved);
        var originalDueAt = workOrder.DueAt;

        var result = WorkOrderWorkflow.Reopen(workOrder, OwningRequester, "Still broken", Now.AddDays(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkOrderStatus.InProgress, workOrder.Status);
        Assert.Equal(AssignedTechnician.Id, workOrder.AssignedTechnicianId);
        Assert.Equal(originalDueAt, workOrder.DueAt);
        Assert.Null(workOrder.ResolutionNote);
        Assert.Equal("Still broken", result.Value!.Note);
    }

    [Fact]
    public void Reopening_requires_a_reason()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Resolved);

        var result = WorkOrderWorkflow.Reopen(workOrder, OwningRequester, " ", Now);

        Assert.Equal(FailureKind.Validation, result.Failure!.Kind);
        Assert.Equal(WorkOrderStatus.Resolved, workOrder.Status);
    }

    [Fact]
    public void Wrong_role_is_forbidden_but_wrong_status_is_an_invalid_transition()
    {
        Assert.Equal(FailureKind.Forbidden,
            WorkOrderWorkflow.Close(WorkOrderIn(WorkOrderStatus.Resolved), AssignedTechnician, Now).Failure!.Kind);
        Assert.Equal(FailureKind.InvalidState,
            WorkOrderWorkflow.Close(WorkOrderIn(WorkOrderStatus.InProgress), Manager, Now).Failure!.Kind);
    }

    [Fact]
    public void Actors_from_another_organization_get_not_found()
    {
        var outsider = User(UserRole.Manager) with { OrganizationId = Guid.NewGuid() };

        var result = WorkOrderWorkflow.SetPriority(WorkOrderIn(WorkOrderStatus.New), outsider, WorkOrderPriority.High, Now);

        Assert.Equal(FailureKind.NotFound, result.Failure!.Kind);
    }

    [Fact]
    public void Priority_change_recalculates_the_deadline_from_creation_not_from_now()
    {
        var workOrder = WorkOrderIn(WorkOrderStatus.Assigned);

        WorkOrderWorkflow.SetPriority(workOrder, Manager, WorkOrderPriority.High, Created.AddHours(10));
        Assert.Equal(Created.AddHours(24), workOrder.DueAt);

        WorkOrderWorkflow.SetPriority(workOrder, Manager, WorkOrderPriority.Normal, Created.AddHours(30));
        Assert.Equal(Created.AddHours(72), workOrder.DueAt);
    }

    private static WorkOrder WorkOrderIn(WorkOrderStatus status) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = OrganizationId,
        PropertyId = Guid.NewGuid(),
        RequesterId = OwningRequester.Id,
        AssignedTechnicianId = status == WorkOrderStatus.New ? null : AssignedTechnician.Id,
        Title = "Test",
        Description = "Test",
        Category = WorkOrderCategory.General,
        Priority = WorkOrderPriority.Normal,
        Status = status,
        CreatedAt = Created,
        UpdatedAt = Created,
        DueAt = Created.AddHours(72),
        ResolutionNote = status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed ? "Done" : null,
        ResolvedAt = status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed ? Created.AddHours(1) : null,
        ClosedAt = status == WorkOrderStatus.Closed ? Created.AddHours(2) : null,
    };

    private static CurrentUser Resolve(Actor actor) => actor switch
    {
        Actor.Manager => Manager,
        Actor.AssignedTechnician => AssignedTechnician,
        Actor.OtherTechnician => OtherTechnician,
        Actor.OwningRequester => OwningRequester,
        Actor.OtherRequester => OtherRequester,
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    private static CurrentUser User(UserRole role) =>
        new(Guid.NewGuid(), $"{role}@test.example", role.ToString(), role, OrganizationId, "Test Org");

    private static TechnicianChoice Technician(CurrentUser user) => new(user.Id, user.DisplayName);
}
