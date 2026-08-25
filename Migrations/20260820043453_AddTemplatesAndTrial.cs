using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DailyPosterGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplatesAndTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TemplateId",
                table: "Posters",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateName",
                table: "Posters",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PosterTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Theme = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AccentColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ThumbnailPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosterTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosterTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posters_TemplateId",
                table: "Posters",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PosterTemplates_TenantId_Name",
                table: "PosterTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Posters_PosterTemplates_TemplateId",
                table: "Posters",
                column: "TemplateId",
                principalTable: "PosterTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Posters_PosterTemplates_TemplateId",
                table: "Posters");

            migrationBuilder.DropTable(
                name: "PosterTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Posters_TemplateId",
                table: "Posters");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "Posters");

            migrationBuilder.DropColumn(
                name: "TemplateName",
                table: "Posters");
        }
    }
}
