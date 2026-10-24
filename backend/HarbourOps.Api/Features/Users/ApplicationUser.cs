using HarbourOps.Api.Features.Organizations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Users;

public enum UserRole
{
    Manager,
    Technician,
    Requester,
}

/// <summary>
/// An account managed by ASP.NET Core Identity (password hashing, lockout, security stamp).
/// We add the organization and a single role directly on the user row: in this release an
/// account has exactly one role in exactly one organization, and the schema enforces that.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string DisplayName { get; set; }
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
}

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.DisplayName).HasMaxLength(100);
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);

        builder.HasOne(u => u.Organization)
            .WithMany()
            .HasForeignKey(u => u.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // (OrganizationId, Id) is unique so other tables can point at "a user in organization X"
        // with a composite foreign key. That lets PostgreSQL itself reject cross-organization links.
        builder.HasAlternateKey(u => new { u.OrganizationId, u.Id });
        builder.HasIndex(u => new { u.OrganizationId, u.Role });

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AspNetUsers_Role", "\"Role\" IN ('Manager', 'Technician', 'Requester')"));
    }
}
