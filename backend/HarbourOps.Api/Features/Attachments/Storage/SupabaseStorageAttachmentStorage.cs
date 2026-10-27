using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Stores photos in a private Supabase Storage bucket through its HTTP API. Objects are named only by
/// server-generated keys, are never given public or signed URLs, and are only ever streamed through the
/// authorized attachment endpoints, which re-check access on every request.
///
/// The server authenticates with a secret API key (Supabase's service_role). The key stays on the server: it is
/// sent only to Supabase, over HTTPS, and never logged.
///
/// The Storage API reports errors as HTTP 400 with the real status in the JSON body ("statusCode": "409"), and the
/// hosted gateway can answer with the status itself, so both forms are understood.
/// </summary>
public sealed class SupabaseStorageAttachmentStorage(HttpClient http, string bucket, int listPageSize = 1000) : IAttachmentStorage
{
    public static SupabaseStorageAttachmentStorage Create(SupabaseStorageOptions options, int listPageSize = 1000)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri(options.Url!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var key = options.ServiceKey!;
        // Secret keys (sb_secret_…) go in the apikey header. A JWT (the legacy service_role key, or the local
        // storage server used in tests) is also sent as the bearer token, which is what the Storage API itself reads.
        http.DefaultRequestHeaders.Add("apikey", key);
        if (key.StartsWith("eyJ", StringComparison.Ordinal))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var storage = new SupabaseStorageAttachmentStorage(http, options.Bucket, listPageSize);
        if (options.CreateBucketIfMissing)
        {
            storage.CreateBucketIfMissingAsync(CancellationToken.None).GetAwaiter().GetResult(); // tests only
        }

        return storage;
    }

    public string Bucket => bucket;

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        // One request: the object appears complete or not at all. x-upsert: false means an existing key is never
        // overwritten (the Storage API answers 409 Duplicate instead).
        using var request = new HttpRequestMessage(HttpMethod.Post, ObjectPath(key)) { Content = new ReadOnlyMemoryContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("x-upsert", "false");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "store a photo", ct);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        var response = await http.GetAsync($"object/authenticated/{bucket}/{key}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }

        using (response)
        {
            // The body of a streamed response can be read only once, so the status is worked out once.
            var status = await StatusOfAsync(response, ct);
            return status == HttpStatusCode.NotFound ? null : throw Failure(status, "read a photo");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        // Deleting by name list is idempotent: a missing object is simply not in the returned list.
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"object/{bucket}")
        {
            Content = JsonContent.Create(new DeleteRequest([key]), StorageJson.Default.DeleteRequest),
        };
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "delete a photo", ct);
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        for (var offset = 0; ; offset += listPageSize)
        {
            using var response = await http.PostAsJsonAsync($"object/list/{bucket}",
                new ListRequest("", listPageSize, offset, new ListSort("name", "asc")), StorageJson.Default.ListRequest, ct);
            await EnsureSuccessAsync(response, "list photos", ct);
            var page = await response.Content.ReadFromJsonAsync(StorageJson.Default.ListEntryArray, ct) ?? [];
            foreach (var entry in page)
            {
                // Anything not named like a storage key isn't ours to reconcile, so it is never reported (or deleted).
                if (StorageKeys.IsValid(entry.Name))
                {
                    yield return new StoredObjectInfo(entry.Name!, entry.UpdatedAt ?? entry.CreatedAt ?? DateTimeOffset.MinValue);
                }
            }

            if (page.Length < listPageSize)
            {
                yield break;
            }
        }
    }

    /// <summary>Nothing to do: each object is written by one request, so there are no partial objects.</summary>
    public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct) => Task.FromResult(0);

    /// <summary>The bucket's settings, or null if it doesn't exist. Used by the health check to insist it is private.</summary>
    public async Task<BucketInfo?> GetBucketAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync($"bucket/{bucket}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = await StatusOfAsync(response, ct);
            return status == HttpStatusCode.NotFound ? null : throw Failure(status, "read the photo bucket");
        }

        return await response.Content.ReadFromJsonAsync(StorageJson.Default.BucketInfo, ct);
    }

    private async Task CreateBucketIfMissingAsync(CancellationToken ct)
    {
        if (await GetBucketAsync(ct) is not null)
        {
            return;
        }

        using var response = await http.PostAsJsonAsync("bucket", new CreateBucketRequest(bucket, bucket, Public: false),
            StorageJson.Default.CreateBucketRequest, ct);
        await EnsureSuccessAsync(response, "create the photo bucket", ct);
    }

    private string ObjectPath(string key) => $"object/{bucket}/{key}";

    /// <summary>The effective status: the HTTP status, or the one in the error body when the API answers 400.</summary>
    private static async Task<HttpStatusCode> StatusOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            return response.StatusCode;
        }

        try
        {
            var error = await response.Content.ReadFromJsonAsync(StorageJson.Default.StorageError, ct);
            return int.TryParse(error?.StatusCode, out var code) ? (HttpStatusCode)code : response.StatusCode;
        }
        catch (JsonException)
        {
            return response.StatusCode;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw Failure(await StatusOfAsync(response, ct), action);
    }

    // Only the status is reported: response bodies can echo request details, and request headers carry the key.
    private static HttpRequestException Failure(HttpStatusCode status, string action) =>
        new($"Supabase Storage could not {action} (HTTP {(int)status}).", null, status);

    /// <summary>Disposes the HTTP response together with the content stream handed to the caller.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public sealed record BucketInfo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("public")] bool Public);

internal sealed record DeleteRequest([property: JsonPropertyName("prefixes")] string[] Prefixes);

internal sealed record ListSort(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("order")] string Order);

internal sealed record ListRequest(
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("sortBy")] ListSort SortBy);

internal sealed record ListEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

internal sealed record CreateBucketRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("public")] bool Public);

internal sealed record StorageError([property: JsonPropertyName("statusCode")] string? StatusCode);

[JsonSerializable(typeof(DeleteRequest))]
[JsonSerializable(typeof(ListRequest))]
[JsonSerializable(typeof(ListEntry[]))]
[JsonSerializable(typeof(CreateBucketRequest))]
[JsonSerializable(typeof(BucketInfo))]
[JsonSerializable(typeof(StorageError))]
internal sealed partial class StorageJson : JsonSerializerContext;
