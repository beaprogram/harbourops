using System.Net;
using System.Text;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

public sealed class ExportTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    private static async Task<(HttpResponseMessage Response, List<string[]> Rows)> ExportAsync(ApiClient client, string query)
    {
        var response = await client.GetAsync($"/api/work-orders/export?{query}");
        var rows = response.IsSuccessStatusCode
            ? CsvReader.Parse(Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).TrimStart('﻿'))
            : [];
        return (response, rows);
    }

    [Fact]
    public async Task Export_uses_the_same_filters_and_order_as_the_list_without_paging()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        for (var i = 0; i < 3; i++)
        {
            var created = await CreateRequestAsync(riley, title: $"Export row {i} {tag}");
            if (i == 1) await SetPriorityAsync(morgan, created, "High");
        }

        var list = await ListAsync(morgan, $"q={tag}&sort=priority&pageSize=50");
        var (response, rows) = await ExportAsync(morgan, $"q={tag}&sort=priority&page=7&pageSize=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(list.Items.Select(i => $"WO-{i.Number}"), rows.Skip(1).Select(r => r[0]));
        Assert.Equal("3", response.Headers.GetValues("X-Total-Count").Single());
    }

    [Fact]
    public async Task Export_is_a_safe_utf8_csv_download_with_time_zone_labels()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.GetAsync("/api/work-orders/export?pageSize=1");
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var rows = CsvReader.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Matches(@"^harbourops-work-orders-\d{8}-\d{4}\.csv$", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // UTF-8 byte-order mark for Excel

        var header = rows[0];
        Assert.Equal("Reference", header[0]);
        Assert.Contains("Created (America/Halifax)", header);
        Assert.Contains("Due (America/Halifax)", header);
        var created = rows[1][Array.IndexOf(header, "Created (America/Halifax)")];
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}-0[34]:00$", created);
    }

    [Fact]
    public async Task Malicious_and_awkward_text_round_trips_safely()
    {
        var tag = UniqueTag();
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);
        var maliciousTitle = $"=HYPERLINK(\"http://evil.example\",\"Click\") {tag}";
        var description = "Line one, with a comma\nLine \"two\" with quotes\nCafé déjà vu ✓ 🚰";
        await CreateRequestAsync(riley, title: maliciousTitle, description: description);

        var (_, rows) = await ExportAsync(morgan, $"q={tag}");

        var header = rows[0];
        var row = Assert.Single(rows.Skip(1));
        Assert.Equal(header.Length, row.Length);
        // Formula neutralised: a spreadsheet shows it as text instead of evaluating it.
        Assert.Equal("'" + maliciousTitle, row[Array.IndexOf(header, "Title")]);
        // Ordinary escaping keeps commas, quotes, line breaks, and Unicode intact in one cell.
        Assert.Equal(description, row[Array.IndexOf(header, "Description")]);
    }

    [Theory]
    [InlineData(DemoAccounts.SamTechnician)]
    [InlineData(DemoAccounts.RileyRequester)]
    public async Task Only_managers_can_export(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.GetAsync("/api/work-orders/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await ApiClient.ReadProblemAsync(response)).Code);
    }

    [Fact]
    public async Task Anonymous_export_is_rejected()
    {
        using var anonymous = Factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/work-orders/export")).StatusCode);
    }

    [Fact]
    public async Task Export_contains_only_the_managers_organization()
    {
        using var dana = await SignInAsync(DemoAccounts.DanaManager);

        var (_, rows) = await ExportAsync(dana, "");

        var harbourviewReferences = await QueryDbAsync(async db => (await db.WorkOrders
            .Where(w => w.OrganizationId == DemoAccounts.HarbourviewId)
            .Select(w => w.Number).ToListAsync()).Select(n => $"WO-{n}").ToHashSet());
        Assert.NotEmpty(rows.Skip(1));
        Assert.DoesNotContain(rows.Skip(1), r => harbourviewReferences.Contains(r[0]));
    }

    [Fact]
    public async Task Exports_over_the_limit_are_refused_rather_than_truncated()
    {
        await using var limitedFactory = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?> { ["Export:MaxRows"] = "2" });
        var tag = UniqueTag();
        using var riley = await limitedFactory.SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await limitedFactory.SignInAsync(DemoAccounts.MorganManager);
        await CreateRequestAsync(riley, DemoAccounts.QuaysideLofts, $"Limit A {tag}");
        await CreateRequestAsync(riley, DemoAccounts.QuaysideLofts, $"Limit B {tag}");
        await CreateRequestAsync(riley, DemoAccounts.TidewaterCourt, $"Limit C {tag}");

        var tooMany = await morgan.GetAsync($"/api/work-orders/export?q={tag}");
        var (atLimit, rows) = await ExportAsync(morgan, $"q={tag}&propertyId={DemoAccounts.QuaysideLofts}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(tooMany);
        Assert.Equal("export_too_large", problem.Code);
        Assert.Contains("3 work orders", problem.Detail);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(2, rows.Count - 1);
    }

    [Fact]
    public async Task Invalid_filters_are_rejected_before_any_export_starts()
    {
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.GetAsync("/api/work-orders/export?sort=title");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
