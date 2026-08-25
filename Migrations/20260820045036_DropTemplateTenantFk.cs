using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DailyPosterGenerator.Migrations
{
    /// <inheritdoc />
    public partial class DropTemplateTenantFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PosterTemplates_Tenants_TenantId",
                table: "PosterTemplates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_PosterTemplates_Tenants_TenantId",
                table: "PosterTemplates",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
