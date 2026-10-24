namespace HarbourOps.Api.Common;

/// <summary>Translates business <see cref="Failure"/>s into consistent HTTP Problem Details.</summary>
public static class ApiResults
{
    public static IResult ToProblem(this Failure failure) => failure.Kind switch
    {
        FailureKind.NotFound => Problem(StatusCodes.Status404NotFound, "Not found", failure.Message, "not_found"),
        FailureKind.Forbidden => Problem(StatusCodes.Status403Forbidden, "Not allowed", failure.Message, "forbidden"),
        FailureKind.InvalidState => Problem(
            StatusCodes.Status422UnprocessableEntity, "Action not available", failure.Message, "invalid_transition"),
        FailureKind.Conflict => Problem(
            StatusCodes.Status409Conflict, "This work order changed", failure.Message, "stale_version"),
        FailureKind.Validation => TypedResults.ValidationProblem(
            failure.Errors ?? new Dictionary<string, string[]>(),
            detail: failure.Message,
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" }),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    public static IResult ToOk<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Failure!.ToProblem();

    private static IResult Problem(int status, string title, string detail, string code) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>Documents the error responses every work-order command can return.</summary>
    public static RouteHandlerBuilder ProducesCommandProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
