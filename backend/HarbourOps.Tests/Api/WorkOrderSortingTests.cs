using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Sorting must be deterministic even when every sort value ties. The fake clock is frozen, so
/// requests created here share identical CreatedAt, UpdatedAt, and DueAt values.
/// </summary>
public sealed class WorkOrderSortingTests(PostgresFixture fixture) : FakeClockIntegrationTest(fixture)
{
    [Fact]
    public async Task Paging_through_tied_rows_returns_every_row_exactly_once_in_a_stable_order()
    {
        var tag = UniqueTag();
        using var riley = await SignInClockedAsync(DemoAccounts.RileyRequester);
        for (var i = 0; i < 5; i++)
        {
            await CreateRequestAsync(riley, title: $"Tied {i} {tag}");
        }

        var allAtOnce = (await ListAsync(riley, $"q={tag}&pageSize=50")).Items.Select(i => i.Id).ToList();
        var paged = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await ListAsync(riley, $"q={tag}&pageSize=2&page={page}");
            Assert.Equal(5, result.TotalCount);
            Assert.Equal(3, result.TotalPages);
            paged.AddRange(result.Items.Select(i => i.Id));
        }

        // Ties on CreatedAt are broken by Id (descending for the default newest-first sort).
        var expected = await QueryDbAsync(db => db.WorkOrders
            .Where(w => allAtOnce.Contains(w.Id)).OrderByDescending(w => w.Id).Select(w => w.Id).ToListAsync());
        Assert.Equal(expected, allAtOnce);
        Assert.Equal(allAtOnce, paged);
        Assert.Equal(allAtOnce, (await ListAsync(riley, $"q={tag}&pageSize=50")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Priority_sort_puts_high_first_then_earliest_due_then_id()
    {
        var tag = UniqueTag();
        using var riley = await SignInClockedAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInClockedAsync(DemoAccounts.MorganManager);
        var normalA = await CreateRequestAsync(riley, title: $"Normal A {tag}");
        var normalB = await CreateRequestAsync(riley, title: $"Normal B {tag}");
        var high = await SetPriorityAsync(morgan, await CreateRequestAsync(riley, title: $"High {tag}"), "High");
        var normalsById = await QueryDbAsync(db => db.WorkOrders
            .Where(w => w.Id == normalA.Id || w.Id == normalB.Id).OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());

        var highestFirst = (await ListAsync(morgan, $"q={tag}&sort=priority")).Items.Select(i => i.Id);
        var lowestFirst = (await ListAsync(morgan, $"q={tag}&sort=priority&direction=asc")).Items.Select(i => i.Id);
        var dueSoonestFirst = (await ListAsync(morgan, $"q={tag}&sort=due")).Items.Select(i => i.Id);

        Assert.Equal([high.Id, .. normalsById], highestFirst);
        Assert.Equal([.. normalsById, high.Id], lowestFirst);
        // High is due after 24 h, Normal after 72 h, so "due" (ascending by default) also starts with High.
        Assert.Equal([high.Id, .. normalsById], dueSoonestFirst);
    }

    [Fact]
    public async Task Recently_updated_sort_follows_workflow_changes()
    {
        var tag = UniqueTag();
        using var riley = await SignInClockedAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInClockedAsync(DemoAccounts.MorganManager);
        var older = await CreateRequestAsync(riley, title: $"Older {tag}");
        Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await CreateRequestAsync(riley, title: $"Newer {tag}");
        Clock.Advance(TimeSpan.FromMinutes(1));
        await AssignAsync(morgan, older, DemoAccounts.SamId);

        var byCreated = (await ListAsync(morgan, $"q={tag}")).Items.Select(i => i.Id);
        var byUpdated = (await ListAsync(morgan, $"q={tag}&sort=updated")).Items.Select(i => i.Id);

        Assert.Equal([newer.Id, older.Id], byCreated);
        Assert.Equal([older.Id, newer.Id], byUpdated);
    }
}
