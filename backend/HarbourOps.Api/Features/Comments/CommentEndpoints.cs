using HarbourOps.Api.Common;

namespace HarbourOps.Api.Features.Comments;

public static class CommentEndpoints
{
    public static IEndpointRouteBuilder MapCommentEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/work-orders/{workOrderId:guid}/comments")
            .WithTags("Comments")
            .RequireAuthorization();

        group.MapGet("/", async (Guid workOrderId, int? limit, Guid? before, CurrentUser user, CommentService service, CancellationToken ct) =>
            {
                var pageSize = limit ?? CommentService.DefaultPageSize;
                if (pageSize is < 1 or > CommentService.MaxPageSize)
                {
                    return Failure.Validation("limit", $"Limit must be between 1 and {CommentService.MaxPageSize}.").ToProblem();
                }

                return (await service.ListAsync(user, workOrderId, pageSize, before, ct)).ToOk();
            })
            .WithName("ListComments")
            .WithSummary("Comments on a work order, newest page first; pass `before` to load older comments.")
            .Produces<CommentPage>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (Guid workOrderId, CreateCommentRequest request, CurrentUser user, CommentService service, CancellationToken ct) =>
            {
                var result = await service.AddAsync(user, workOrderId, request, ct);
                return result.IsSuccess
                    ? TypedResults.Created($"/api/work-orders/{workOrderId}/comments/{result.Value!.Id}", result.Value)
                    : result.Failure!.ToProblem();
            })
            .WithName("AddComment")
            .WithSummary("Add a plain-text comment. Rejected with 422 once the work order is closed.")
            .Produces<CommentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }
}
