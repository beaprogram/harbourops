using System.Net;
using HarbourOps.Api.Features.Auth;
using HarbourOps.Api.Features.Users;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Api;

public sealed class AuthenticationTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/work-orders")]
    [InlineData("/api/work-orders/40000000-0000-4000-8000-000000000101")]
    [InlineData("/api/work-orders/40000000-0000-4000-8000-000000000101/activity")]
    [InlineData("/api/properties")]
    [InlineData("/api/technicians")]
    public async Task Protected_endpoints_return_json_401_without_a_session(string url)
    {
        using var anonymous = Factory.CreateApiClient();

        var response = await anonymous.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location); // no redirect to an HTML login page
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Equal("unauthenticated", problem.Code);
    }

    [Fact]
    public async Task Anonymous_commands_are_rejected_even_with_a_valid_antiforgery_token()
    {
        using var anonymous = Factory.CreateApiClient();
        await anonymous.RefreshAntiforgeryTokenAsync();

        var response = await anonymous.PostAsync("/api/work-orders", new
        {
            propertyId = DemoAccounts.QuaysideLofts,
            title = "Should not be created",
            description = "Anonymous request",
            category = "General",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_sets_a_hardened_cookie_and_me_reports_server_side_identity()
    {
        using var client = Factory.CreateApiClient();
        await client.RefreshAntiforgeryTokenAsync();

        var response = await client.PostAsync("/api/auth/login",
            new { email = DemoAccounts.RileyRequester, password = DemoAccounts.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("HarbourOps.Auth="));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetJsonAsync<CurrentUserResponse>("/api/auth/me");
        Assert.Equal(DemoAccounts.RileyId, me.Id);
        Assert.Equal(UserRole.Requester, me.Role);
        Assert.Equal(DemoAccounts.HarbourviewId, me.Organization.Id);
    }

    [Theory]
    [InlineData(DemoAccounts.RileyRequester, "wrong-password-123!")]
    [InlineData("nobody@harbourview.example", DemoAccounts.Password)]
    public async Task Failed_sign_in_returns_the_same_generic_401(string email, string password)
    {
        using var client = Factory.CreateApiClient();

        var response = await client.LoginAsync(email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ApiClient.ReadProblemAsync(response);
        Assert.Equal("invalid_credentials", problem.Code);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("HarbourOps.Auth=")));
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        using var client = await SignInAsync(DemoAccounts.RileyRequester);

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited_per_client()
    {
        await using var factory = new HarbourOpsFactory(Fixture.ConnectionString,
            new Dictionary<string, string?> { ["Security:LoginPermitLimitPerMinute"] = "3" });
        using var client = factory.CreateApiClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var allowed = await client.LoginAsync("nobody@harbourview.example", "wrong-password-123!");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var limited = await client.LoginAsync("nobody@harbourview.example", "wrong-password-123!");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await ApiClient.ReadProblemAsync(limited)).Code);
    }
}
