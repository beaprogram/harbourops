namespace HarbourOps.Api.Common;

/// <summary>Collects field-level validation messages, keyed by the JSON property name.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
    }

    /// <summary>Requires non-blank text no longer than <paramref name="maxLength"/>.</summary>
    public void RequireText(string field, string? value, int maxLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, $"{label} is required.");
        }
        else if (value.Trim().Length > maxLength)
        {
            Add(field, $"{label} must be {maxLength} characters or fewer.");
        }
    }

    public Failure ToFailure() =>
        Failure.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}
