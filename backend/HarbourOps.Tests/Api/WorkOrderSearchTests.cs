using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Api;

/// <summary>Search, filters, validation, and role restrictions on the work-order list.</summary>
public sealed class WorkOrderSearchTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Search_matches_words_in_title_and_description_with_stemming_and_prefixes()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        var leakInTitle = await CreateRequestAsync(riley, title: $"Leaking pipe {tag}", description: "Under the bathroom sink.");
        var leakInDescription = await CreateRequestAsync(riley, title: $"Window latch {tag}", description: "There is a leak whenever it rains.");
        var radiator = await CreateRequestAsync(riley, title: $"Radiator banging {tag}", description: "Loud at night.");

        var leaks = await ListAsync(morgan, $"q={tag}%20leaking");
        var prefix = await ListAsync(morgan, $"q={tag}%20radia");
        var everything = await ListAsync(morgan, $"q={tag}");

        Assert.Equal([leakInTitle.Id, leakInDescription.Id], leaks.Items.Select(i => i.Id).Order());
        Assert.Equal(2, leaks.TotalCount);
        Assert.Equal([radiator.Id], prefix.Items.Select(i => i.Id));
        Assert.Equal(3, everything.TotalCount);
    }

    [Theory]
    [InlineData("WO-{0}")]
    [InlineData("wo{0}")]
    [InlineData("{0}")]
    public async Task Search_finds_a_work_order_by_its_displayed_reference(string format)
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        var created = await CreateRequestAsync(riley, title: "Reference lookup");

        var result = await ListAsync(riley, $"q={Uri.EscapeDataString(string.Format(format, created.Number))}");

        Assert.Contains(result.Items, i => i.Id == created.Id);
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("&|:*'()<->")]
    public async Task Search_text_without_words_matches_nothing_and_never_errors(string text)
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var result = await ListAsync(morgan, $"q={Uri.EscapeDataString(text)}");

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task Filters_combine_with_and_and_repeated_values_combine_with_or()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var highSamQuayside = await CreateRequestAsync(riley, DemoAccounts.QuaysideLofts, $"Alpha {tag}");
        highSamQuayside = await SetPriorityAsync(morgan, highSamQuayside, "High");
        await AssignAsync(morgan, highSamQuayside, DemoAccounts.SamId);

        var normalSamTidewater = await CreateRequestAsync(riley, DemoAccounts.TidewaterCourt, $"Bravo {tag}");
        await AssignAsync(morgan, normalSamTidewater, DemoAccounts.SamId);

        var highUnassignedQuayside = await CreateRequestAsync(riley, DemoAccounts.QuaysideLofts, $"Charlie {tag}");
        await SetPriorityAsync(morgan, highUnassignedQuayside, "High");

        async Task<Guid[]> Ids(string query) =>
            (await ListAsync(morgan, $"q={tag}&{query}")).Items.Select(i => i.Id).Order().ToArray();

        Assert.Equal([highSamQuayside.Id], await Ids($"priority=High&technicianId={DemoAccounts.SamId}"));
        Assert.Equal([highUnassignedQuayside.Id], await Ids("unassigned=true"));
        Assert.Equal(new[] { highSamQuayside.Id, highUnassignedQuayside.Id }.Order(),
            await Ids($"propertyId={DemoAccounts.QuaysideLofts}&priority=High"));
        Assert.Equal([highUnassignedQuayside.Id], await Ids("status=New"));
        Assert.Equal(new[] { highSamQuayside.Id, normalSamTidewater.Id, highUnassignedQuayside.Id }.Order(),
            await Ids("status=New&status=Assigned"));
        Assert.Empty(await Ids($"status=Resolved&technicianId={DemoAccounts.SamId}"));
    }

    [Fact]
    public async Task Invalid_query_parameters_are_rejected_together_with_field_errors()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.GetAsync(
            $"/api/work-orders?q={new string('a', 101)}&sort=title&direction=up&deadline=late&pageSize=51&page=0" +
            $"&unassigned=true&technicianId={DemoAccounts.SamId}&status=Deleted");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Equal(
            ["deadline", "direction", "page", "pageSize", "q", "sort", "status", "technicianId"],
            problem.Errors!.Keys.Order());
    }

    [Fact]
    public async Task Malformed_ids_in_filters_are_rejected()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.GetAsync("/api/work-orders?propertyId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Another_organizations_records_are_excluded_from_results_and_counts()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var dana = await SignInAsync(DemoAccounts.DanaManager);
        await CreateRequestAsync(riley, title: $"Harbourview only {tag}");

        var bySearch = await ListAsync(dana, $"q={tag}");
        var byForeignProperty = await ListAsync(dana, $"propertyId={DemoAccounts.QuaysideLofts}");
        var byForeignTechnician = await ListAsync(dana, $"technicianId={DemoAccounts.SamId}");

        Assert.Equal(0, bySearch.TotalCount);
        Assert.Equal(0, byForeignProperty.TotalCount);
        Assert.Equal(0, byForeignTechnician.TotalCount);
    }

    [Fact]
    public async Task Role_visibility_is_applied_before_filters_and_counts()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var alex = await SignInAsync(DemoAccounts.AlexRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        using var jordan = await SignInAsync(DemoAccounts.JordanTechnician);
        await AssignAsync(morgan, await CreateRequestAsync(riley, title: $"Riley {tag}"), DemoAccounts.SamId);
        await AssignAsync(morgan, await CreateRequestAsync(alex, title: $"Alex {tag}"), DemoAccounts.SamId);

        var rileysView = await ListAsync(riley, $"q={tag}&technicianId={DemoAccounts.SamId}");
        var jordansView = await ListAsync(jordan, $"q={tag}&technicianId={DemoAccounts.SamId}");
        var managersView = await ListAsync(morgan, $"q={tag}&technicianId={DemoAccounts.SamId}");

        Assert.Equal(1, rileysView.TotalCount);
        Assert.Equal("Riley Chen", Assert.Single(rileysView.Items).RequesterName);
        Assert.Equal(0, jordansView.TotalCount);
        Assert.Equal(2, managersView.TotalCount);
    }

    [Fact]
    public async Task Filter_choices_only_include_what_the_role_may_see()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var jordan = await SignInAsync(DemoAccounts.JordanTechnician);
        using var dana = await SignInAsync(DemoAccounts.DanaManager);

        var managerOptions = await morgan.GetJsonAsync<WorkOrderFilterOptions>("/api/work-orders/filter-options");
        var requesterOptions = await riley.GetJsonAsync<WorkOrderFilterOptions>("/api/work-orders/filter-options");
        var technicianOptions = await jordan.GetJsonAsync<WorkOrderFilterOptions>("/api/work-orders/filter-options");
        var otherManagerOptions = await dana.GetJsonAsync<WorkOrderFilterOptions>("/api/work-orders/filter-options");

        Assert.Equal(new[] { DemoAccounts.JordanId, DemoAccounts.SamId }.Order(), managerOptions.Technicians.Select(t => t.Id).Order());
        Assert.True(managerOptions.CanFilterByTechnician);
        Assert.NotNull(managerOptions.ExportRowLimit);

        // Requesters only see properties and technicians that appear on their own requests.
        var rileysProperties = await QueryDbAsync(db => Task.FromResult(db.WorkOrders
            .Where(w => w.RequesterId == DemoAccounts.RileyId).Select(w => w.PropertyId).Distinct().ToHashSet()));
        Assert.All(requesterOptions.Properties, p => Assert.Contains(p.Id, rileysProperties));
        Assert.DoesNotContain(requesterOptions.Properties, p => p.Id == DemoAccounts.CommonsWalk);
        Assert.Null(requesterOptions.ExportRowLimit);

        Assert.False(technicianOptions.CanFilterByTechnician);
        Assert.Empty(technicianOptions.Technicians);

        Assert.DoesNotContain(otherManagerOptions.Properties, p => p.Id == DemoAccounts.QuaysideLofts);
        Assert.DoesNotContain(otherManagerOptions.Technicians, t => t.Id == DemoAccounts.SamId);
    }
}
