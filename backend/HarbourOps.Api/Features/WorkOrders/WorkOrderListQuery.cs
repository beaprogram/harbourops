using System.Globalization;
using System.Text.RegularExpressions;
using HarbourOps.Api.Common;
using Microsoft.AspNetCore.Http.Extensions;

namespace HarbourOps.Api.Features.WorkOrders;

public enum WorkOrderSortField
{
    Created,
    Updated,
    Due,
    Priority,
}

public enum DeadlineFilter
{
    Overdue,
    DueSoon,
}

/// <summary>
/// Raw query-string values for the work-order list and CSV export, exactly as the browser sends them.
/// Everything is optional and unvalidated here; <see cref="WorkOrderFilter.TryParse"/> turns it into a
/// validated filter. Names match the frontend URL so a list URL can be passed straight to the API.
/// </summary>
public sealed record WorkOrderListParameters(
    string? Q,
    string[]? Status,
    string[]? Priority,
    Guid? PropertyId,
    Guid? TechnicianId,
    bool? Unassigned,
    string? Deadline,
    string? Sort,
    string? Direction,
    int? Page,
    int? PageSize);

/// <summary>
/// Parsed search text. A query like "WO-12" or "12" also matches the displayed work-order number;
/// words become a prefix full-text query ("radia" finds "radiator", "leaking" finds "leak").
/// </summary>
public sealed partial record WorkOrderSearch(string Text, string? TsQuery, long? Number)
{
    public const int MaxLength = 100;
    private const int MaxTerms = 8;

