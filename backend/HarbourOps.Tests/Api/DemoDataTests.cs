using System.Net;
using HarbourOps.Api.Features.Demo;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HarbourOps.Tests.Api;

public sealed class DemoDataTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Seeding_again_does_not_duplicate_records()
    {
        var before = await CountAllAsync();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, await CountAllAsync());
    }

    [Fact]
    public async Task Seeded_history_matches_each_work_orders_status()
    {
        var mismatches = await QueryDbAsync(db => db.WorkOrders
            .Where(w => w.Id.ToString().StartsWith("40000000"))
            .Select(w => new
            {
                w.Status,
                LastStatus = db.AuditEvents
                    .Where(e => e.WorkOrderId == w.Id && e.ToStatus != null)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => e.ToStatus)
                    .First(),
            })
            .Where(x => x.LastStatus != x.Status)
            .CountAsync());

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task Demo_accounts_endpoint_is_hidden_when_demo_mode_is_off()
    {
        await using var factory = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?> { ["DemoData:Enabled"] = "false" });
        using var client = factory.CreateApiClient();

        var response = await client.GetAsync("/api/demo/accounts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Demo_accounts_endpoint_lists_accounts_when_demo_mode_is_on()
    {
        using var client = Factory.CreateApiClient();

        var demo = await client.GetJsonAsync<DemoAccountsResponse>("/api/demo/accounts");

        Assert.Equal(8, demo.Accounts.Count);
        Assert.All(demo.Accounts, a => Assert.EndsWith(".example", a.Email));
    }

    private Task<(int, int, int, int, int)> CountAllAsync() => QueryDbAsync(async db => (
        await db.Organizations.CountAsync(),
        await db.Users.CountAsync(),
        await db.Properties.CountAsync(),
        await db.WorkOrders.CountAsync(),
        await db.AuditEvents.CountAsync()));
}
