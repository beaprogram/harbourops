using HarbourOps.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Demo.Sandbox;

/// <summary>
/// One visitor's private, temporary copy of the fictional demo: its own pair of organizations, people, and work.
/// The visitor proves ownership with a random credential held in an HttpOnly cookie; only its SHA-256 hash is
/// stored, so a database reader cannot take over a sandbox. Access ends at <see cref="ExpiresAt"/> or when the
/// sandbox is revoked (reset, ended, or cleaned up), whichever comes first. The row stays as a tombstone after
/// cleanup so a returning visitor sees "your demo has ended" instead of a generic sign-in page.
/// </summary>
public sealed class DemoSandbox
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the 32 random bytes in the visitor's credential cookie.</summary>
    public required byte[] CredentialHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when access ends early or at cleanup. Revocation always happens before any data is removed.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Set once every record and stored photo of the sandbox has been removed.</summary>
    public DateTimeOffset? CleanedAt { get; set; }

    // Usage counters. Each is raised by one conditional UPDATE in the same transaction as the write it allows,
    // so concurrent requests can't overshoot a limit (see DemoQuotas).
    public int WorkOrderCount { get; set; }
    public int CommentCount { get; set; }
    public int ActionCount { get; set; }
    public int UploadCount { get; set; }
    public int AttachmentCount { get; set; }
    public long StoredBytes { get; set; }
    public int ExportCount { get; set; }
}

/// <summary>A predefined person a visitor may act as inside their own sandbox, chosen by key, never by user ID.</summary>
public sealed class DemoPersona
{
    public Guid SandboxId { get; set; }
    public required string Key { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public int SortOrder { get; set; }
}

internal sealed class DemoSandboxConfiguration : IEntityTypeConfiguration<DemoSandbox>
{
    public void Configure(EntityTypeBuilder<DemoSandbox> builder)
    {
        builder.ToTable("DemoSandboxes", t =>
        {
            t.HasCheckConstraint("CK_DemoSandboxes_CredentialHash", "octet_length(\"CredentialHash\") = 32");
            t.HasCheckConstraint("CK_DemoSandboxes_Lifetime", "\"ExpiresAt\" > \"CreatedAt\"");
            t.HasCheckConstraint("CK_DemoSandboxes_RevokedReason",
                "\"RevokedReason\" IS NULL OR \"RevokedReason\" IN ('expired', 'reset', 'ended', 'failed', 'disabled')");
            t.HasCheckConstraint("CK_DemoSandboxes_Cleaned", "\"CleanedAt\" IS NULL OR \"RevokedAt\" IS NOT NULL");
            t.HasCheckConstraint("CK_DemoSandboxes_Counters",
                "\"WorkOrderCount\" >= 0 AND \"CommentCount\" >= 0 AND \"ActionCount\" >= 0 AND \"UploadCount\" >= 0 " +
                "AND \"AttachmentCount\" >= 0 AND \"StoredBytes\" >= 0 AND \"ExportCount\" >= 0");
        });
        builder.Property(s => s.CredentialHash).HasMaxLength(32);
        builder.Property(s => s.RevokedReason).HasMaxLength(20);
        builder.HasIndex(s => s.CredentialHash).IsUnique();
        // Capacity checks and the cleanup loop look at sandboxes that still hold data.
        builder.HasIndex(s => s.CreatedAt).HasFilter("\"CleanedAt\" IS NULL").HasDatabaseName("IX_DemoSandboxes_Uncleaned");
    }
}

internal sealed class DemoPersonaConfiguration : IEntityTypeConfiguration<DemoPersona>
{
    public void Configure(EntityTypeBuilder<DemoPersona> builder)
    {
        builder.ToTable("DemoPersonas", t => t.HasCheckConstraint(
            "CK_DemoPersonas_Key", "\"Key\" IN ('manager', 'technician', 'requester', 'otherManager')"));
        builder.HasKey(p => new { p.SandboxId, p.Key });
        builder.Property(p => p.Key).HasMaxLength(20);
        builder.HasIndex(p => p.UserId).IsUnique();

        builder.HasOne<DemoSandbox>().WithMany().HasForeignKey(p => p.SandboxId).OnDelete(DeleteBehavior.Restrict);
        // A persona is a user in a specific organization; the composite key keeps the pair consistent.
        builder.HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(p => new { p.OrganizationId, p.UserId })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
