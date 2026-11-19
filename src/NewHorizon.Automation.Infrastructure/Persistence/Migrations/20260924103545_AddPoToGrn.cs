using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPoToGrn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoGrnAutomationConfig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RunMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScheduleTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    ReceiptMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Sites = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    MaxPosPerRun = table.Column<int>(type: "int", nullable: true),
                    LastScheduledRunDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LastRunReference = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoGrnAutomationConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PoGrnRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TriggerReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceiptMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedSites = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PosExamined = table.Column<int>(type: "int", nullable: false),
                    GrnsCreated = table.Column<int>(type: "int", nullable: false),
                    PosSkipped = table.Column<int>(type: "int", nullable: false),
                    PosFailed = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoGrnRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PoGrnReceipt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoId = table.Column<long>(type: "bigint", nullable: false),
                    PoNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PoType = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    VendorCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    GrnId = table.Column<long>(type: "bigint", nullable: true),
                    GrnNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LinesReceived = table.Column<int>(type: "int", nullable: false),
                    LinesSkipped = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoGrnReceipt", x => x.Id);
                    table.CheckConstraint("CK_PoGrnReceipt_Result", "([Status] = 'Created' AND [GrnNumber] IS NOT NULL) OR ([Status] <> 'Created' AND [Reason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PoGrnReceipt_PoGrnRun_RunId",
                        column: x => x.RunId,
                        principalTable: "PoGrnRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "PoGrnAutomationConfig",
                columns: new[] { "Id", "DryRun", "InvoiceNumber", "IsActive", "LastRunReference", "LastRunStatus", "LastScheduledRunDate", "LastTriggeredAtUtc", "MaxPosPerRun", "ReceiptMode", "RunMode", "ScheduleTime", "Sites", "UpdatedAtUtc", "UpdatedBy" },
                values: new object[] { new Guid("7a2e5d30-0000-0000-0000-000000000001"), false, null, false, null, null, null, null, null, "Complete", "Api", null, null, new DateTimeOffset(new DateTime(2026, 9, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null });

            migrationBuilder.CreateIndex(
                name: "IX_PoGrnReceipt_PoId_RecordedAtUtc",
                table: "PoGrnReceipt",
                columns: new[] { "PoId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PoGrnReceipt_RunId",
                table: "PoGrnReceipt",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PoGrnRun_StartedAtUtc",
                table: "PoGrnRun",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoGrnAutomationConfig");

            migrationBuilder.DropTable(
                name: "PoGrnReceipt");

            migrationBuilder.DropTable(
                name: "PoGrnRun");
        }
    }
}
