using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Attachments;

/// <summary>
/// Bounds how many uploads may hold resources in this process at once: at most
/// <see cref="AttachmentOptions.MaxConcurrentProcessing"/> being decoded plus
/// <see cref="AttachmentOptions.MaxQueuedUploads"/> waiting for a decoding slot.
///
/// Admission is decided before the request body is read. Without it, a burst of uploads would each buffer
/// up to the file limit in memory while queued behind the decoder, so waiting requests could accumulate
/// unbounded memory. Requests beyond the bound are refused at once (503 + Retry-After) instead of waiting.
/// This is deliberately per process: it protects this process's memory, not a global quota.
/// </summary>
public sealed class UploadAdmission(IOptions<AttachmentOptions> options)
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentProcessing + options.Value.MaxQueuedUploads);

    /// <summary>A slot to hold for the whole upload, or null when the process is already at its bound.</summary>
    public Lease? TryAcquire() => _slots.Wait(0) ? new Lease(_slots) : null;

    /// <summary>Uploads that can be admitted right now (for tests and diagnostics).</summary>
    public int Available => _slots.CurrentCount;

    public sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
