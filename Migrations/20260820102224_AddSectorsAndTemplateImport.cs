using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DailyPosterGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddSectorsAndTemplateImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sector",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BackgroundDim",
                table: "PosterTemplates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundImagePath",
                table: "PosterTemplates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsImported",
                table: "PosterTemplates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Sector",
                table: "PosterTemplates",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TextColor",
                table: "PosterTemplates",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextRegionsJson",
                table: "PosterTemplates",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sector",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BackgroundDim",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "BackgroundImagePath",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "IsImported",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "Sector",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "TextColor",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "TextRegionsJson",
                table: "PosterTemplates");
        }
    }
}
