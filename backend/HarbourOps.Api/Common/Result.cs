namespace HarbourOps.Api.Common;

public enum FailureKind
{
    /// <summary>The record does not exist or the caller may not see it (404).</summary>
    NotFound,

    /// <summary>The caller can see the record but their role or relationship does not allow the action (403).</summary>
    Forbidden,

    /// <summary>Input is missing or invalid (400 with field errors).</summary>
    Validation,

    /// <summary>The action is not allowed from the work order's current status (422).</summary>
    InvalidState,

    /// <summary>The caller's copy is out of date; someone else changed the record first (409).</summary>
    Conflict,

    /// <summary>The request body is larger than allowed (413).</summary>
    PayloadTooLarge,

    /// <summary>The content is not a supported type, whatever its name or declared Content-Type says (415).</summary>
    UnsupportedMediaType,

    /// <summary>A dependency such as photo storage is temporarily unavailable (503).</summary>
    Unavailable,

    /// <summary>The caller's session no longer authorizes this, for example an ended demo sandbox (401).</summary>
    Unauthenticated,
}

/// <summary>
/// An expected business failure. Unexpected failures are exceptions instead.
/// <paramref name="Code"/> overrides the default machine-readable Problem Details code for the kind
/// (for example "work_order_closed" instead of "invalid_transition").
/// </summary>
public sealed record Failure(
    FailureKind Kind, string Message, IReadOnlyDictionary<string, string[]>? Errors = null, string? Code = null)
{
    public static Failure NotFound(string message) => new(FailureKind.NotFound, message);
    public static Failure Forbidden(string message) => new(FailureKind.Forbidden, message);
    public static Failure InvalidState(string message, string? code = null) => new(FailureKind.InvalidState, message, Code: code);
    public static Failure Conflict(string message) => new(FailureKind.Conflict, message);

    public static Failure Validation(string field, string message) =>
        new(FailureKind.Validation, message, new Dictionary<string, string[]> { [field] = [message] });

    public static Failure Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(FailureKind.Validation, "One or more fields need attention.", errors);
}

/// <summary>Either a value or a <see cref="Failure"/>. Keeps expected errors out of exceptions.</summary>
public readonly record struct Result<T>
{
    private Result(T? value, Failure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }
    public Failure? Failure { get; }
    public bool IsSuccess => Failure is null;

    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(Failure failure) => new(default, failure);
}
