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
}

/// <summary>An expected business failure. Unexpected failures are exceptions instead.</summary>
public sealed record Failure(FailureKind Kind, string Message, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static Failure NotFound(string message) => new(FailureKind.NotFound, message);
    public static Failure Forbidden(string message) => new(FailureKind.Forbidden, message);
    public static Failure InvalidState(string message) => new(FailureKind.InvalidState, message);
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
