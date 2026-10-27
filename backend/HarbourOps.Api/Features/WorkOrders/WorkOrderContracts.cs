using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Users;

namespace HarbourOps.Api.Features.WorkOrders;

// API contracts. These records are what the frontend sees; persistence entities never leave the server.
// Request fields are nullable so a missing value produces a clear validation message instead of a
// generic JSON binding error. Note what is *not* here: no OrganizationId, RequesterId, Status, or
// role. The server derives those from the authenticated user and the workflow rules.

public sealed record CreateWorkOrderRequest(
    Guid? PropertyId,
    string? Title,
    string? Description,
    WorkOrderCategory? Category)
{
    public ValidationErrors Validate()
    {
        var errors = new ValidationErrors();
        if (PropertyId is null || PropertyId == Guid.Empty)
        {
            errors.Add("propertyId", "Choose a property.");
        }

        errors.RequireText("title", Title, WorkOrderConfiguration.TitleMaxLength, "Title");
        errors.RequireText("description", Description, WorkOrderConfiguration.DescriptionMaxLength, "Description");
        if (Category is null)
        {
            errors.Add("category", "Choose a category.");
        }

        return errors;
    }
}

/// <summary>Every command carries the <c>version</c> the client last saw, for optimistic concurrency.</summary>
public interface IVersionedRequest
{
    uint? Version { get; }
}

public sealed record SetPriorityRequest(WorkOrderPriority? Priority, uint? Version) : IVersionedRequest;

public sealed record AssignTechnicianRequest(Guid? TechnicianId, uint? Version) : IVersionedRequest;

public sealed record WorkOrderCommandRequest(uint? Version) : IVersionedRequest;

public sealed record ResolveWorkOrderRequest(string? ResolutionNote, uint? Version) : IVersionedRequest;

public sealed record ReopenWorkOrderRequest(string? Reason, uint? Version) : IVersionedRequest;

public sealed record PersonSummary(Guid Id, string DisplayName);

public sealed record PropertySummary(Guid Id, string Name, string AddressLine, string Community);

public sealed record WorkOrderListItem(
    Guid Id,
    long Number,
    string Title,
    WorkOrderStatus Status,
    WorkOrderPriority Priority,
    WorkOrderCategory Category,
    string PropertyName,
    string RequesterName,
    string? AssignedTechnicianName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DueAt,
    bool IsOverdue,
    bool IsDueSoon);

/// <summary>
/// One page of results. <c>AsOf</c> is the single "now" used for every deadline flag in the response,
/// so the client can show relative times ("due in 3 h") that agree with the flags.
/// </summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages, DateTimeOffset AsOf);

public sealed record FilterChoice(Guid Id, string Name);

/// <summary>
/// Choices for the list's property and technician filters, limited to what the caller's role may see.
/// <c>ExportRowLimit</c> is only sent to managers, who are the only role allowed to export.
/// </summary>
public sealed record WorkOrderFilterOptions(
    IReadOnlyList<FilterChoice> Properties,
    IReadOnlyList<FilterChoice> Technicians,
    bool CanFilterByTechnician,
    int? ExportRowLimit);

public sealed record WorkOrderDetailResponse(
    Guid Id,
    long Number,
    string Title,
    string Description,
    WorkOrderCategory Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    PropertySummary Property,
    PersonSummary Requester,
    PersonSummary? AssignedTechnician,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DueAt,
    bool IsOverdue,
    bool IsDueSoon,
    DateTimeOffset AsOf,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    uint Version,
    IReadOnlyList<WorkOrderAction> AllowedActions);

public sealed record ActivityEventResponse(
    Guid Id,
    AuditEventType Type,
    WorkOrderStatus? FromStatus,
    WorkOrderStatus? ToStatus,
    string Summary,
    string? Note,
    PersonSummary Actor,
    UserRole ActorRole,
    DateTimeOffset OccurredAt);
