using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndentToPoTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_IdempotencyKey_Live",
                table: "AutomationJob");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversionId",
                table: "AutomationJob",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "AutomationJob",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "AutomationJob",
                type: "bigint",
                nullable: true,
                computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])");

            migrationBuilder.CreateTable(
                name: "AutomationRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkflowType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TriggerSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TriggerReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RequestedIndentTypes = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    RequestedSites = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxIndents = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IndentsExamined = table.Column<int>(type: "int", nullable: false),
                    JobsCreated = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrdersCreated = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true, computedColumnSql: "DATEDIFF_BIG(MILLISECOND, [StartedAtUtc], [CompletedAtUtc])")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndentPoConversion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IndentId = table.Column<long>(type: "bigint", nullable: false),
                    IndentKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IndentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    IndentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndentPoConversion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndentPoOutcome",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VendorCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    RateStructureCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PoId = table.Column<long>(type: "bigint", nullable: true),
                    PoNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndentPoOutcome", x => x.Id);
                    table.CheckConstraint("CK_IndentPoOutcome_Result", "([Outcome] = 'Created' AND [PoId] IS NOT NULL AND [PoNumber] IS NOT NULL) OR ([Outcome] <> 'Created' AND [Reason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_IndentPoOutcome_AutomationJobStep_StepId",
                        column: x => x.StepId,
                        principalTable: "AutomationJobStep",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IndentPoOutcome_AutomationJob_JobId",
                        column: x => x.JobId,
                        principalTable: "AutomationJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJob_Conversion",
                table: "AutomationJob",
                columns: new[] { "ConversionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJob_RunId",
                table: "AutomationJob",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_IdempotencyKey_Live",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[Status] <> 'Cancelled' AND [ConversionId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[ConversionId] IS NOT NULL AND [Status] <> 'Completed' AND [Status] <> 'Cancelled' AND [Status] <> 'Failed'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRun_CorrelationId",
                table: "AutomationRun",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRun_StartedAtUtc",
                table: "AutomationRun",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRun_Trigger",
                table: "AutomationRun",
                columns: new[] { "TriggerSource", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IndentPoConversion_IndentNumber",
                table: "IndentPoConversion",
                column: "IndentNumber");

            migrationBuilder.CreateIndex(
                name: "IX_IndentPoConversion_SiteId",
                table: "IndentPoConversion",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "UX_IndentPoConversion_Indent",
                table: "IndentPoConversion",
                columns: new[] { "IndentKind", "IndentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndentPoOutcome_StepId",
                table: "IndentPoOutcome",
                column: "StepId");

            migrationBuilder.CreateIndex(
                name: "IX_IndentPoOutcome_VendorCode",
                table: "IndentPoOutcome",
                column: "VendorCode");

            migrationBuilder.CreateIndex(
                name: "UX_IndentPoOutcome_Job_Sequence",
                table: "IndentPoOutcome",
                columns: new[] { "JobId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_IndentPoOutcome_PoId",
                table: "IndentPoOutcome",
                column: "PoId",
                unique: true,
                filter: "[PoId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationJob_AutomationRun_RunId",
                table: "AutomationJob",
                column: "RunId",
                principalTable: "AutomationRun",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationJob_IndentPoConversion_ConversionId",
                table: "AutomationJob",
                column: "ConversionId",
                principalTable: "IndentPoConversion",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationJob_AutomationRun_RunId",
                table: "AutomationJob");

            migrationBuilder.DropForeignKey(
                name: "FK_AutomationJob_IndentPoConversion_ConversionId",
                table: "AutomationJob");

            migrationBuilder.DropTable(
                name: "AutomationRun");

            migrationBuilder.DropTable(
                name: "IndentPoConversion");

            migrationBuilder.DropTable(
                name: "IndentPoOutcome");

            migrationBuilder.DropIndex(
                name: "IX_AutomationJob_Conversion",
                table: "AutomationJob");

            migrationBuilder.DropIndex(
                name: "IX_AutomationJob_RunId",
                table: "AutomationJob");

            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_IdempotencyKey_Live",
                table: "AutomationJob");

            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "AutomationJob");

            migrationBuilder.DropColumn(
                name: "ConversionId",
                table: "AutomationJob");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "AutomationJob");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_IdempotencyKey_Live",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[Status] <> 'Cancelled'");
        }
    }
}
