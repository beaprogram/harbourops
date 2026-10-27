using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace HarbourOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "WorkOrders",
                type: "tsvector",
                nullable: false)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "Description" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_Org_UpdatedAt",
                table: "WorkOrders",
                columns: new[] { "OrganizationId", "UpdatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_SearchVector",
                table: "WorkOrders",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_Org_UpdatedAt",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_SearchVector",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "WorkOrders");
        }
    }
}
