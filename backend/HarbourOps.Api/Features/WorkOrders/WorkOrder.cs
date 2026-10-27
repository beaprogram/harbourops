using HarbourOps.Api.Features.Organizations;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace HarbourOps.Api.Features.WorkOrders;

public enum WorkOrderStatus
{
    New,
    Assigned,
    InProgress,
    Resolved,
    Closed,
}

public enum WorkOrderPriority
{
    Normal,
    High,
}

public enum WorkOrderCategory
{
    Plumbing,
    Electrical,
    Heating,
    Appliances,
    Exterior,
    General,
}

/// <summary>
/// Persistence entity for a maintenance request. State changes go through
/// <see cref="WorkOrderWorkflow"/>; never set <see cref="Status"/> directly from an endpoint.
/// </summary>
public sealed class WorkOrder
{
    public Guid Id { get; set; }

    /// <summary>Human-friendly reference shown as "WO-1001".</summary>
    public long Number { get; set; }

    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public Guid PropertyId { get; set; }
    public Property Property { get; set; } = null!;

    public Guid RequesterId { get; set; }
    public ApplicationUser Requester { get; set; } = null!;

    public Guid? AssignedTechnicianId { get; set; }
    public ApplicationUser? AssignedTechnician { get; set; }

    public required string Title { get; set; }
    public required string Description { get; set; }
    public WorkOrderCategory Category { get; set; }
    public WorkOrderPriority Priority { get; set; }
    public WorkOrderStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }

    public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Npgsql maps a <c>uint</c> row version to PostgreSQL's
    /// built-in <c>xmin</c> system column, which changes on every update of the row.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// Full-text search document for Title + Description. A PostgreSQL generated column: the database
    /// computes it on every insert/update, so it can never drift from the text. Never set it in code.
    /// </summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}

internal sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 4000;
    public const int NoteMaxLength = 2000;

    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("WorkOrders", t =>
        {
            t.HasCheckConstraint("CK_WorkOrders_Status",
                "\"Status\" IN ('New', 'Assigned', 'InProgress', 'Resolved', 'Closed')");
            t.HasCheckConstraint("CK_WorkOrders_Priority", "\"Priority\" IN ('Normal', 'High')");
            t.HasCheckConstraint("CK_WorkOrders_Category",
                "\"Category\" IN ('Plumbing', 'Electrical', 'Heating', 'Appliances', 'Exterior', 'General')");
            // Database-level backstops for the workflow rules in WorkOrderWorkflow.
            t.HasCheckConstraint("CK_WorkOrders_AssignedUnlessNew",
                "(\"Status\" = 'New') = (\"AssignedTechnicianId\" IS NULL)");
            t.HasCheckConstraint("CK_WorkOrders_ResolutionRecorded",
                "\"Status\" NOT IN ('Resolved', 'Closed') OR (\"ResolutionNote\" IS NOT NULL AND \"ResolvedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_WorkOrders_ClosedAtRecorded",
                "\"Status\" <> 'Closed' OR \"ClosedAt\" IS NOT NULL");
            t.HasCheckConstraint("CK_WorkOrders_DueAfterCreated", "\"DueAt\" > \"CreatedAt\"");
        });

        builder.Property(w => w.Number).UseIdentityAlwaysColumn();
        builder.HasIndex(w => w.Number).IsUnique();

        builder.Property(w => w.Title).HasMaxLength(TitleMaxLength);
        builder.Property(w => w.Description).HasMaxLength(DescriptionMaxLength);
        builder.Property(w => w.ResolutionNote).HasMaxLength(NoteMaxLength);
        builder.Property(w => w.Category).HasConversion<string>().HasMaxLength(20);
        builder.Property(w => w.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(w => w.Version).IsRowVersion();

        builder.HasOne(w => w.Organization)
            .WithMany()
            .HasForeignKey(w => w.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Composite foreign keys include OrganizationId, so PostgreSQL rejects a work order
        // that points at a property or person from a different organization.
        builder.HasOne(w => w.Property)
            .WithMany()
            .HasForeignKey(w => new { w.OrganizationId, w.PropertyId })
            .HasPrincipalKey(p => new { p.OrganizationId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.Requester)
            .WithMany()
            .HasForeignKey(w => new { w.OrganizationId, w.RequesterId })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.AssignedTechnician)
            .WithMany()
            .HasForeignKey(w => new { w.OrganizationId, w.AssignedTechnicianId })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Lets comments and audit events reference "a work order in organization X".
        builder.HasAlternateKey(w => new { w.OrganizationId, w.Id });

        // Indexes for organization-scoped lists, filters, and deadline queries.
        builder.HasIndex(w => new { w.OrganizationId, w.CreatedAt, w.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_WorkOrders_Org_CreatedAt");
        builder.HasIndex(w => new { w.OrganizationId, w.Status })
            .HasDatabaseName("IX_WorkOrders_Org_Status");
        builder.HasIndex(w => new { w.OrganizationId, w.AssignedTechnicianId, w.Status })
            .HasDatabaseName("IX_WorkOrders_Org_Assignee_Status");
        builder.HasIndex(w => new { w.OrganizationId, w.RequesterId, w.CreatedAt })
            .HasDatabaseName("IX_WorkOrders_Org_Requester_CreatedAt");
        builder.HasIndex(w => new { w.OrganizationId, w.DueAt })
            .HasFilter("\"Status\" IN ('New', 'Assigned', 'InProgress')")
            .HasDatabaseName("IX_WorkOrders_Org_DueAt_Open");
        builder.HasIndex(w => new { w.OrganizationId, w.UpdatedAt, w.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_WorkOrders_Org_UpdatedAt");

        // English full-text search with stemming ("leaking" matches "leak"), served by a GIN index.
        builder.HasGeneratedTsVectorColumn(w => w.SearchVector, "english", w => new { w.Title, w.Description })
            .HasIndex(w => w.SearchVector)
            .HasMethod("GIN")
            .HasDatabaseName("IX_WorkOrders_SearchVector");
    }
}
