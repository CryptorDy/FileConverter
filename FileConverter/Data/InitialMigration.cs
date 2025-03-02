using Microsoft.EntityFrameworkCore.Migrations;

namespace FileConverter.Data
{
    /// <summary>
    /// Начальная миграция для создания базы данных
    /// </summary>
    public partial class InitialMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Создаем таблицу для BatchJob
            migrationBuilder.CreateTable(
                name: "BatchJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchJobs", x => x.Id);
                });

            // Создаем таблицу для ConversionJob
            migrationBuilder.CreateTable(
                name: "ConversionJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    VideoUrl = table.Column<string>(type: "text", nullable: false),
                    Mp3Url = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    BatchId = table.Column<string>(type: "text", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ContentType = table.Column<string>(type: "varchar", maxLength: 100, nullable: true),
                    ProcessingAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversionJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversionJobs_BatchJobs_BatchId",
                        column: x => x.BatchId,
                        principalTable: "BatchJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Создаем индекс для BatchId
            migrationBuilder.CreateIndex(
                name: "IX_ConversionJobs_BatchId",
                table: "ConversionJobs",
                column: "BatchId");

            // Создаем индекс для Status
            migrationBuilder.CreateIndex(
                name: "IX_ConversionJobs_Status",
                table: "ConversionJobs",
                column: "Status");

            // Создаем индекс для CreatedAt
            migrationBuilder.CreateIndex(
                name: "IX_ConversionJobs_CreatedAt",
                table: "ConversionJobs",
                column: "CreatedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Удаляем таблицу ConversionJobs
            migrationBuilder.DropTable(
                name: "ConversionJobs");

            // Удаляем таблицу BatchJobs
            migrationBuilder.DropTable(
                name: "BatchJobs");
        }
    }
} 