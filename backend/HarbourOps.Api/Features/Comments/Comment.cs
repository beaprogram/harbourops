using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Comments;

/// <summary>
/// A plain-text discussion comment on a work order. Comments are deliberately separate from the
/// workflow: adding one does not change the work order's status, Version, UpdatedAt, or activity history.
/// Comments are immutable in this release (no edit or delete).
/// </summary>
public sealed class Comment
{
    public const int BodyMaxLength = 2000;

    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public ApplicationUser Author { get; set; } = null!;
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> builder)
    {
        builder.ToTable("Comments", t =>
            t.HasCheckConstraint("CK_Comments_BodyNotBlank", "length(btrim(\"Body\", E' \\t\\n\\r')) > 0"));
        builder.Property(c => c.Body).HasMaxLength(Comment.BodyMaxLength);

        builder.HasOne(c => c.WorkOrder)
            .WithMany()
            .HasForeignKey(c => new { c.OrganizationId, c.WorkOrderId })
            .HasPrincipalKey(w => new { w.OrganizationId, w.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Author)
            .WithMany()
            .HasForeignKey(c => new { c.OrganizationId, c.AuthorId })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Matches the keyset pagination order exactly: (CreatedAt, Id) within one work order.
        builder.HasIndex(c => new { c.OrganizationId, c.WorkOrderId, c.CreatedAt, c.Id })
            .HasDatabaseName("IX_Comments_Org_WorkOrder_CreatedAt_Id");
    }
}
