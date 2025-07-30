using FileConverter.Models;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class addAudioAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<AudioAnalysisData>(
                name: "AudioAnalysis",
                table: "MediaItems",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<AudioAnalysisData>(
                name: "AudioAnalysis",
                table: "ConversionJobs",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioAnalysis",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AudioAnalysis",
                table: "ConversionJobs");
        }
    }
}
