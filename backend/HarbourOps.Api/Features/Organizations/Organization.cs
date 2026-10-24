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
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations");
        builder.Property(o => o.Name).HasMaxLength(120);
        builder.Property(o => o.Slug).HasMaxLength(60);
        builder.HasIndex(o => o.Slug).IsUnique();
    }
}
