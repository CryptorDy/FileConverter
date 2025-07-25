using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoDurationToTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "MediaItems",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "ConversionJobs",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "ConversionJobs");
        }
    }
}
