using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndentPoAutomationConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndentPoAutomationConfig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IndentKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RunMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduleTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Sites = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    MaxIndentsPerRun = table.Column<int>(type: "int", nullable: true),
                    LastScheduledRunDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastRunReference = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndentPoAutomationConfig", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "IndentPoAutomationConfig",
                columns: new[] { "Id", "DryRun", "IndentKind", "IsActive", "LastRunReference", "LastRunStatus", "LastScheduledRunDate", "LastTriggeredAtUtc", "MaxIndentsPerRun", "RunMode", "ScheduleTime", "Sites", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("6f1d4c20-0000-0000-0000-00000000000a"), false, "Regular", false, null, null, null, null, null, "Disabled", null, null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null },
                    { new Guid("6f1d4c20-0000-0000-0000-00000000000b"), false, "Capital", false, null, null, null, null, null, "Disabled", null, null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null },
                    { new Guid("6f1d4c20-0000-0000-0000-00000000000c"), false, "Service", false, null, null, null, null, null, "Disabled", null, null, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null }
                });

            migrationBuilder.CreateIndex(
                name: "UX_IndentPoAutomationConfig_IndentKind",
                table: "IndentPoAutomationConfig",
                column: "IndentKind",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndentPoAutomationConfig");
        }
    }
}
