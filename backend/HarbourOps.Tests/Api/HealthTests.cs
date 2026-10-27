using System.Net;
using System.Net.Http.Json;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Api;

/// <summary>The endpoints App Service and the release workflow rely on.</summary>
public sealed class HealthTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Readiness_covers_the_database_cookie_keys_and_photo_storage()
    {
        using var client = Factory.CreateApiClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_version_endpoint_reports_the_build_and_is_never_cached()
    {
        await using var app = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?> { ["HARBOUROPS_VERSION"] = "0123abc" });
        using var client = app.CreateApiClient();

        var response = await client.GetAsync("/health/version");

        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.Equal("0123abc", body!["version"]);
    }
}
