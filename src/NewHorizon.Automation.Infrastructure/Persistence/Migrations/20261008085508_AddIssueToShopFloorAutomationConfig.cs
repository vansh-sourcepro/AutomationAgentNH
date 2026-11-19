using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueToShopFloorAutomationConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssueToShopFloorAutomationConfig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RunMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduleTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Sites = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DocumentNumbers = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    MaxDocumentsPerRun = table.Column<int>(type: "int", nullable: true),
                    LastScheduledRunDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastRunReference = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueToShopFloorAutomationConfig", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "IssueToShopFloorAutomationConfig",
                columns: new[] { "Id", "DocumentNumbers", "DryRun", "IsActive", "LastRunReference", "LastRunStatus", "LastScheduledRunDate", "LastTriggeredAtUtc", "MaxDocumentsPerRun", "RunMode", "ScheduleTime", "Sites", "Source", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("7a2e5d30-0000-0000-0000-00000000000a"), null, false, false, null, null, null, null, null, "Api", null, null, "Sjo", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null },
                    { new Guid("7a2e5d30-0000-0000-0000-00000000000b"), null, false, false, null, null, null, null, null, "Api", null, null, "WorkOrder", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null },
                    { new Guid("7a2e5d30-0000-0000-0000-00000000000c"), null, false, false, null, null, null, null, null, "Api", null, null, "SalesOaf", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null }
                });

            migrationBuilder.CreateIndex(
                name: "UX_IssueToShopFloorAutomationConfig_Source",
                table: "IssueToShopFloorAutomationConfig",
                column: "Source",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueToShopFloorAutomationConfig");
        }
    }
}
