using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NewVideoUrl",
                table: "ConversionJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoHash",
                table: "ConversionJobs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    VideoHash = table.Column<string>(type: "text", nullable: false),
                    VideoUrl = table.Column<string>(type: "text", nullable: false),
                    AudioUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAccessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_VideoHash",
                table: "MediaItems",
                column: "VideoHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaItems");

            migrationBuilder.DropColumn(
                name: "NewVideoUrl",
                table: "ConversionJobs");

            migrationBuilder.DropColumn(
                name: "VideoHash",
                table: "ConversionJobs");
        }
    }
}
