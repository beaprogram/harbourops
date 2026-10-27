namespace HarbourOps.Api.Features.Attachments;

/// <summary>Bound from the "Attachments" configuration section. Every limit is configurable.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Largest accepted upload (the original file), in bytes. Default 5 MiB.</summary>
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Most photos one work order can hold.</summary>
    public int MaxPerWorkOrder { get; set; } = 5;

    /// <summary>
    /// Largest width or height, in pixels, as declared in the image header. Checked before any pixels are
    /// decoded, so a tiny file that claims enormous dimensions ("decompression bomb") is rejected cheaply.
    /// </summary>
    public int MaxDimension { get; set; } = 10_000;

    /// <summary>Largest width × height. 40 megapixels decodes to at most 160 MB of pixels.</summary>
    public long MaxPixels { get; set; } = 40_000_000;

    /// <summary>Long edge of the stored full-size image. Larger photos are scaled down.</summary>
    public int StoredMaxEdge { get; set; } = 2560;

    /// <summary>Long edge of the gallery preview, so opening a work order never loads full-size photos.</summary>
    public int PreviewMaxEdge { get; set; } = 480;

    public int JpegQuality { get; set; } = 85;

    /// <summary>How many photos may be decoded at once (bounds peak memory).</summary>
    public int MaxConcurrentProcessing { get; set; } = 2;

    /// <summary>
    /// How many further uploads may wait for a decoding slot, each holding its buffered file. Uploads beyond
    /// MaxConcurrentProcessing + MaxQueuedUploads are refused with 503 before their body is read.
    /// </summary>
    public int MaxQueuedUploads { get; set; } = 4;

    public AttachmentStorageOptions Storage { get; set; } = new();

    public AttachmentCleanupOptions Cleanup { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (MaxFileSizeBytes <= 0) yield return "Attachments:MaxFileSizeBytes must be positive.";
        if (MaxPerWorkOrder <= 0) yield return "Attachments:MaxPerWorkOrder must be positive.";
        if (MaxDimension <= 0 || MaxPixels <= 0) yield return "Attachments:MaxDimension and MaxPixels must be positive.";
        if (PreviewMaxEdge <= 0 || StoredMaxEdge < PreviewMaxEdge) yield return "Attachments:StoredMaxEdge must be at least PreviewMaxEdge.";
        if (JpegQuality is < 1 or > 100) yield return "Attachments:JpegQuality must be between 1 and 100.";
        if (MaxConcurrentProcessing <= 0) yield return "Attachments:MaxConcurrentProcessing must be positive.";
        if (MaxQueuedUploads < 0) yield return "Attachments:MaxQueuedUploads must be zero or more.";
        switch (Storage.Provider)
        {
            case AttachmentStorageOptions.LocalProvider:
                if (string.IsNullOrWhiteSpace(Storage.LocalPath)) yield return "Attachments:Storage:LocalPath must be set to a private directory outside the web root.";
                break;
            case AttachmentStorageOptions.AzureBlobProvider:
                foreach (var error in Storage.AzureBlob.Validate()) yield return error;
                break;
            default:
                yield return "Attachments:Storage:Provider must be 'Local' or 'AzureBlob'.";
                break;
        }
        if (Cleanup.MinimumAge < TimeSpan.FromHours(1)) yield return "Attachments:Cleanup:MinimumAge must be at least one hour.";
    }
}

public sealed class AttachmentStorageOptions
{
    public const string LocalProvider = "Local";
    public const string AzureBlobProvider = "AzureBlob";

    /// <summary>"Local" (a private directory) or "AzureBlob" (a private Blob Storage container).</summary>
    public string Provider { get; set; } = LocalProvider;

    /// <summary>Private directory for stored photos. Relative paths resolve against the app's content root.</summary>
    public string LocalPath { get; set; } = string.Empty;

    public AzureBlobStorageOptions AzureBlob { get; set; } = new();
}

public sealed partial class AzureBlobStorageOptions
{
    /// <summary>For example https://&lt;account&gt;.blob.core.windows.net. Reached with the app's managed identity.</summary>
    public string? ServiceUri { get; set; }

    /// <summary>Only for the local emulator (Azurite) and tests. Hosted configurations must use <see cref="ServiceUri"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>A private container used only for photos (Data Protection keys live elsewhere: see decisions §41).</summary>
    public string ContainerName { get; set; } = "photos";

    /// <summary>Create the container if it is missing (emulator convenience). In Azure, infrastructure creates it, private.</summary>
    public bool CreateContainerIfMissing { get; set; }

    public IEnumerable<string> Validate()
    {
        var hasUri = !string.IsNullOrWhiteSpace(ServiceUri);
        var hasConnectionString = !string.IsNullOrWhiteSpace(ConnectionString);
        if (hasUri == hasConnectionString)
            yield return "Attachments:Storage:AzureBlob needs exactly one of ServiceUri (managed identity) or ConnectionString (emulator only).";
        if (hasUri && (!Uri.TryCreate(ServiceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            yield return "Attachments:Storage:AzureBlob:ServiceUri must be an https URL.";
        if (!ContainerNamePattern().IsMatch(ContainerName) || ContainerName.Contains("--", StringComparison.Ordinal))
            yield return "Attachments:Storage:AzureBlob:ContainerName must be a valid container name (3-63 lowercase letters, digits, and single hyphens).";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial System.Text.RegularExpressions.Regex ContainerNamePattern();
}

public sealed class AttachmentCleanupOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Only stored objects older than this can be removed as orphans. It must be far longer than any upload
    /// request takes, so an upload that has stored its files but not yet saved its database row is never touched.
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromHours(24);
}
