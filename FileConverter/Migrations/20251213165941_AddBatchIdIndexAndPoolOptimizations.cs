using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchIdIndexAndPoolOptimizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Добавляем индекс на BatchId для ускорения запросов batch-status
            migrationBuilder.CreateIndex(
                name: "IX_ConversionJobs_BatchId",
                table: "ConversionJobs",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversionJobs_BatchId",
                table: "ConversionJobs");
        }
    }
}
