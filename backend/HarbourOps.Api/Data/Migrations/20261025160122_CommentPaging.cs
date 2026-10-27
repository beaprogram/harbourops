using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HarbourOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentPaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_Org_WorkOrder_CreatedAt",
                table: "Comments");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_Org_WorkOrder_CreatedAt_Id",
                table: "Comments",
                columns: new[] { "OrganizationId", "WorkOrderId", "CreatedAt", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Comments_BodyNotBlank",
                table: "Comments",
                sql: "length(btrim(\"Body\", E' \\t\\n\\r')) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_Org_WorkOrder_CreatedAt_Id",
                table: "Comments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Comments_BodyNotBlank",
                table: "Comments");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_Org_WorkOrder_CreatedAt",
                table: "Comments",
                columns: new[] { "OrganizationId", "WorkOrderId", "CreatedAt" });
        }
    }
}
