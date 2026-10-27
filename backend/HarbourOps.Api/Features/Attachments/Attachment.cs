using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HarbourOps.Api.Features.Attachments;

/// <summary>
/// Metadata for one photo. The bytes live in <see cref="Storage.IAttachmentStorage"/> under two opaque keys:
/// the full-size image and a small preview. A photo exists for users only once this row is committed.
/// Like comments, attachments are supporting material: adding one does not change the work order's
/// Status, Version, UpdatedAt, or activity timeline. They cannot be edited or deleted in this release.
/// </summary>
public sealed class Attachment
{
    public const int FileNameMaxLength = 120;

    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;
    public Guid UploadedById { get; set; }
    public ApplicationUser UploadedBy { get; set; } = null!;
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Sanitised display name, always ending in .jpg because the stored image is a JPEG.</summary>
    public required string FileName { get; set; }

    /// <summary>Media type of the stored image (always image/jpeg after re-encoding).</summary>
    public required string MediaType { get; set; }

    /// <summary>Format detected from the uploaded bytes (image/jpeg or image/png), for information only.</summary>
    public required string SourceMediaType { get; set; }

    public required string StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public required string PreviewStorageKey { get; set; }
    public long PreviewSizeBytes { get; set; }
    public int PreviewWidth { get; set; }
    public int PreviewHeight { get; set; }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments", t =>
        {
            t.HasCheckConstraint("CK_Attachments_MediaType", "\"MediaType\" = 'image/jpeg'");
            t.HasCheckConstraint("CK_Attachments_SourceMediaType", "\"SourceMediaType\" IN ('image/jpeg', 'image/png')");
            t.HasCheckConstraint("CK_Attachments_StorageKeys",
                "\"StorageKey\" ~ '^[0-9a-f]{32}$' AND \"PreviewStorageKey\" ~ '^[0-9a-f]{32}$' AND \"StorageKey\" <> \"PreviewStorageKey\"");
            t.HasCheckConstraint("CK_Attachments_Sizes",
                "\"SizeBytes\" > 0 AND \"Width\" > 0 AND \"Height\" > 0 AND \"PreviewSizeBytes\" > 0 AND \"PreviewWidth\" > 0 AND \"PreviewHeight\" > 0");
        });

        builder.Property(a => a.FileName).HasMaxLength(Attachment.FileNameMaxLength);
        builder.Property(a => a.MediaType).HasMaxLength(20);
        builder.Property(a => a.SourceMediaType).HasMaxLength(20);
        builder.Property(a => a.StorageKey).HasMaxLength(32);
        builder.Property(a => a.PreviewStorageKey).HasMaxLength(32);
        builder.HasIndex(a => a.StorageKey).IsUnique();
        builder.HasIndex(a => a.PreviewStorageKey).IsUnique();

        // Organization-scoped composite foreign keys: an attachment can only point at a work order and an
        // uploader from its own organization, even if application code were wrong.
        builder.HasOne(a => a.WorkOrder)
            .WithMany()
            .HasForeignKey(a => new { a.OrganizationId, a.WorkOrderId })
            .HasPrincipalKey(w => new { w.OrganizationId, w.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.UploadedBy)
            .WithMany()
            .HasForeignKey(a => new { a.OrganizationId, a.UploadedById })
            .HasPrincipalKey(u => new { u.OrganizationId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.OrganizationId, a.WorkOrderId, a.UploadedAt, a.Id })
            .HasDatabaseName("IX_Attachments_Org_WorkOrder_UploadedAt_Id");
    }
}
