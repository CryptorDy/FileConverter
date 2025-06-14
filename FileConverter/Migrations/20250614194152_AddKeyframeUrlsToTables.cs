using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyframeUrlsToTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "KeyframeUrls",
                table: "MediaItems",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "KeyframeUrls",
                table: "ConversionJobs",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyframeUrls",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "KeyframeUrls",
                table: "ConversionJobs");
        }
    }
}
