using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DailyPosterGenerator.Migrations
{
    /// <inheritdoc />
    public partial class TemplateEditableOriginal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportBoxesJson",
                table: "PosterTemplates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalBackgroundPath",
                table: "PosterTemplates",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportBoxesJson",
                table: "PosterTemplates");

            migrationBuilder.DropColumn(
                name: "OriginalBackgroundPath",
                table: "PosterTemplates");
        }
    }
}
