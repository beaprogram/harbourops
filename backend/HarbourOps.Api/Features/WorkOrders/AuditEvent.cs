using HarbourOps.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.WorkOrders;

public enum AuditEventType
{
    Created,
    PriorityChanged,
    Assigned,
    Reassigned,
    Started,
    Resolved,
    Reopened,
    Closed,
}

/// <summary>
/// One entry in a work order's activity history. Written in the same database transaction as
/// the change it describes, so history can never disagree with the work order itself.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;
    public Guid ActorId { get; set; }
    public ApplicationUser Actor { get; set; } = null!;
    public AuditEventType Type { get; set; }
    public WorkOrderStatus? FromStatus { get; set; }
    public WorkOrderStatus? ToStatus { get; set; }

    /// <summary>Human-readable description captured at the time of the event.</summary>
    public required string Summary { get; set; }

    /// <summary>Resolution note or reopen reason, when the action requires one.</summary>
    public string? Note { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Summary).HasMaxLength(300);
        builder.Property(e => e.Note).HasMaxLength(WorkOrderConfiguration.NoteMaxLength);

        builder.HasOne(e => e.WorkOrder)
            .WithMany()
            .HasForeignKey(e => new { e.OrganizationId, e.WorkOrderId })
            .HasPrincipalKey(w => new { w.OrganizationId, w.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Actor)
            .WithMany()
            .HasForeignKey(e => new { e.OrganizationId, e.ActorId })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrganizationId, e.WorkOrderId, e.OccurredAt })
            .HasDatabaseName("IX_AuditEvents_Org_WorkOrder_OccurredAt");
    }
}
