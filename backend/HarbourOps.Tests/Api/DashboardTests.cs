using System.Net;
using HarbourOps.Api.Features.Dashboard;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HarbourOps.Tests.Api;

/// <summary>
/// The dashboard must agree with the list it links to. The fake clock is frozen, so the dashboard and
/// the list requests in each test are evaluated at exactly the same "now".
/// </summary>
public sealed class DashboardTests(PostgresFixture fixture) : FakeClockIntegrationTest(fixture)
{
    [Theory]
    [InlineData(DemoAccounts.SamTechnician)]
    [InlineData(DemoAccounts.RileyRequester)]
    public async Task Only_managers_can_load_the_dashboard(string email)
    {
        using var client = await SignInClockedAsync(email);

        var response = await client.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_get_401()
    {
        using var anonymous = ClockedFactory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Every_card_and_workload_number_matches_the_list_it_links_to()
    {
        // Create some deadline pressure: requests that are overdue, due soon, and unassigned.
        using (var riley = await SignInClockedAsync(DemoAccounts.RileyRequester))
        using (var morgan = await SignInClockedAsync(DemoAccounts.MorganManager))
        {
            await AssignAsync(morgan, await CreateRequestAsync(riley, title: "Dashboard check A"), DemoAccounts.JordanId);
            await CreateRequestAsync(riley, title: "Dashboard check B");
        }

        Clock.Advance(TimeSpan.FromHours(60)); // the new Normal requests are now due within 24 h
        using var manager = await SignInClockedAsync(DemoAccounts.MorganManager);

        var dashboard = await manager.GetJsonAsync<DashboardResponse>("/api/dashboard");

        Assert.Equal(Clock.GetUtcNow(), dashboard.AsOf);
        Assert.Equal(["active", "unassigned", "overdue", "dueSoon"], dashboard.Cards.Select(c => c.Key));
        foreach (var card in dashboard.Cards)
        {
            var list = await ListAsync(manager, card.ListQuery);
            Assert.True(card.Count == list.TotalCount, $"{card.Key}: dashboard {card.Count}, list {list.TotalCount}");
        }

        Assert.All(dashboard.Cards, c => Assert.True(c.Count > 0, $"{c.Key} should not be empty in this scenario"));

        foreach (var row in dashboard.Workload)
        {
            Assert.Equal(row.ActiveCount, (await ListAsync(manager, row.ActiveListQuery)).TotalCount);
            Assert.Equal(row.OverdueCount, (await ListAsync(manager, row.OverdueListQuery)).TotalCount);
        }

        var assignedActive = await ListAsync(manager, "status=Assigned&status=InProgress&pageSize=1");
        Assert.Equal(assignedActive.TotalCount, dashboard.Workload.Sum(w => w.ActiveCount));

        var oldestFromList = await ListAsync(manager, dashboard.OverdueListQuery + $"&pageSize={DashboardService.OldestOverdueLimit}");
        Assert.Equal(oldestFromList.Items.Select(i => i.Id), dashboard.OldestOverdue.Select(o => o.Id));
        Assert.Equal(dashboard.OldestOverdue.OrderBy(o => o.DueAt).Select(o => o.Id), dashboard.OldestOverdue.Select(o => o.Id));
    }

    [Fact]
    public async Task Counts_are_scoped_to_the_managers_organization()
    {
        using var morgan = await SignInClockedAsync(DemoAccounts.MorganManager);
        using var dana = await SignInClockedAsync(DemoAccounts.DanaManager);
        var harbourBefore = Card(await morgan.GetJsonAsync<DashboardResponse>("/api/dashboard"), "unassigned");
        var coveBefore = await dana.GetJsonAsync<DashboardResponse>("/api/dashboard");

        using (var riley = await SignInClockedAsync(DemoAccounts.RileyRequester))
        {
            await CreateRequestAsync(riley, title: "Scoped count check");
        }

        var harbourAfter = Card(await morgan.GetJsonAsync<DashboardResponse>("/api/dashboard"), "unassigned");
        var coveAfter = await dana.GetJsonAsync<DashboardResponse>("/api/dashboard");

        Assert.Equal(harbourBefore + 1, harbourAfter);
        Assert.Equal(coveBefore.Cards.Select(c => c.Count), coveAfter.Cards.Select(c => c.Count));
        Assert.DoesNotContain(coveAfter.Workload, w => w.Id == DemoAccounts.SamId || w.Id == DemoAccounts.JordanId);
        Assert.All(coveAfter.OldestOverdue, o => Assert.DoesNotContain("Harbourview", o.PropertyName));
    }

    [Fact]
    public async Task Technicians_with_no_active_work_are_listed_for_assignment_decisions()
    {
        var email = $"tech.{UniqueTag()}@covecommons.example";
        await using (var scope = ClockedFactory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var result = await users.CreateAsync(new ApplicationUser
            {
                Id = Guid.NewGuid(),
                OrganizationId = DemoAccounts.CoveCommonsId,
                UserName = email,
                Email = email,
                DisplayName = "Quinn Newhire",
                Role = UserRole.Technician,
            }, DemoAccounts.Password);
            Assert.True(result.Succeeded);
        }

        using var dana = await SignInClockedAsync(DemoAccounts.DanaManager);
        var dashboard = await dana.GetJsonAsync<DashboardResponse>("/api/dashboard");

        var quinn = Assert.Single(dashboard.Workload, w => w.DisplayName == "Quinn Newhire");
        Assert.Equal(0, quinn.ActiveCount);
        Assert.Equal(0, quinn.OverdueCount);
    }

    private static int Card(DashboardResponse dashboard, string key) => dashboard.Cards.Single(c => c.Key == key).Count;
}
