using HarbourOps.Api.Features.Attachments.Storage;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>Holds an upload inside its first storage write until the test calls <see cref="Release"/>.</summary>
public sealed class GatedStorage
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public IAttachmentStorage Wrap(IAttachmentStorage inner) => new Gated(inner, this);

    private sealed class Gated(IAttachmentStorage inner, GatedStorage gate) : IAttachmentStorage
    {
        public async Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            gate.Entered.TrySetResult();
            await gate._release.Task.WaitAsync(ct);
            await inner.SaveAsync(key, content, ct);
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public IAsyncEnumerable<StoredObjectInfo> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct) =>
            inner.DeleteAbandonedPartialWritesAsync(olderThan, ct);
    }
}

/// <summary>Lets a test make one specific delete fail (for example the second of a cleanup pass).</summary>
public sealed class FlakyDeleteStorage
{
    private int _deletes;
    private int _failAt = int.MaxValue;

    /// <summary>The <paramref name="n"/>-th delete from now throws once; the ones before it succeed.</summary>
    public void FailDeleteNumber(int n) => Interlocked.Exchange(ref _failAt, Volatile.Read(ref _deletes) + n);

    public IAttachmentStorage Wrap(IAttachmentStorage inner) => new Flaky(inner, this);

    private sealed class Flaky(IAttachmentStorage inner, FlakyDeleteStorage owner) : IAttachmentStorage
    {
        public Task DeleteAsync(string key, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref owner._deletes);
            if (number == Volatile.Read(ref owner._failAt))
            {
                Interlocked.Exchange(ref owner._failAt, int.MaxValue);
                throw new IOException("Storage unavailable (simulated).");
            }

            return inner.DeleteAsync(key, ct);
        }

        public Task SaveAsync(string key, ReadOnlyMemory<byte> content, CancellationToken ct) => inner.SaveAsync(key, content, ct);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public IAsyncEnumerable<StoredObjectInfo> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<int> DeleteAbandonedPartialWritesAsync(DateTimeOffset olderThan, CancellationToken ct) =>
            inner.DeleteAbandonedPartialWritesAsync(olderThan, ct);
    }
}
