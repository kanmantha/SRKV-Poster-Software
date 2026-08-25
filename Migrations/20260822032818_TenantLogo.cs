using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DailyPosterGenerator.Migrations
{
    /// <inheritdoc />
    public partial class TenantLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Tenants",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Tenants");
        }
    }
}
