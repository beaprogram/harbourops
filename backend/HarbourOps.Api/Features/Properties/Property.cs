using HarbourOps.Api.Features.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Properties;

/// <summary>A building or site that an organization maintains.</summary>
public sealed class Property
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public required string AddressLine { get; set; }
    public required string Community { get; set; }
    public bool IsActive { get; set; } = true;
}

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.ToTable("Properties");
        builder.Property(p => p.Name).HasMaxLength(120);
        builder.Property(p => p.AddressLine).HasMaxLength(200);
        builder.Property(p => p.Community).HasMaxLength(80);

        builder.HasOne(p => p.Organization)
            .WithMany()
            .HasForeignKey(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Target for composite foreign keys (see ApplicationUserConfiguration).
        builder.HasAlternateKey(p => new { p.OrganizationId, p.Id });
        builder.HasIndex(p => new { p.OrganizationId, p.Name }).IsUnique();
    }
}
