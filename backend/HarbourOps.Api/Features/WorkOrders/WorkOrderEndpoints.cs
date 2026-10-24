using HarbourOps.Api.Common;

namespace HarbourOps.Api.Features.WorkOrders;

/// <summary>
/// HTTP layer for work orders. Each workflow step is its own explicit endpoint
/// (POST /assign, /start, /resolve, ...) rather than a generic "PATCH status" endpoint, so every
/// transition has its own rule, request contract, permission check, and audit event.
/// </summary>
public static class WorkOrderEndpoints
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public static IEndpointRouteBuilder MapWorkOrderEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/work-orders")
            .WithTags("Work orders")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .WithName("ListWorkOrders")
            .WithSummary("List work orders visible to the current user, newest first.")
            .Produces<PagedResponse<WorkOrderListItem>>()
            .ProducesValidationProblem();

        group.MapPost("/", async (CreateWorkOrderRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(user, request, ct);
                return result.IsSuccess
                    ? TypedResults.Created($"/api/work-orders/{result.Value!.Id}", result.Value)
                    : result.Failure!.ToProblem();
            })
            .WithName("CreateWorkOrder")
            .WithSummary("Submit a maintenance request. The requester is always the signed-in user.")
            .Produces<WorkOrderDetailResponse>(StatusCodes.Status201Created)
            .ProducesCommandProblems();

        group.MapGet("/{id:guid}", async (Guid id, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.GetAsync(user, id, ct)).ToOk())
            .WithName("GetWorkOrder")
            .Produces<WorkOrderDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/activity", async (Guid id, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.GetActivityAsync(user, id, ct)).ToOk())
            .WithName("GetWorkOrderActivity")
            .WithSummary("Activity history for one work order, oldest first.")
            .Produces<ActivityEventResponse[]>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/priority", async (Guid id, SetPriorityRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.SetPriorityAsync(user, id, request, ct)).ToOk())
            .WithName("SetWorkOrderPriority")
            .WithSummary("Manager: change priority. The due date is recalculated from the creation time.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        group.MapPost("/{id:guid}/assign", async (Guid id, AssignTechnicianRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.AssignAsync(user, id, request, ct)).ToOk())
            .WithName("AssignWorkOrder")
            .WithSummary("Manager: assign or reassign a technician from the same organization.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        group.MapPost("/{id:guid}/start", async (Guid id, WorkOrderCommandRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.StartAsync(user, id, request, ct)).ToOk())
            .WithName("StartWorkOrder")
            .WithSummary("Assigned technician: move Assigned work to InProgress.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        group.MapPost("/{id:guid}/resolve", async (Guid id, ResolveWorkOrderRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.ResolveAsync(user, id, request, ct)).ToOk())
            .WithName("ResolveWorkOrder")
            .WithSummary("Assigned technician: move InProgress work to Resolved with a resolution note.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        group.MapPost("/{id:guid}/close", async (Guid id, WorkOrderCommandRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.CloseAsync(user, id, request, ct)).ToOk())
            .WithName("CloseWorkOrder")
            .WithSummary("Manager: close Resolved work. Closed work orders are read-only.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        group.MapPost("/{id:guid}/reopen", async (Guid id, ReopenWorkOrderRequest request, CurrentUser user, WorkOrderService service, CancellationToken ct) =>
                (await service.ReopenAsync(user, id, request, ct)).ToOk())
            .WithName("ReopenWorkOrder")
            .WithSummary("Requester: reopen their own Resolved request with a reason. Keeps the technician.")
            .Produces<WorkOrderDetailResponse>()
            .ProducesCommandProblems();

        return api;
    }

    private static async Task<IResult> ListAsync(
        int? page, int? pageSize, string[]? status, CurrentUser user, WorkOrderService service, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var pageNumber = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (pageNumber < 1)
        {
            errors.Add("page", "Page must be 1 or greater.");
        }

        if (size is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"Page size must be between 1 and {MaxPageSize}.");
        }

        var statuses = new List<WorkOrderStatus>();
        foreach (var value in status ?? [])
        {
            // Enum.TryParse also accepts numbers like "7", so require a defined name.
            if (Enum.TryParse<WorkOrderStatus>(value, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
            {
                statuses.Add(parsed);
            }
            else
            {
                errors.Add("status", $"'{value}' is not a valid status.");
            }
        }

        if (errors.HasErrors)
        {
            return errors.ToFailure().ToProblem();
        }

        var result = await service.ListAsync(user, new WorkOrderListQuery(pageNumber, size, statuses), ct);
        return TypedResults.Ok(result);
    }
}
