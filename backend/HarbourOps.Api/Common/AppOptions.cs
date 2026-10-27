using Microsoft.AspNetCore.Http;

namespace HarbourOps.Api.Common;

/// <summary>Bound from the "Security" configuration section.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// "Always" (the default) marks auth and antiforgery cookies Secure. Only local HTTP setups
    /// (Vite dev server, Docker Compose on localhost) set "SameAsRequest".
    /// </summary>
    public CookieSecurePolicy CookieSecurePolicy { get; set; } = CookieSecurePolicy.Always;

    /// <summary>Maximum sign-in attempts per client IP address per minute.</summary>
    public int LoginPermitLimitPerMinute { get; set; } = 10;
}

/// <summary>Bound from the "Database" configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Convenient for local development and the demo container. Production deployments apply
    /// migrations as a separate, explicit release step instead (see docs/deployment.md).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}

/// <summary>Bound from the "DemoData" configuration section. Disabled unless explicitly enabled.</summary>
public sealed class DemoDataOptions
{
    public const string SectionName = "DemoData";

    public bool Enabled { get; set; }

    /// <summary>Shared password for the fictional demo accounts. Demo-only; never reuse it.</summary>
    public string? Password { get; set; }
}

/// <summary>Bound from the "Export" configuration section.</summary>
public sealed class ExportOptions
{
    public const string SectionName = "Export";

    /// <summary>
    /// Maximum rows in one CSV export. Larger exports are refused with a clear message (never truncated),
    /// keeping memory, database time, and response size bounded for a synchronous download.
    /// </summary>
    public int MaxRows { get; set; } = 5_000;
}
