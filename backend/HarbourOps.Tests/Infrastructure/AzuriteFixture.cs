using HarbourOps.Api.Features.Attachments;
using HarbourOps.Api.Features.Attachments.Storage;
using Testcontainers.Azurite;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>
/// The Azure Storage emulator (Azurite) in a container, for testing the Blob Storage adapter locally.
/// Passing these tests shows the adapter works against the emulator's implementation of the Blob API; it is
/// not proof of behaviour against real Azure Storage (authentication, networking, and limits differ).
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

    // In-memory persistence: nothing written to the Docker host's disk. Azurite 3.37 doesn't know the storage API
    // version that Azure.Storage.Blobs 12.30 sends (2026-10-06), so its version check is skipped; real Azure is
    // unaffected. This is one of the reasons emulator results aren't treated as proof of Azure behaviour.
    private readonly AzuriteContainer _container = new AzuriteBuilder(Image)
        .WithInMemoryPersistence()
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public string BlobEndpoint => _container.GetBlobEndpoint();

    /// <summary>A fresh, private container for one test, so tests never see each other's blobs.</summary>
    public AzureBlobAttachmentStorage NewStorage(string? containerName = null) =>
        AzureBlobAttachmentStorage.Create(Options(containerName), () => throw new InvalidOperationException("Tests use the emulator connection string."));

    public AzureBlobStorageOptions Options(string? containerName = null) => new()
    {
        ConnectionString = ConnectionString,
        ContainerName = containerName ?? NewContainerName(),
        CreateContainerIfMissing = true,
    };

    public static string NewContainerName() => "test-" + Guid.NewGuid().ToString("N")[..20];

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
