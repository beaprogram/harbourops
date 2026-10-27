using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Where photo bytes live. Deliberately small so a private Azure Blob Storage implementation can replace
/// the local one later without touching the upload logic. Objects are addressed only by server-generated
/// opaque keys (see <see cref="StorageKeys"/>); no user-supplied text ever becomes part of a path or blob name.
/// </summary>
public interface IAttachmentStorage
{
    /// <summary>Writes the whole object. Readers never see a partially written object under <paramref name="key"/>.</summary>
    Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Opens an object for reading, or returns null if it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Deletes an object. Deleting a missing object is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Every stored object, for reconciliation against the database.</summary>
    IAsyncEnumerable<StoredObjectInfo> ListAsync(CancellationToken ct);

    /// <summary>Removes leftovers of writes that never completed (for example after a crash). Returns how many.</summary>
    Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct);
}

public sealed record StoredObjectInfo(string Key, DateTimeOffset LastModified);

public static partial class StorageKeys
{
    /// <summary>A new random 128-bit key, as 32 lowercase hex characters. Unguessable and path-safe.</summary>
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static void EnsureValid(string key)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException("Storage keys must be 32 lowercase hexadecimal characters.", nameof(key));
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyPattern();
}
