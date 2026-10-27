namespace HarbourOps.Api.Features.Demo.Sandbox;

/// <summary>
/// Bound from the "DemoSandbox" configuration section. Disabled unless explicitly enabled; when disabled,
/// every /api/demo sandbox endpoint returns 404 and existing sandbox personas cannot be used.
/// All limits are enforced by the server (see decisions §37–§39).
/// </summary>
public sealed class DemoSandboxOptions
{
    public const string SectionName = "DemoSandbox";

    public bool Enabled { get; set; }

    /// <summary>Fixed lifetime from creation. Not extended by activity.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Sandboxes that still hold data (active, or revoked but not yet cleaned up). Checked in the database under an
    /// advisory lock, so concurrent "Try the demo" requests can't exceed it.
    /// </summary>
    public int MaxActiveSandboxes { get; set; } = 50;

    /// <summary>Sandboxes created in the last hour, across all visitors (database-enforced).</summary>
    public int MaxCreationsPerHour { get; set; } = 200;

    /// <summary>Sandbox creations and resets per client IP address per hour (in-process rate limiter).</summary>
    public int CreationsPerClientPerHour { get; set; } = 10;

    /// <summary>Persona switches per client IP address per minute (in-process rate limiter).</summary>
    public int PersonaSwitchesPerClientPerMinute { get; set; } = 30;

    // Per-sandbox limits on visitor activity. Seeded records don't count.
    public int MaxWorkOrders { get; set; } = 20;
    public int MaxComments { get; set; } = 60;
    /// <summary>Workflow changes (assign, start, resolve, close, reopen, priority).</summary>
    public int MaxActions { get; set; } = 200;
    /// <summary>Upload attempts, counted before the file is read, including ones that are later rejected.</summary>
    public int MaxUploads { get; set; } = 20;
    public int MaxAttachments { get; set; } = 12;
    /// <summary>Bytes of stored photos (full size + preview) across the sandbox.</summary>
    public long MaxStoredBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxExports { get; set; } = 20;

    /// <summary>Runs the background cleanup. Tests turn it off and call the cleaner directly.</summary>
    public bool CleanupEnabled { get; set; } = true;
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a cleaned-up sandbox's row is kept, so a returning visitor is told their demo ended.</summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(1);

    public IEnumerable<string> Validate()
    {
        if (Lifetime < TimeSpan.FromMinutes(5) || Lifetime > TimeSpan.FromDays(1))
            yield return "DemoSandbox:Lifetime must be between 5 minutes and 1 day.";
        if (MaxActiveSandboxes <= 0 || MaxCreationsPerHour <= 0 || CreationsPerClientPerHour <= 0 || PersonaSwitchesPerClientPerMinute <= 0)
            yield return "DemoSandbox capacity and rate limits must be positive.";
        if (MaxWorkOrders < 0 || MaxComments < 0 || MaxActions < 0 || MaxUploads < 0 || MaxAttachments < 0 || MaxStoredBytes < 0 || MaxExports < 0)
            yield return "DemoSandbox per-sandbox limits must be zero or more.";
        if (CleanupInterval < TimeSpan.FromSeconds(5))
            yield return "DemoSandbox:CleanupInterval must be at least 5 seconds.";
        // The hourly creation limit counts rows, including cleaned-up tombstones, so they must outlive an hour.
        if (TombstoneRetention < TimeSpan.FromHours(1))
            yield return "DemoSandbox:TombstoneRetention must be at least one hour.";
    }
}
