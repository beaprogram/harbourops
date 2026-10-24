using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Comments;

/// <summary>
/// A discussion comment on a work order. The table ships in the first migration so the
/// organization-scoped foreign keys are in place; the comment endpoints arrive in the next milestone.
/// </summary>
public sealed class Comment
{
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
        builder.ToTable("Comments");
        builder.Property(c => c.Body).HasMaxLength(WorkOrderConfiguration.NoteMaxLength);

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

        builder.HasIndex(c => new { c.OrganizationId, c.WorkOrderId, c.CreatedAt })
            .HasDatabaseName("IX_Comments_Org_WorkOrder_CreatedAt");
    }
}
