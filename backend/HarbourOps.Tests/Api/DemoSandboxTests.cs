using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Api.Features.Auth;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HarbourOps.Tests.Api;

/// <summary>
/// Per-visitor demo sandboxes on real PostgreSQL: ownership, isolation between visitors, expiry and reset with
/// retained cookies, capacity and per-sandbox limits under concurrency, and cleanup. Each visitor is a client
/// with its own cookie jar, like a separate browser.
/// </summary>
public sealed class DemoSandboxTests(PostgresFixture fixture) : IntegrationTest(fixture), IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private HarbourOpsFactory _app = null!;

    public ValueTask InitializeAsync()
    {
        _app = DemoApp();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    [Fact]
    public async Task Demo_mode_is_unreachable_when_disabled_and_existing_personas_stop_working()
    {
        using var visitor = await StartDemoAsync(_app);
        using var anonymous = Factory.CreateApiClient(); // the shared test app: demo sandboxes are off
        await anonymous.RefreshAntiforgeryTokenAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/demo/session")).StatusCode);
        foreach (var path in new[] { "/api/demo/sessions", "/api/demo/persona", "/api/demo/reset", "/api/demo/end" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync(path, new { persona = "manager" })).StatusCode);
        }

        // The same browser's cookies, presented to an app where demo mode is off, authorize nothing.
        using var replayed = Factory.CreateApiClient(Copy(visitor.Cookies));
        var me = await replayed.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("demo_expired", (await ApiClient.ReadProblemAsync(me)).Code);
    }

    [Fact]
    public async Task Two_visitors_cannot_reach_each_others_records_or_photos()
    {
        using var a = await StartDemoAsync(_app);
        using var b = await StartDemoAsync(_app);
        Assert.NotEqual(a.OrganizationId, b.OrganizationId);
        var target = await FirstOpenWorkOrderAsync(a);
        var photo = await UploadAsync(a, target);

        foreach (var persona in DemoSandboxService.PersonaKeys)
        {
            await SwitchAsync(b, persona);
            foreach (var url in new[] { $"/api/work-orders/{target}", $"/api/work-orders/{target}/attachments", photo.PreviewUrl, photo.ContentUrl })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync(url)).StatusCode);
            }

            var list = await b.Client.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");
            Assert.DoesNotContain(target, list.Items.Select(i => i.Id));
        }

        // Ordinary accounts can't see into sandboxes either.
        using var morgan = await _app.SignInAsync(DemoAccounts.MorganManager);
        Assert.Equal(HttpStatusCode.NotFound, (await morgan.GetAsync($"/api/work-orders/{target}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await morgan.GetAsync(photo.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Persona_switching_only_reaches_the_owned_sandboxes_predefined_people()
    {
        using var a = await StartDemoAsync(_app);
        using var b = await StartDemoAsync(_app);
        Assert.Equal(["manager", "technician", "requester", "otherManager"], a.Session.Session.Session!.Personas.Select(p => p.Key));

        var requester = await SwitchAsync(a, "requester");
        Assert.Equal(UserRole.Requester, requester.User.Role);
        Assert.Equal(a.OrganizationId, requester.User.Organization.Id);
        var other = await SwitchAsync(a, "otherManager");
        Assert.Equal(UserRole.Manager, other.User.Role);
        Assert.NotEqual(a.OrganizationId, other.User.Organization.Id);
        Assert.Equal(await SandboxOfAsync(a.OrganizationId), await SandboxOfAsync(other.User.Organization.Id));

        // Unknown keys, user IDs, and role names are refused.
        foreach (var bad in new[] { "admin", "Manager", b.Session.User.Id.ToString(), "" })
        {
            var refused = await a.Client.PostAsync("/api/demo/persona", new { persona = bad });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("persona", (await ApiClient.ReadProblemAsync(refused)).Errors!.Keys);
        }

        // Smuggled IDs are ignored: the credential decides the sandbox.
        var smuggled = await a.Client.PostAsync("/api/demo/persona", new
        {
            persona = "manager", userId = b.Session.User.Id, organizationId = b.OrganizationId, role = "Manager",
        });
        var smuggledSession = (await smuggled.Content.ReadFromJsonAsync<DemoSignInResponse>(ApiClient.Json, TestContext.Current.CancellationToken))!;
        Assert.Equal(a.OrganizationId, smuggledSession.User.Organization.Id);
        await a.Client.RefreshAntiforgeryTokenAsync();

        // A guessed credential is not a sandbox.
        var guessed = new CookieContainer();
        guessed.Add(new Uri("https://localhost/api/demo"),
            new Cookie(DemoSandboxService.CredentialCookie, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)), "/api/demo", "localhost"));
        using var stranger = _app.CreateApiClient(guessed);
        await stranger.RefreshAntiforgeryTokenAsync();
        var strangerSwitch = await stranger.PostAsync("/api/demo/persona", new { persona = "manager" });
        Assert.Equal(HttpStatusCode.Unauthorized, strangerSwitch.StatusCode);
        Assert.Equal("demo_session_missing", (await ApiClient.ReadProblemAsync(strangerSwitch)).Code);
        Assert.Equal("none", (await stranger.GetJsonAsync<DemoSessionResponse>("/api/demo/session")).Status);

        // B's switches always land in B's own sandbox.
        Assert.Equal(b.OrganizationId, (await SwitchAsync(b, "manager")).User.Organization.Id);
    }

    [Fact]
    public async Task Demo_endpoints_never_authenticate_or_affect_ordinary_accounts()
    {
        using var morgan = await _app.SignInAsync(DemoAccounts.MorganManager);
        foreach (var path in new[] { "/api/demo/persona", "/api/demo/reset" })
        {
            var response = await morgan.PostAsync(path, new { persona = "manager" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("demo_session_missing", (await ApiClient.ReadProblemAsync(response)).Code);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await morgan.PostAsync("/api/demo/end", null)).StatusCode);
        var me = await morgan.GetJsonAsync<CurrentUserResponse>("/api/auth/me"); // still signed in
        Assert.Equal(DemoAccounts.MorganManager, me.Email);
        Assert.Null(me.Demo);

        // Every persona key resolves to an account inside the visitor's own sandbox, never a shared one.
        using var visitor = await StartDemoAsync(_app);
        var sandbox = await SandboxOfAsync(visitor.OrganizationId);
        foreach (var key in DemoSandboxService.PersonaKeys)
        {
            var session = await SwitchAsync(visitor, key);
            Assert.Equal(sandbox, await SandboxOfAsync(session.User.Organization.Id));
            Assert.DoesNotContain(session.User.Organization.Id, new[] { DemoAccounts.HarbourviewId, DemoAccounts.CoveCommonsId });
        }

        // Personas have no password, and password sign-in refuses them before counting a failure.
        using var login = _app.CreateApiClient(new CookieContainer());
        var attempt = await login.LoginAsync(visitor.Session.User.Email, "Any-guess-at-all-123!");
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        Assert.Equal(0, await QueryDbAsync(db => db.Users.Where(u => u.Id == visitor.Session.User.Id).Select(u => u.AccessFailedCount).SingleAsync()));
    }

    [Fact]
    public async Task Expired_sandboxes_lose_access_even_with_retained_cookies()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = DemoApp(clock: clock);
        using var visitor = await StartDemoAsync(app);
        var workOrder = await FirstOpenWorkOrderAsync(visitor);
        var photo = await UploadAsync(visitor, workOrder);
        var retained = Copy(visitor.Cookies);

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        using var replayed = app.CreateApiClient(retained);
        foreach (var client in new[] { visitor.Client, replayed })
        {
            foreach (var url in new[] { "/api/auth/me", "/api/work-orders", $"/api/work-orders/{workOrder}", photo.PreviewUrl, photo.ContentUrl, "/api/work-orders/export", "/api/dashboard" })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
            }
        }

        Assert.Equal("ended", (await visitor.Client.GetJsonAsync<DemoSessionResponse>("/api/demo/session")).Status);
        await visitor.Client.RefreshAntiforgeryTokenAsync();
        foreach (var path in new[] { "/api/demo/persona", "/api/demo/reset" })
        {
            var response = await visitor.Client.PostAsync(path, new { persona = "requester" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("demo_expired", (await ApiClient.ReadProblemAsync(response)).Code);
        }

        // Starting again gives a brand-new sandbox.
        var fresh = await visitor.Client.PostJsonAsync<DemoSignInResponse>("/api/demo/sessions", null);
        Assert.NotEqual(visitor.OrganizationId, fresh.User.Organization.Id);
    }

    [Fact]
    public async Task Reset_replaces_only_the_callers_sandbox_and_retained_cookies_stop_working()
    {
        using var a = await StartDemoAsync(_app);
        using var b = await StartDemoAsync(_app);
        var requester = await SwitchAsync(a, "requester");
        var oldSandbox = await SandboxOfAsync(a.OrganizationId);
        var oldWorkOrder = await FirstOpenWorkOrderAsync(a);
        var retained = Copy(a.Cookies);

        var reset = await a.Client.PostJsonAsync<DemoSignInResponse>("/api/demo/reset", null);
        await a.Client.RefreshAntiforgeryTokenAsync();

        Assert.Equal("requester", reset.Session.Session!.CurrentPersona);
        Assert.NotEqual(requester.User.Organization.Id, reset.User.Organization.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/work-orders/{oldWorkOrder}")).StatusCode);

        // A copy of the old cookies (another tab, or someone who copied them) no longer authorizes anything.
        using var old = _app.CreateApiClient(retained);
        var me = await old.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("demo_expired", (await ApiClient.ReadProblemAsync(me)).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync($"/api/work-orders/{oldWorkOrder}")).StatusCode);

        // B is untouched.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/auth/me")).StatusCode);
        var bSandbox = await SandboxOfAsync(b.OrganizationId);
        Assert.Null(await QueryDbAsync(db => db.DemoSandboxes.Where(s => s.Id == bSandbox).Select(s => s.RevokedAt).SingleAsync()));

        // Cleanup removes the old sandbox and nothing else.
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>().CleanAsync(oldSandbox!.Value, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await QueryDbAsync(db => db.Organizations.CountAsync(o => o.DemoSandboxId == oldSandbox)));
        Assert.Equal(2, await QueryDbAsync(db => db.Organizations.CountAsync(o => o.DemoSandboxId == bSandbox)));
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/work-orders")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_starts_never_exceed_the_active_sandbox_limit()
    {
        // Far enough ahead that every sandbox from earlier tests has expired and can be cleaned first.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
        await using var app = DemoApp(new() { ["DemoSandbox:MaxActiveSandboxes"] = "3" }, clock: clock);
        await CleanEverythingAsync(app);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var client = app.CreateApiClient(new CookieContainer());
            await client.RefreshAntiforgeryTokenAsync();
            return await client.PostAsync("/api/demo/sessions", null);
        }));

        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var refused in attempts.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("demo_capacity_reached", (await ApiClient.ReadProblemAsync(refused)).Code);
            Assert.Equal("60", refused.Headers.RetryAfter!.ToString());
        }

        Assert.Equal(3, await QueryDbAsync(db => db.DemoSandboxes.CountAsync(s => s.CleanedAt == null)));
        Assert.Equal(6, await QueryDbAsync(db => db.Organizations.CountAsync(o => o.DemoSandboxId != null && o.DemoSandbox!.CleanedAt == null)));

        clock.Advance(TimeSpan.FromDays(1));
        await CleanEverythingAsync(app);
    }

    [Fact]
    public async Task Per_sandbox_limits_hold_under_concurrent_requests()
    {
        await using var app = DemoApp(new()
        {
            ["DemoSandbox:MaxWorkOrders"] = "3",
            ["DemoSandbox:MaxComments"] = "2",
            ["DemoSandbox:MaxUploads"] = "3",
            ["DemoSandbox:MaxAttachments"] = "1",
            ["DemoSandbox:MaxExports"] = "1",
        });
        using var visitor = await StartDemoAsync(app);
        using var neighbour = await StartDemoAsync(app);
        await SwitchAsync(visitor, "requester");
        var property = (await visitor.Client.GetJsonAsync<List<PropertySummary>>("/api/properties"))[0].Id;

        var creates = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => visitor.Client.PostAsync("/api/work-orders", new
        {
            propertyId = property, title = $"Sandbox request {i}", description = "Created concurrently.", category = "Plumbing",
        })));
        Assert.Equal(3, creates.Count(r => r.StatusCode == HttpStatusCode.Created));
        await AssertAllLimitedAsync(creates.Where(r => r.StatusCode != HttpStatusCode.Created));

        var workOrder = (await creates.First(r => r.StatusCode == HttpStatusCode.Created).Content
            .ReadFromJsonAsync<WorkOrderDetailResponse>(ApiClient.Json, TestContext.Current.CancellationToken))!.Id;
        var comments = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            visitor.Client.PostAsync($"/api/work-orders/{workOrder}/comments", new { body = $"Comment {i}" })));
        Assert.Equal(2, comments.Count(r => r.StatusCode == HttpStatusCode.Created));
        await AssertAllLimitedAsync(comments.Where(r => r.StatusCode != HttpStatusCode.Created));

        // Three attempts are allowed; only one photo may be kept, so the other two are refused and their files removed.
        var uploads = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            visitor.Client.UploadAsync($"/api/work-orders/{workOrder}/attachments", TestImages.Jpeg(64, 64))));
        Assert.Equal(1, uploads.Count(r => r.StatusCode == HttpStatusCode.Created));
        await AssertAllLimitedAsync(uploads.Where(r => r.StatusCode != HttpStatusCode.Created));
        await AssertAllLimitedAsync([await visitor.Client.UploadAsync($"/api/work-orders/{workOrder}/attachments", TestImages.Jpeg(64, 64))]);
        Assert.Equal(2, AttachmentTests.StoredFiles(app.StoragePath)); // one photo: full size + preview

        await SwitchAsync(visitor, "manager");
        Assert.Equal(HttpStatusCode.OK, (await visitor.Client.GetAsync("/api/work-orders/export")).StatusCode);
        await AssertAllLimitedAsync([await visitor.Client.GetAsync("/api/work-orders/export")]);

        var sandbox = await SandboxOfAsync(visitor.OrganizationId);
        var usage = await QueryDbAsync(db => db.DemoSandboxes.SingleAsync(s => s.Id == sandbox));
        Assert.Equal((3, 2, 3, 1, 1), (usage.WorkOrderCount, usage.CommentCount, usage.UploadCount, usage.AttachmentCount, usage.ExportCount));

        // One visitor's usage never limits another.
        Assert.Equal(HttpStatusCode.OK, (await neighbour.Client.GetAsync("/api/work-orders/export")).StatusCode);
    }

    [Fact]
    public async Task Cleanup_retries_after_a_failure_and_never_removes_anyone_elses_photos()
    {
        var flaky = new FlakyDeleteStorage();
        await using var app = DemoApp(services: services => services.AddSingleton<IAttachmentStorage>(sp =>
            flaky.Wrap(ActivatorUtilities.CreateInstance<LocalFileAttachmentStorage>(sp))));
        using var a = await StartDemoAsync(app);
        using var b = await StartDemoAsync(app);
        var aPhoto = await UploadAsync(a, await FirstOpenWorkOrderAsync(a));
        var bPhoto = await UploadAsync(b, await FirstOpenWorkOrderAsync(b));
        using var riley = await app.SignInAsync(DemoAccounts.RileyRequester);
        var shared = await CreateRequestAsync(riley);
        var sharedPhoto = (await (await riley.UploadAsync($"/api/work-orders/{shared.Id}/attachments", TestImages.Jpeg(64, 64)))
            .Content.ReadFromJsonAsync<AttachmentResponse>(ApiClient.Json, TestContext.Current.CancellationToken))!;
        var aSandbox = (await SandboxOfAsync(a.OrganizationId))!.Value;
        var files = AttachmentTests.StoredFiles(app.StoragePath);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsync("/api/demo/end", null)).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var cleaner = scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>();

        // 1. Storage fails after deleting one of A's two objects: the transaction rolls back and A's rows remain.
        flaky.FailDeleteNumber(2);
        await Assert.ThrowsAsync<IOException>(() => cleaner.CleanAsync(aSandbox, TestContext.Current.CancellationToken));
        Assert.Equal(1, await QueryDbAsync(db => db.Attachments.CountAsync(x => x.Id == aPhoto.Id)));
        Assert.Null(await QueryDbAsync(db => db.DemoSandboxes.Where(s => s.Id == aSandbox).Select(s => s.CleanedAt).SingleAsync()));

        // 2. The retry finishes the job (the already-deleted object is simply gone).
        Assert.Equal(2, await cleaner.CleanAsync(aSandbox, TestContext.Current.CancellationToken));
        Assert.Equal(0, await QueryDbAsync(db => db.Organizations.CountAsync(o => o.DemoSandboxId == aSandbox)));
        Assert.Equal(0, await QueryDbAsync(db => db.Attachments.CountAsync(x => x.Id == aPhoto.Id)));
        Assert.NotNull(await QueryDbAsync(db => db.DemoSandboxes.Where(s => s.Id == aSandbox).Select(s => s.CleanedAt).SingleAsync()));
        Assert.Equal(files - 2, AttachmentTests.StoredFiles(app.StoragePath));

        // 3. Running it again changes nothing.
        Assert.Equal(0, await cleaner.CleanAsync(aSandbox, TestContext.Current.CancellationToken));
        Assert.Equal(files - 2, AttachmentTests.StoredFiles(app.StoragePath));

        // Everyone else's photos are still stored and served.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync(bPhoto.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await riley.GetAsync(sharedPhoto.ContentUrl)).StatusCode);
    }

    [Theory]
    [InlineData(false)] // the visitor ended the demo while the upload was storing its files
    [InlineData(true)]  // ... and cleanup ran before the upload reached the database
    public async Task An_upload_in_flight_when_the_demo_ends_leaves_no_row_or_file(bool cleanedFirst)
    {
        var gate = new GatedStorage();
        await using var app = DemoApp(services: services => services.AddSingleton<IAttachmentStorage>(sp =>
            gate.Wrap(ActivatorUtilities.CreateInstance<LocalFileAttachmentStorage>(sp))));
        using var visitor = await StartDemoAsync(app);
        var sandbox = (await SandboxOfAsync(visitor.OrganizationId))!.Value;
        var workOrder = await FirstOpenWorkOrderAsync(visitor);
        var retained = Copy(visitor.Cookies);

        var upload = visitor.Client.UploadAsync($"/api/work-orders/{workOrder}/attachments", TestImages.Jpeg(64, 64));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        using (var otherTab = app.CreateApiClient(Copy(visitor.Cookies)))
        {
            await otherTab.RefreshAntiforgeryTokenAsync();
            Assert.Equal(HttpStatusCode.NoContent, (await otherTab.PostAsync("/api/demo/end", null)).StatusCode);
        }

        if (cleanedFirst)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>().CleanAsync(sandbox, TestContext.Current.CancellationToken);
        }

        gate.Release();
        var response = await upload;

        Assert.Equal(cleanedFirst ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(cleanedFirst ? "not_found" : "demo_expired", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(0, AttachmentTests.StoredFiles(app.StoragePath));
        Assert.Equal(0, await QueryDbAsync(db => db.Attachments.CountAsync(x => x.WorkOrderId == workOrder)));
        using var replayed = app.CreateApiClient(retained);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replayed.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Starting_switching_resetting_and_ending_require_the_antiforgery_header()
    {
        using var visitor = await StartDemoAsync(_app);
        foreach (var path in new[] { "/api/demo/sessions", "/api/demo/persona", "/api/demo/reset", "/api/demo/end" })
        {
            var response = await visitor.Client.PostWithTokenAsync(path, new { persona = "requester" }, token: null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("antiforgery_failed", (await ApiClient.ReadProblemAsync(response)).Code);
        }

        using var anonymous = _app.CreateApiClient(new CookieContainer());
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostWithTokenAsync("/api/demo/sessions", null, token: null)).StatusCode);

        var me = await visitor.Client.GetJsonAsync<CurrentUserResponse>("/api/auth/me"); // nothing changed
        Assert.Equal(UserRole.Manager, me.Role);
        Assert.Equal(visitor.OrganizationId, me.Organization.Id);
    }

    [Fact]
    public async Task Starting_demos_is_rate_limited_per_client()
    {
        await using var app = DemoApp(new() { ["DemoSandbox:CreationsPerClientPerHour"] = "2" });
        for (var i = 0; i < 2; i++)
        {
            using var _ = await StartDemoAsync(app);
        }

        using var third = app.CreateApiClient(new CookieContainer());
        await third.RefreshAntiforgeryTokenAsync();
        var refused = await third.PostAsync("/api/demo/sessions", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate_limited", (await ApiClient.ReadProblemAsync(refused)).Code);
        Assert.NotNull(refused.Headers.RetryAfter);
    }

    [Fact]
    public async Task Ending_the_demo_revokes_it_and_signs_the_persona_out()
    {
        using var visitor = await StartDemoAsync(_app);
        var retained = Copy(visitor.Cookies);

        Assert.Equal(HttpStatusCode.NoContent, (await visitor.Client.PostAsync("/api/demo/end", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.Client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal("none", (await visitor.Client.GetJsonAsync<DemoSessionResponse>("/api/demo/session")).Status);
        using var replayed = _app.CreateApiClient(retained);
        var me = await replayed.GetAsync("/api/auth/me");
        Assert.Equal("demo_expired", (await ApiClient.ReadProblemAsync(me)).Code);
        Assert.Equal("ended", (await replayed.GetJsonAsync<DemoSessionResponse>("/api/demo/session")).Status);
    }

    [Fact]
    public async Task A_new_sandbox_has_its_own_fictional_data_and_a_session_capped_at_its_lifetime()
    {
        using var visitor = await StartDemoAsync(_app);
        var session = visitor.Session.Session.Session!;
        Assert.Equal(UserRole.Manager, visitor.Session.User.Role);
        Assert.Equal("manager", session.CurrentPersona);
        Assert.Equal(_clock.GetUtcNow().AddHours(1), session.ExpiresAt);
        Assert.Equal(session.ExpiresAt, visitor.Session.User.Demo!.ExpiresAt);
        Assert.EndsWith("@sandbox.harbourops.example", visitor.Session.User.Email);

        var list = await visitor.Client.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");
        Assert.Equal(7, list.TotalCount); // Harbourview's seven fictional requests, in this sandbox only

        // The credential cookie is HttpOnly, SameSite=Strict, and scoped to the demo endpoints.
        var credential = visitor.Cookies.GetAllCookies().Single(c => c.Name == DemoSandboxService.CredentialCookie);
        Assert.Equal("/api/demo", credential.Path);
        Assert.True(credential.HttpOnly);
        Assert.Equal(43, credential.Value.Length);
    }

    // ---- helpers ----

    private sealed record Visitor(ApiClient Client, CookieContainer Cookies, DemoSignInResponse Session) : IDisposable
    {
        public Guid OrganizationId => Session.User.Organization.Id;

        public void Dispose() => Client.Dispose();
    }

    private HarbourOpsFactory DemoApp(
        Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null, FakeTimeProvider? clock = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["DemoSandbox:Enabled"] = "true",
            ["DemoSandbox:MaxActiveSandboxes"] = "1000",
            ["DemoSandbox:MaxCreationsPerHour"] = "1000",
            ["DemoSandbox:CreationsPerClientPerHour"] = "1000",
            ["DemoSandbox:PersonaSwitchesPerClientPerMinute"] = "1000",
        };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        return new HarbourOpsFactory(Fixture.ConnectionString, all, collection =>
        {
            collection.AddSingleton<TimeProvider>(clock ?? _clock);
            services?.Invoke(collection);
        });
    }

    private static async Task<Visitor> StartDemoAsync(HarbourOpsFactory app)
    {
        var cookies = new CookieContainer();
        var client = app.CreateApiClient(cookies);
        await client.RefreshAntiforgeryTokenAsync();
        var session = await client.PostJsonAsync<DemoSignInResponse>("/api/demo/sessions", null);
        await client.RefreshAntiforgeryTokenAsync(); // the token is bound to the identity, which just changed
        return new Visitor(client, cookies, session);
    }

    private static async Task<DemoSignInResponse> SwitchAsync(Visitor visitor, string persona)
    {
        var session = await visitor.Client.PostJsonAsync<DemoSignInResponse>("/api/demo/persona", new { persona });
        await visitor.Client.RefreshAntiforgeryTokenAsync();
        return session;
    }

    private static async Task<Guid> FirstOpenWorkOrderAsync(Visitor visitor)
    {
        var list = await visitor.Client.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");
        return list.Items.First(w => w.Status != WorkOrderStatus.Closed).Id;
    }

    private static async Task<AttachmentResponse> UploadAsync(Visitor visitor, Guid workOrder)
    {
        var response = await visitor.Client.UploadAsync($"/api/work-orders/{workOrder}/attachments", TestImages.Jpeg(64, 64));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AttachmentResponse>(ApiClient.Json))!;
    }

    private Task<Guid?> SandboxOfAsync(Guid organizationId) =>
        QueryDbAsync(db => db.Organizations.Where(o => o.Id == organizationId).Select(o => o.DemoSandboxId).SingleAsync());

    private static async Task AssertAllLimitedAsync(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("demo_limit_reached", (await ApiClient.ReadProblemAsync(response)).Code);
        }
    }

    private static async Task CleanEverythingAsync(HarbourOpsFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var cleaner = scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>();
        while ((await cleaner.RunOnceAsync(TestContext.Current.CancellationToken)).Cleaned > 0)
        {
        }
    }

    private static CookieContainer Copy(CookieContainer source)
    {
        var copy = new CookieContainer();
        foreach (Cookie cookie in source.GetAllCookies())
        {
            copy.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain) { HttpOnly = cookie.HttpOnly, Secure = cookie.Secure });
        }

        return copy;
    }
}
