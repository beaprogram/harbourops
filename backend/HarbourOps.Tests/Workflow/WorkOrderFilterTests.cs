using HarbourOps.Api.Features.WorkOrders;
using Microsoft.AspNetCore.WebUtilities;

namespace HarbourOps.Tests.Workflow;

/// <summary>Parsing and round-tripping list filters without HTTP or a database.</summary>
public sealed class WorkOrderFilterTests
{
    [Fact]
    public void Search_text_only_ever_produces_letter_and_digit_prefix_terms()
    {
        var search = WorkOrderSearch.Parse("radia & !leak:* | 'DROP' (pipe)");

        Assert.Equal("radia:* & leak:* & drop:* & pipe:*", search.TsQuery);
        Assert.Null(search.Number);
    }

    [Theory]
    [InlineData("WO-1042", 1042L)]
    [InlineData("wo 7", 7L)]
    [InlineData("#15", 15L)]
    [InlineData("1042", 1042L)]
    [InlineData("WO-12 radiator", null)]
    public void Work_order_references_are_recognised_only_when_they_are_the_whole_query(string text, long? expected)
    {
        Assert.Equal(expected, WorkOrderSearch.Parse(text).Number);
    }

    [Fact]
    public void Defaults_are_newest_first_and_each_sort_has_a_natural_direction()
    {
        var defaults = Parse("");
        var due = Parse("sort=due");

        Assert.Equal(WorkOrderSortField.Created, defaults.Sort);
        Assert.True(defaults.Descending);
        Assert.False(due.Descending);
    }

    [Fact]
    public void Query_string_round_trips_so_dashboard_links_reproduce_the_counted_filter()
    {
        var original = Parse(
            "q=leak%20WO&status=New&status=Assigned&priority=High&propertyId=30000000-0000-4000-8000-000000000011" +
            "&deadline=dueSoon&sort=priority&direction=asc");

        var roundTripped = Parse(original.ToQueryString());

        Assert.Equal(original.ToQueryString(), roundTripped.ToQueryString());
        Assert.Equal(original.Statuses, roundTripped.Statuses);
        Assert.Equal(DeadlineFilter.DueSoon, roundTripped.Deadline);
        Assert.Equal(WorkOrderSortField.Priority, roundTripped.Sort);
        Assert.False(roundTripped.Descending);
    }

    [Fact]
    public void Unknown_sort_fields_and_directions_are_rejected_not_ignored()
    {
        var result = WorkOrderFilter.TryParse(Parameters("sort=Title;DROP&direction=sideways"));

        Assert.Equal(["direction", "sort"], result.Failure!.Errors!.Keys.Order());
    }

    private static WorkOrderFilter Parse(string query)
    {
        var result = WorkOrderFilter.TryParse(Parameters(query));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value!;
    }

    /// <summary>Builds the bound parameters the way ASP.NET Core would from a query string.</summary>
    private static WorkOrderListParameters Parameters(string query)
    {
        var q = QueryHelpers.ParseQuery(query.Length > 0 ? "?" + query : "");
        string? One(string key) => q.TryGetValue(key, out var v) ? v.ToString() : null;
        string[]? Many(string key) => q.TryGetValue(key, out var v) ? v.Select(x => x ?? "").ToArray() : null;
        return new WorkOrderListParameters(
            One("q"), Many("status"), Many("priority"),
            One("propertyId") is { } p ? Guid.Parse(p) : null,
            One("technicianId") is { } t ? Guid.Parse(t) : null,
            One("unassigned") is { } u ? bool.Parse(u) : null,
            One("deadline"), One("sort"), One("direction"), null, null);
    }
}
