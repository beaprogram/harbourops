using System.Net;
using System.Net.Http.Json;
using HarbourOps.Api.Features.Attachments.Storage;
using HarbourOps.Tests.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HarbourOps.Tests.Workflow;

/// <summary>The Supabase Storage adapter's contract, against Supabase's open-source Storage API server (not the hosted platform).</summary>
public sealed class SupabaseStorageTests(SupabaseStorageFixture supabase) : IClassFixture<SupabaseStorageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Objects_round_trip_and_deleting_twice_or_deleting_nothing_is_fine()
    {
        var storage = supabase.NewStorage();
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
        var storage = supabase.NewStorage();
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);

        var conflict = await Assert.ThrowsAsync<HttpRequestException>(() => storage.SaveAsync(key, new byte[] { 2 }, Ct));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var stream = await storage.OpenReadAsync(key, Ct);
        Assert.Equal(1, stream!.ReadByte());
    }

    [Fact]
    public async Task Only_server_generated_keys_are_accepted_and_listed()
    {
        var bucket = SupabaseStorageFixture.NewBucketName();
        var storage = supabase.NewStorage(bucket);
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);
        using var raw = supabase.RawClient(supabase.ServiceKey);
        Assert.True((await raw.PostAsync($"object/{bucket}/notes/readme.txt", new StringContent("not ours"), Ct)).IsSuccessStatusCode);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync("../../escape", new byte[] { 1 }, Ct));
        var listed = new List<StoredObjectInfo>();
        await foreach (var item in storage.ListAsync(Ct))
        {
            listed.Add(item);
        }

        var only = Assert.Single(listed);
        Assert.Equal(key, only.Key);
        Assert.InRange(only.LastModified, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Listing_pages_through_every_object()
    {
        var storage = supabase.NewStorage(listPageSize: 2);
        var keys = Enumerable.Range(0, 5).Select(_ => StorageKeys.New()).ToList();
        foreach (var key in keys)
        {
            await storage.SaveAsync(key, new byte[] { 1 }, Ct);
        }

        var listed = new List<string>();
        await foreach (var item in storage.ListAsync(Ct))
        {
            listed.Add(item.Key);
        }

        Assert.Equal(keys.Order(StringComparer.Ordinal), listed);
    }

    [Fact]
    public async Task Photos_in_the_private_bucket_are_unreachable_without_the_server_key()
    {
        var bucket = SupabaseStorageFixture.NewBucketName();
        var storage = supabase.NewStorage(bucket);
        var key = StorageKeys.New();
        await storage.SaveAsync(key, new byte[] { 1 }, Ct);

        using var anonymous = supabase.RawClient();
        using var browserKey = supabase.RawClient(supabase.AnonKey);
        Assert.False((await anonymous.GetAsync($"object/public/{bucket}/{key}", Ct)).IsSuccessStatusCode);
        Assert.False((await anonymous.GetAsync($"object/authenticated/{bucket}/{key}", Ct)).IsSuccessStatusCode);
        Assert.False((await browserKey.GetAsync($"object/authenticated/{bucket}/{key}", Ct)).IsSuccessStatusCode);
        Assert.NotNull(await storage.OpenReadAsync(key, Ct)); // the server's key still can
    }

    [Fact]
    public async Task The_health_check_insists_the_bucket_exists_and_stays_private()
    {
        var bucket = SupabaseStorageFixture.NewBucketName();
        var storage = supabase.NewStorage(bucket);
        var check = new AttachmentStorageHealthCheck(storage);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);

        using var raw = supabase.RawClient(supabase.ServiceKey);
        Assert.True((await raw.PutAsJsonAsync($"bucket/{bucket}", new { @public = true }, Ct)).IsSuccessStatusCode);
        var exposed = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Unhealthy, exposed.Status);
        Assert.Contains("public", exposed.Description);

        var missingOptions = supabase.Options();
        missingOptions.CreateBucketIfMissing = false;
        var missing = SupabaseStorageAttachmentStorage.Create(missingOptions);
        Assert.Equal(HealthStatus.Degraded, (await new AttachmentStorageHealthCheck(missing).CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
    }
}
