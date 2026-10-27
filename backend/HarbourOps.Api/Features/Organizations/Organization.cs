using HarbourOps.Api.Features.Demo.Sandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Organizations;

/// <summary>A property-management team. Every account and record belongs to exactly one organization.</summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set only for the fictional organizations inside one visitor's demo sandbox. Ordinary organizations
    /// (and the shared demo organizations) have no sandbox, and demo endpoints never touch them.
    /// </summary>
    public Guid? DemoSandboxId { get; set; }
    public DemoSandbox? DemoSandbox { get; set; }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");
        builder.Property(o => o.Name).HasMaxLength(120);
        builder.Property(o => o.Slug).HasMaxLength(60);
        builder.HasIndex(o => o.Slug).IsUnique();
        builder.HasOne(o => o.DemoSandbox)
            .WithMany()
            .HasForeignKey(o => o.DemoSandboxId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(o => o.DemoSandboxId);
    }
}
