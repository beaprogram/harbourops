using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HarbourOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedById = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceMediaType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    PreviewStorageKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviewSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    PreviewWidth = table.Column<int>(type: "integer", nullable: false),
                    PreviewHeight = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.CheckConstraint("CK_Attachments_MediaType", "\"MediaType\" = 'image/jpeg'");
                    table.CheckConstraint("CK_Attachments_Sizes", "\"SizeBytes\" > 0 AND \"Width\" > 0 AND \"Height\" > 0 AND \"PreviewSizeBytes\" > 0 AND \"PreviewWidth\" > 0 AND \"PreviewHeight\" > 0");
                    table.CheckConstraint("CK_Attachments_SourceMediaType", "\"SourceMediaType\" IN ('image/jpeg', 'image/png')");
                    table.CheckConstraint("CK_Attachments_StorageKeys", "\"StorageKey\" ~ '^[0-9a-f]{32}$' AND \"PreviewStorageKey\" ~ '^[0-9a-f]{32}$' AND \"StorageKey\" <> \"PreviewStorageKey\"");
                    table.ForeignKey(
                        name: "FK_Attachments_AspNetUsers_OrganizationId_UploadedById",
                        columns: x => new { x.OrganizationId, x.UploadedById },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Attachments_WorkOrders_OrganizationId_WorkOrderId",
                        columns: x => new { x.OrganizationId, x.WorkOrderId },
                        principalTable: "WorkOrders",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_Org_WorkOrder_UploadedAt_Id",
                table: "Attachments",
                columns: new[] { "OrganizationId", "WorkOrderId", "UploadedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_OrganizationId_UploadedById",
                table: "Attachments",
                columns: new[] { "OrganizationId", "UploadedById" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_PreviewStorageKey",
                table: "Attachments",
                column: "PreviewStorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_StorageKey",
                table: "Attachments",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
