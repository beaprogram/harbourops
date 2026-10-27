using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Stores photos in a private Azure Blob Storage container. Blobs are named only by server-generated keys,
/// are never given public access or SAS links, and are only ever streamed through the authorized attachment
/// endpoints, which re-check access on every request (so reassignment and demo expiry take effect at once).
///
/// In Azure the app authenticates with its managed identity (role: Storage Blob Data Contributor on this
/// container only). A connection string is accepted for the local emulator (Azurite) and tests.
/// </summary>
public sealed class AzureBlobAttachmentStorage(BlobContainerClient container) : IAttachmentStorage
{
    public static AzureBlobAttachmentStorage Create(AzureBlobStorageOptions options, Func<TokenCredential> credential)
    {
        var service = options.ConnectionString is { Length: > 0 } connectionString
            ? new BlobServiceClient(connectionString)
            : new BlobServiceClient(new Uri(options.ServiceUri!), credential());
        var container = service.GetBlobContainerClient(options.ContainerName);
        if (options.CreateContainerIfMissing)
        {
            container.CreateIfNotExists(PublicAccessType.None); // emulator and tests; in Azure, infrastructure creates it
        }

        return new AzureBlobAttachmentStorage(container);
    }

    public BlobContainerClient Container => container;

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        // Photos are a few MB at most, so this is one Put Blob request: the blob appears complete or not at all.
        // If-None-Match: * means an existing key is never overwritten.
        await container.GetBlobClient(key).UploadAsync(BinaryData.FromBytes(content), new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
            TransferOptions = new Azure.Storage.StorageTransferOptions { InitialTransferSize = 32 * 1024 * 1024 },
        }, ct);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        try
        {
            var download = await container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: ct);
            return download.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        await container.GetBlobClient(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
        {
            // Anything not named like a storage key isn't ours to reconcile, so it is never reported (or deleted).
            if (StorageKeys.IsValid(blob.Name))
            {
                yield return new StoredObjectInfo(blob.Name, blob.Properties.LastModified ?? blob.Properties.CreatedOn ?? DateTimeOffset.MinValue);
            }
        }
    }

    /// <summary>
    /// Nothing to do: a single Put Blob is atomic, and Blob Storage discards uncommitted blocks by itself after
    /// seven days, so there are no partial objects to remove.
    /// </summary>
    public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct) => Task.FromResult(0);
}
