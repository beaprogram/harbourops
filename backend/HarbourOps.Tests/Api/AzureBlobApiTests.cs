using System.Net;
using System.Net.Http.Json;
using Azure.Storage.Blobs;
using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Api.Features.Demo.Sandbox;
using HarbourOps.Api.Features.WorkOrders;
using HarbourOps.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HarbourOps.Tests.Api;

/// <summary>
/// The whole photo feature with the Azure Blob provider, against PostgreSQL and the Azurite emulator: the same
/// access rules, headers, failure cleanup, reconciliation, and demo cleanup as local storage. Emulator only;
/// real Azure (managed identity, network rules) is verified separately after deployment.
/// </summary>
public sealed class AzureBlobApiTests(PostgresFixture fixture, AzuriteFixture azurite) : IntegrationTest(fixture), IClassFixture<AzuriteFixture>
{
    private static string AttachmentsUrl(Guid workOrderId) => $"/api/work-orders/{workOrderId}/attachments";

    [Fact]
    public async Task Photos_round_trip_through_a_private_container_with_the_same_access_rules_and_headers()
    {
        var container = AzuriteFixture.NewContainerName();
        await using var app = BlobApp(container);
        using var riley = await app.SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        var upload = await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(120, 80));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var photo = (await upload.Content.ReadFromJsonAsync<AttachmentResponse>(ApiClient.Json, TestContext.Current.CancellationToken))!;

        using var morgan = await app.SignInAsync(DemoAccounts.MorganManager);
        var content = await morgan.GetAsync(photo.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/jpeg", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", content.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(content.Headers.CacheControl!.NoStore);
        using (var decoded = TestImages.Decode(await content.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal((120, 80), (decoded.Width, decoded.Height));
        }

        using var dana = await app.SignInAsync(DemoAccounts.DanaManager);
        Assert.Equal(HttpStatusCode.NotFound, (await dana.GetAsync(photo.ContentUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dana.GetAsync(photo.PreviewUrl)).StatusCode);

        // The blob is named only by its server-generated key, and the container refuses anonymous reads.
        var stored = await QueryDbAsync(db => db.Attachments.SingleAsync(a => a.Id == photo.Id));
        Assert.Equal(2, await BlobCountAsync(container));
        using var anonymous = new HttpClient();
        var direct = await anonymous.GetAsync($"{azurite.BlobEndpoint}/{container}/{stored.StorageKey}", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, direct.StatusCode);
    }

    [Fact]
    public async Task An_upload_refused_after_storing_its_files_removes_the_blobs()
    {
        var container = AzuriteFixture.NewContainerName();
        var gate = new GatedStorage();
        await using var app = BlobApp(container, services: services => services.AddSingleton<IAttachmentStorage>(_ =>
            gate.Wrap(AzureBlobAttachmentStorage.Create(azurite.Options(container), () => throw new InvalidOperationException()))));
        var workOrder = await CreateInStatusAsync(WorkOrderStatus.Resolved);
        using var riley = await app.SignInAsync(DemoAccounts.RileyRequester);
        using var morgan = await app.SignInAsync(DemoAccounts.MorganManager);

        var upload = riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(60, 60));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var closed = await morgan.PostAsync($"/api/work-orders/{workOrder.Id}/close", new { version = workOrder.Version });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        gate.Release();

        var response = await upload;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("work_order_closed", (await ApiClient.ReadProblemAsync(response)).Code);
        Assert.Equal(0, await BlobCountAsync(container));
    }

    [Fact]
    public async Task Reconciliation_removes_only_old_unreferenced_blobs()
    {
        var container = AzuriteFixture.NewContainerName();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = BlobApp(container, services: services => services.AddSingleton<TimeProvider>(clock));
        using var riley = await app.SignInAsync(DemoAccounts.RileyRequester);
        var workOrder = await CreateRequestAsync(riley);
        Assert.Equal(HttpStatusCode.Created, (await riley.UploadAsync(AttachmentsUrl(workOrder.Id), TestImages.Jpeg(50, 50))).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAttachmentStorage>();
        var orphan = StorageKeys.New();
        await storage.SaveAsync(orphan, new byte[] { 1 }, TestContext.Current.CancellationToken);
        var reconciler = scope.ServiceProvider.GetRequiredService<AttachmentReconciler>();

        var tooRecent = await reconciler.RunAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(2));
        var later = await reconciler.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, tooRecent.OrphansDeleted);
        Assert.Equal(1, later.OrphansDeleted);
        Assert.Null(await storage.OpenReadAsync(orphan, TestContext.Current.CancellationToken));
        Assert.Equal(2, await BlobCountAsync(container)); // the referenced photo and its preview are kept
    }

    [Fact]
    public async Task Demo_cleanup_removes_only_the_ended_sandboxes_blobs()
    {
        var container = AzuriteFixture.NewContainerName();
        await using var app = BlobApp(container, new() { ["DemoSandbox:Enabled"] = "true", ["DemoSandbox:CreationsPerClientPerHour"] = "1000" });
        var (ending, endingSandbox) = await StartDemoWithPhotoAsync(app);
        var (staying, _) = await StartDemoWithPhotoAsync(app);
        Assert.Equal(4, await BlobCountAsync(container));

        Assert.Equal(HttpStatusCode.NoContent, (await ending.PostAsync("/api/demo/end", null)).StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoSandboxCleaner>().CleanAsync(endingSandbox, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, await BlobCountAsync(container));
        Assert.Equal(HttpStatusCode.OK, (await staying.GetAsync("/api/work-orders")).StatusCode);
        ending.Dispose();
        staying.Dispose();
    }

    private HarbourOpsFactory BlobApp(
        string container, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Attachments:Storage:Provider"] = "AzureBlob",
            ["Attachments:Storage:AzureBlob:ConnectionString"] = azurite.ConnectionString,
            ["Attachments:Storage:AzureBlob:ContainerName"] = container,
            ["Attachments:Storage:AzureBlob:CreateContainerIfMissing"] = "true",
        };
        foreach (var (key, value) in settings ?? [])
        {
            all[key] = value;
        }

        return new HarbourOpsFactory(Fixture.ConnectionString, all, services);
    }

    private async Task<int> BlobCountAsync(string container)
    {
        var count = 0;
        await foreach (var _ in new BlobContainerClient(azurite.ConnectionString, container).GetBlobsAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            count++;
        }

        return count;
    }

    private async Task<(ApiClient Client, Guid SandboxId)> StartDemoWithPhotoAsync(HarbourOpsFactory app)
    {
        var client = app.CreateApiClient(new System.Net.CookieContainer());
        await client.RefreshAntiforgeryTokenAsync();
        var session = await client.PostJsonAsync<DemoSignInResponse>("/api/demo/sessions", null);
        await client.RefreshAntiforgeryTokenAsync();
        var list = await client.GetJsonAsync<PagedResponse<WorkOrderListItem>>("/api/work-orders?pageSize=50");
        var target = list.Items.First(w => w.Status != WorkOrderStatus.Closed).Id;
        Assert.Equal(HttpStatusCode.Created, (await client.UploadAsync(AttachmentsUrl(target), TestImages.Jpeg(40, 40))).StatusCode);
        var sandbox = await QueryDbAsync(db => db.Organizations.Where(o => o.Id == session.User.Organization.Id).Select(o => o.DemoSandboxId!.Value).SingleAsync());
        return (client, sandbox);
    }
}
