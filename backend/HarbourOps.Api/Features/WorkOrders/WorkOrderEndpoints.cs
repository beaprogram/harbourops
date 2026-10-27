using HarbourOps.Api.Common;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.WorkOrders.Export;
using Microsoft.Extensions.Options;

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
            .WithSummary("Search, filter, sort, and page the work orders visible to the current user.")
            .Produces<PagedResponse<WorkOrderListItem>>()
            .ProducesValidationProblem();

        group.MapGet("/export", async ([AsParameters] WorkOrderListParameters parameters, CurrentUser user,
                WorkOrderExportService exporter, DemoQuotas demoQuotas, IOptions<ExportOptions> export, CancellationToken ct) =>
            {
                // Same filter parsing as the list; paging parameters are ignored because the export
                // includes every matching row (up to the configured limit).
                var filter = WorkOrderFilter.TryParse(parameters);
                if (!filter.IsSuccess)
                {
                    return filter.Failure!.ToProblem();
                }

                if (user.IsManager && await demoQuotas.TryUseAsync(user, DemoQuota.Export, ct) is { } limited)
                {
                    return limited.ToProblem();
                }

                return await exporter.ExportAsync(user, filter.Value!, export.Value.MaxRows, ct);
            })
            .WithName("ExportWorkOrders")
            .WithSummary("Manager: download every matching work order as CSV (same filters and order as the list).")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/filter-options", async (CurrentUser user, WorkOrderService service, IOptions<ExportOptions> export, CancellationToken ct) =>
                TypedResults.Ok(await service.GetFilterOptionsAsync(user, export.Value.MaxRows, ct)))
            .WithName("GetWorkOrderFilterOptions")
            .WithSummary("Property and technician choices for the list filters, limited to what the role may see.")
            .Produces<WorkOrderFilterOptions>();

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
        [AsParameters] WorkOrderListParameters parameters, CurrentUser user, WorkOrderService service, CancellationToken ct)
    {
        var filter = WorkOrderFilter.TryParse(parameters);
        var paging = WorkOrderFilter.TryParsePaging(parameters, DefaultPageSize, MaxPageSize);
        if (MergeFailures(filter.Failure, paging.Failure) is { } failure)
        {
            return failure.ToProblem();
        }

        var (page, pageSize) = paging.Value;
        return TypedResults.Ok(await service.ListAsync(user, filter.Value!, page, pageSize, ct));
    }

    /// <summary>Reports filter and paging problems together, so the client sees every invalid field at once.</summary>
    internal static Failure? MergeFailures(params Failure?[] failures)
    {
        var present = failures.OfType<Failure>().ToList();
        if (present.Count == 0)
        {
            return null;
        }

        var errors = present
            .SelectMany(f => f.Errors ?? new Dictionary<string, string[]>())
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Value).ToArray());
        return Failure.Validation(errors);
    }
}
