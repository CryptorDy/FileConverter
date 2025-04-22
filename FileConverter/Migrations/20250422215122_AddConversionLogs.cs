using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileConverter.Migrations
{
    /// <inheritdoc />
    public partial class AddConversionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversionLogs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    JobId = table.Column<string>(type: "text", nullable: false),
                    BatchId = table.Column<string>(type: "text", nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    JobStatus = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    VideoUrl = table.Column<string>(type: "text", nullable: true),
                    Mp3Url = table.Column<string>(type: "text", nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ProcessingRateBytesPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    Step = table.Column<int>(type: "integer", nullable: true),
                    TotalSteps = table.Column<int>(type: "integer", nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    QueueTimeMs = table.Column<long>(type: "bigint", nullable: true),
                    WaitReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversionLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversionLogs_BatchId",
                table: "ConversionLogs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversionLogs_EventType",
                table: "ConversionLogs",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_ConversionLogs_JobId",
                table: "ConversionLogs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversionLogs_JobId_Timestamp",
                table: "ConversionLogs",
                columns: new[] { "JobId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversionLogs_Timestamp",
                table: "ConversionLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversionLogs");
        }
    }
}
