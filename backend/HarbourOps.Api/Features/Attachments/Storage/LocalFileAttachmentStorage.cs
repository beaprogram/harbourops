using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Stores photos as files in a private directory: outside the web root, never served as static files,
/// and only reachable through the authorized attachment endpoints.
///
/// Layout: <c>{root}/{first two key characters}/{key}</c> for complete objects, and
/// <c>{root}/.incoming/</c> for writes in progress. A write goes to .incoming first and is then renamed into
/// place, so a crash can leave an abandoned .incoming file but never a truncated object under a real key.
/// </summary>
public sealed class LocalFileAttachmentStorage : IAttachmentStorage
{
    private const string IncomingDirectory = ".incoming";
    private readonly string _root;

    public LocalFileAttachmentStorage(IOptions<AttachmentOptions> options, IWebHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.Storage.LocalPath, environment.ContentRootPath);

        var webRoot = environment.WebRootPath is { Length: > 0 } path ? Path.GetFullPath(path) : null;
        if (webRoot is not null && IsSameOrInside(_root, webRoot))
        {
            throw new InvalidOperationException("Attachment storage must not be inside the web root, where files could be served publicly.");
        }

        CreatePrivateDirectory(_root);
        CreatePrivateDirectory(Path.Combine(_root, IncomingDirectory));
    }

    public string RootPath => _root;

    public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        var finalPath = PathFor(key);
        var tempPath = Path.Combine(_root, IncomingDirectory, $"{key}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous))
            {
                await file.WriteAsync(content, ct);
                file.Flush(flushToDisk: true);
            }

            CreatePrivateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(tempPath, finalPath, overwrite: false); // atomic rename on the same volume
        }
        finally
        {
            File.Delete(tempPath); // no-op after a successful move
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        var path = PathFor(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        StorageKeys.EnsureValid(key);
        try
        {
            File.Delete(PathFor(key)); // a missing file is not an error ...
        }
        catch (DirectoryNotFoundException)
        {
            // ... and neither is a missing shard directory: deleting must be idempotent so cleanup can be retried.
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var shard in Directory.EnumerateDirectories(_root))
        {
            if (Path.GetFileName(shard) == IncomingDirectory)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(shard))
            {
                ct.ThrowIfCancellationRequested();
                var key = Path.GetFileName(file);
                if (StorageKeys.IsValid(key))
                {
                    yield return new StoredObjectInfo(key, File.GetLastWriteTimeUtc(file));
                }
            }

            await Task.Yield();
        }
    }

    public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, IncomingDirectory), "*.part"))
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
            {
                File.Delete(file);
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    private string PathFor(string key) => Path.Combine(_root, key[..2], key);

    private static bool IsSameOrInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    /// <summary>Owner-only permissions (0700) on Unix, so other local accounts can't browse the photos.</summary>
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
