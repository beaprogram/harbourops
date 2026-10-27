namespace HarbourOps.Api.Common;

/// <summary>Translates business <see cref="Failure"/>s into consistent HTTP Problem Details.</summary>
public static class ApiResults
{
    public static IResult ToProblem(this Failure failure) => failure.Kind switch
    {
        FailureKind.NotFound => Problem(StatusCodes.Status404NotFound, "Not found", failure.Message, failure.Code ?? "not_found"),
        FailureKind.Forbidden => Problem(StatusCodes.Status403Forbidden, "Not allowed", failure.Message, failure.Code ?? "forbidden"),
        FailureKind.InvalidState => Problem(
            StatusCodes.Status422UnprocessableEntity, "Action not available", failure.Message, failure.Code ?? "invalid_transition"),
        FailureKind.Conflict => Problem(
            StatusCodes.Status409Conflict, "This work order changed", failure.Message, failure.Code ?? "stale_version"),
        FailureKind.PayloadTooLarge => Problem(
            StatusCodes.Status413PayloadTooLarge, "Too large", failure.Message, failure.Code ?? "payload_too_large"),
        FailureKind.UnsupportedMediaType => Problem(
            StatusCodes.Status415UnsupportedMediaType, "Unsupported type", failure.Message, failure.Code ?? "unsupported_media_type"),
        FailureKind.Unavailable => Problem(
            StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable", failure.Message, failure.Code ?? "unavailable"),
        FailureKind.Unauthenticated => Problem(
            StatusCodes.Status401Unauthorized, "Not signed in", failure.Message, failure.Code ?? "unauthenticated"),
        FailureKind.Validation => TypedResults.ValidationProblem(
            failure.Errors ?? new Dictionary<string, string[]>(),
            detail: failure.Message,
            extensions: new Dictionary<string, object?> { ["code"] = failure.Code ?? "validation_failed" }),
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
