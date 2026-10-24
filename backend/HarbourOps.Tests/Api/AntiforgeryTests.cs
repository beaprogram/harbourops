using System.Net;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Tests.Api;

/// <summary>
/// The session cookie alone must never be enough to change data. Every state-changing request,
/// including sign-in and sign-out, needs a valid antiforgery token in the X-XSRF-TOKEN header.
/// </summary>
public sealed class AntiforgeryTests(PostgresFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Sign_in_without_a_token_is_rejected_and_no_session_is_created()
    {
        using var client = Factory.CreateApiClient();

        var response = await client.PostWithTokenAsync("/api/auth/login",
            new { email = DemoAccounts.RileyRequester, password = DemoAccounts.Password }, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery_failed", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Cookie_authenticated_command_without_a_token_is_rejected_and_nothing_changes()
    {
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.New);
        using var morgan = await SignInAsync(DemoAccounts.MorganManager);

        var response = await morgan.PostWithTokenAsync($"/api/work-orders/{workOrder.Id}/priority",
            new { priority = "High", version = workOrder.Version }, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("antiforgery_failed", (await ApiClient.ReadProblemAsync(response)).Code);
        var stored = await QueryDbAsync(db => db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == workOrder.Id));
        Assert.Equal(WorkOrderPriority.Normal, stored.Priority);
    }

    [Fact]
    public async Task Forged_token_is_rejected()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);

        var response = await riley.PostWithTokenAsync("/api/work-orders", new
        {
            propertyId = DemoAccounts.QuaysideLofts,
            title = "Forged",
            description = "Forged",
            category = "General",
        }, token: "not-a-real-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Token_issued_before_sign_in_stops_working_after_sign_in()
    {
        using var client = Factory.CreateApiClient();
        var anonymousToken = await client.RefreshAntiforgeryTokenAsync();
        var login = await client.PostAsync("/api/auth/login",
            new { email = DemoAccounts.RileyRequester, password = DemoAccounts.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Tokens are bound to the user identity, so the frontend must refresh after sign-in.
        var withStaleToken = await client.PostWithTokenAsync("/api/auth/logout", null, anonymousToken);
        Assert.Equal(HttpStatusCode.BadRequest, withStaleToken.StatusCode);

        await client.RefreshAntiforgeryTokenAsync();
        var withFreshToken = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, withFreshToken.StatusCode);
    }

    [Fact]
    public async Task Token_from_one_user_cannot_be_replayed_by_another()
    {
        using var riley = await SignInAsync(DemoAccounts.RileyRequester);
        using var alex = await SignInAsync(DemoAccounts.AlexRequester);

        var response = await alex.PostWithTokenAsync("/api/auth/logout", null, riley.AntiforgeryToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
