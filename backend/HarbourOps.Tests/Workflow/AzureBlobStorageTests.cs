using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Tests.Infrastructure;

namespace HarbourOps.Tests.Workflow;

/// <summary>The Blob Storage adapter's contract, against the Azurite emulator (not real Azure).</summary>
public sealed class AzureBlobStorageTests(AzuriteFixture azurite) : IClassFixture<AzuriteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Objects_round_trip_and_deleting_twice_or_deleting_nothing_is_fine()
    {
        var storage = azurite.NewStorage();
        var key = StorageKeys.New();

        await storage.SaveAsync(key, new byte[] { 1, 2, 3 }, Ct);
        await using (var stream = await storage.OpenReadAsync(key, Ct))
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy, Ct);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        await storage.DeleteAsync(key, Ct);
        await storage.DeleteAsync(key, Ct);
        await storage.DeleteAsync(StorageKeys.New(), Ct);
        Assert.Null(await storage.OpenReadAsync(key, Ct));
    }

    [Fact]
    public async Task An_existing_key_is_never_overwritten()
    {
        var storage = azurite.NewStorage();
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);

        var conflict = await Assert.ThrowsAsync<RequestFailedException>(() => storage.SaveAsync(key, new byte[] { 2 }, Ct));

        Assert.Equal(409, conflict.Status);
        await using var stream = await storage.OpenReadAsync(key, Ct);
        Assert.Equal(1, stream!.ReadByte());
    }

    [Fact]
    public async Task Only_server_generated_keys_are_accepted_and_listed()
    {
        var storage = azurite.NewStorage();
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);
        await storage.Container.GetBlobClient("notes/readme.txt").UploadAsync(BinaryData.FromString("not ours"), Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync("../../escape", new byte[] { 1 }, Ct));
        var listed = new List<StoredObjectInfo>();
        await foreach (var item in storage.ListAsync(Ct))
        {
            listed.Add(item);
        }

        var only = Assert.Single(listed); // the foreign blob is never reported, so reconciliation can't delete it
        Assert.Equal(key, only.Key);
        Assert.InRange(only.LastModified, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task The_container_is_private_and_blobs_cannot_be_read_anonymously()
    {
        var storage = azurite.NewStorage();
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);

        var properties = await storage.Container.GetPropertiesAsync(cancellationToken: Ct);
        Assert.Equal(PublicAccessType.None, properties.Value.PublicAccess);
        var anonymous = new BlobClient(storage.Container.GetBlobClient(key).Uri);
        var refused = await Assert.ThrowsAsync<RequestFailedException>(() => anonymous.DownloadContentAsync(Ct));
        Assert.NotEqual(200, refused.Status);
    }

    [Fact]
    public async Task A_cancelled_write_stores_nothing()
    {
        var storage = azurite.NewStorage();
        var key = StorageKeys.New();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SaveAsync(key, new byte[1024], cancelled.Token));

        Assert.Null(await storage.OpenReadAsync(key, Ct));
    }
}
