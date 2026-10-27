using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HarbourOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DemoSandboxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DemoSandboxId",
                table: "Organizations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DemoSandboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CleanedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WorkOrderCount = table.Column<int>(type: "integer", nullable: false),
                    CommentCount = table.Column<int>(type: "integer", nullable: false),
                    ActionCount = table.Column<int>(type: "integer", nullable: false),
                    UploadCount = table.Column<int>(type: "integer", nullable: false),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    StoredBytes = table.Column<long>(type: "bigint", nullable: false),
                    ExportCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoSandboxes", x => x.Id);
                    table.CheckConstraint("CK_DemoSandboxes_Cleaned", "\"CleanedAt\" IS NULL OR \"RevokedAt\" IS NOT NULL");
                    table.CheckConstraint("CK_DemoSandboxes_Counters", "\"WorkOrderCount\" >= 0 AND \"CommentCount\" >= 0 AND \"ActionCount\" >= 0 AND \"UploadCount\" >= 0 AND \"AttachmentCount\" >= 0 AND \"StoredBytes\" >= 0 AND \"ExportCount\" >= 0");
                    table.CheckConstraint("CK_DemoSandboxes_CredentialHash", "octet_length(\"CredentialHash\") = 32");
                    table.CheckConstraint("CK_DemoSandboxes_Lifetime", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_DemoSandboxes_RevokedReason", "\"RevokedReason\" IS NULL OR \"RevokedReason\" IN ('expired', 'reset', 'ended', 'failed', 'disabled')");
                });

            migrationBuilder.CreateTable(
                name: "DemoPersonas",
                columns: table => new
                {
                    SandboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoPersonas", x => new { x.SandboxId, x.Key });
                    table.CheckConstraint("CK_DemoPersonas_Key", "\"Key\" IN ('manager', 'technician', 'requester', 'otherManager')");
                    table.ForeignKey(
                        name: "FK_DemoPersonas_AspNetUsers_OrganizationId_UserId",
                        columns: x => new { x.OrganizationId, x.UserId },
                        principalTable: "AspNetUsers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoPersonas_DemoSandboxes_SandboxId",
                        column: x => x.SandboxId,
                        principalTable: "DemoSandboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_DemoSandboxId",
                table: "Organizations",
                column: "DemoSandboxId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoPersonas_OrganizationId_UserId",
                table: "DemoPersonas",
                columns: new[] { "OrganizationId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoPersonas_UserId",
                table: "DemoPersonas",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoSandboxes_CredentialHash",
                table: "DemoSandboxes",
                column: "CredentialHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoSandboxes_Uncleaned",
                table: "DemoSandboxes",
                column: "CreatedAt",
                filter: "\"CleanedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Organizations_DemoSandboxes_DemoSandboxId",
                table: "Organizations",
                column: "DemoSandboxId",
                principalTable: "DemoSandboxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Organizations_DemoSandboxes_DemoSandboxId",
                table: "Organizations");

            migrationBuilder.DropTable(
                name: "DemoPersonas");

            migrationBuilder.DropTable(
                name: "DemoSandboxes");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_DemoSandboxId",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "DemoSandboxId",
                table: "Organizations");
        }
    }
}