    public static WorkOrderSearch Parse(string text)
    {
        var identifier = IdentifierPattern().Match(text);
        long? number = identifier.Success && long.TryParse(identifier.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        // Only letters and digits reach to_tsquery, so user input can never inject tsquery operators.
        var terms = WordPattern().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .Take(MaxTerms)
            .Select(term => term + ":*")
            .ToArray();

        return new WorkOrderSearch(text, terms.Length > 0 ? string.Join(" & ", terms) : null, number);
    }

    [GeneratedRegex(@"^(?:WO[\s-]?)?#?(\d{1,15})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}

/// <summary>
/// A validated list filter. The list endpoint, the CSV export, and the dashboard all apply filters
/// through this one type (see <see cref="WorkOrderQueries.ApplyFilter"/>), so a dashboard count and the
/// list it links to use the same definition (they match for the same data and the same "now").
/// </summary>
public sealed record WorkOrderFilter
{
    public const int MaxPage = 10_000;

    public WorkOrderSearch? Search { get; init; }
    public IReadOnlyList<WorkOrderStatus> Statuses { get; init; } = [];
    public IReadOnlyList<WorkOrderPriority> Priorities { get; init; } = [];
    public Guid? PropertyId { get; init; }
    public Guid? TechnicianId { get; init; }
    public bool Unassigned { get; init; }
    public DeadlineFilter? Deadline { get; init; }
    public WorkOrderSortField Sort { get; init; } = WorkOrderSortField.Created;
    public bool Descending { get; init; } = true;

    public static readonly IReadOnlyList<WorkOrderStatus> ActiveStatuses =
        [WorkOrderStatus.New, WorkOrderStatus.Assigned, WorkOrderStatus.InProgress];

    /// <summary>Each sort has a natural default direction when the caller doesn't choose one.</summary>
    public static bool DefaultDescending(WorkOrderSortField sort) => sort switch
    {
        WorkOrderSortField.Due => false,      // soonest due first
        _ => true,                            // newest / most recently updated / highest priority first
    };

    /// <summary>
    /// Validates every value against an explicit allowlist. Unknown sort fields, directions,
    /// statuses, or deadline names are rejected rather than ignored, so a typo never silently
    /// returns unfiltered data.
    /// </summary>
    public static Result<WorkOrderFilter> TryParse(WorkOrderListParameters p)
    {
        var errors = new ValidationErrors();

        WorkOrderSearch? search = null;
        var text = p.Q?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (text.Length > WorkOrderSearch.MaxLength)
            {
                errors.Add("q", $"Search text must be {WorkOrderSearch.MaxLength} characters or fewer.");
            }
            else
            {
                search = WorkOrderSearch.Parse(text);
            }
        }

        var statuses = ParseNames<WorkOrderStatus>(p.Status, "status", "status", errors);
        var priorities = ParseNames<WorkOrderPriority>(p.Priority, "priority", "priority", errors);

        if (p.Unassigned == true && p.TechnicianId is not null)
        {
            errors.Add("technicianId", "Choose either a technician or unassigned work, not both.");
        }

        DeadlineFilter? deadline = p.Deadline switch
        {
            null or "" => null,
            "overdue" => DeadlineFilter.Overdue,
            "dueSoon" => DeadlineFilter.DueSoon,
            _ => Invalid<DeadlineFilter>(errors, "deadline", "Deadline must be 'overdue' or 'dueSoon'."),
        };

        var sort = p.Sort switch
        {
            null or "" or "created" => WorkOrderSortField.Created,
            "updated" => WorkOrderSortField.Updated,
            "due" => WorkOrderSortField.Due,
            "priority" => WorkOrderSortField.Priority,
            _ => Invalid<WorkOrderSortField>(errors, "sort", "Sort must be one of: created, updated, due, priority."),
        };

        var descending = p.Direction switch
        {
            null or "" => DefaultDescending(sort),
            "desc" => true,
            "asc" => false,
            _ => Invalid<bool>(errors, "direction", "Direction must be 'asc' or 'desc'."),
        };

        if (errors.HasErrors)
        {
            return errors.ToFailure();
        }

        return new WorkOrderFilter
        {
            Search = search,
            Statuses = statuses,
            Priorities = priorities,
            PropertyId = p.PropertyId,
            TechnicianId = p.TechnicianId,
            Unassigned = p.Unassigned == true,
            Deadline = deadline,
            Sort = sort,
            Descending = descending,
        };
    }

    /// <summary>Validates paging for the list endpoint (the export ignores paging).</summary>
    public static Result<(int Page, int PageSize)> TryParsePaging(WorkOrderListParameters p, int defaultPageSize, int maxPageSize)
    {
        var errors = new ValidationErrors();
        var page = p.Page ?? 1;
        var pageSize = p.PageSize ?? defaultPageSize;
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"Page must be between 1 and {MaxPage}.");
        }

        if (pageSize < 1 || pageSize > maxPageSize)
        {
            errors.Add("pageSize", $"Page size must be between 1 and {maxPageSize}.");
        }

        return errors.HasErrors ? errors.ToFailure() : (page, pageSize);
    }

    /// <summary>
    /// The URL query string (without "?") that reproduces this filter. The dashboard returns it with
    /// each count so the card links to exactly the list it counted.
    /// </summary>
    public string ToQueryString()
    {
        var query = new QueryBuilder();
        if (Search is not null) query.Add("q", Search.Text);
        foreach (var status in Statuses) query.Add("status", status.ToString());
        foreach (var priority in Priorities) query.Add("priority", priority.ToString());
        if (PropertyId is { } propertyId) query.Add("propertyId", propertyId.ToString());
        if (TechnicianId is { } technicianId) query.Add("technicianId", technicianId.ToString());
        if (Unassigned) query.Add("unassigned", "true");
        if (Deadline is { } deadline) query.Add("deadline", deadline == DeadlineFilter.Overdue ? "overdue" : "dueSoon");
        if (Sort != WorkOrderSortField.Created || !Descending)
        {
            query.Add("sort", Sort.ToString().ToLowerInvariant());
            query.Add("direction", Descending ? "desc" : "asc");
        }

        return query.ToQueryString().Value?.TrimStart('?') ?? string.Empty;
    }

    private static IReadOnlyList<T> ParseNames<T>(string[]? values, string field, string label, ValidationErrors errors)
        where T : struct, Enum
    {
        var parsed = new List<T>();
        foreach (var value in values ?? [])
        {
            // Enum.TryParse also accepts numbers like "7", so require a defined name.
            if (Enum.TryParse<T>(value, ignoreCase: true, out var item) && Enum.IsDefined(item)
                && !int.TryParse(value, out _))
            {
                if (!parsed.Contains(item)) parsed.Add(item);
            }
            else
            {
                errors.Add(field, $"'{value}' is not a valid {label}.");
            }
        }

        return parsed;
    }

    private static T Invalid<T>(ValidationErrors errors, string field, string message)
    {
        errors.Add(field, message);
        return default!;
    }
}
