using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAutomationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationConfig",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Module = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EnableAgent = table.Column<bool>(type: "bit", nullable: false),
                    EnableModule = table.Column<bool>(type: "bit", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    ReconcileIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    WorkingHoursStart = table.Column<TimeOnly>(type: "time", nullable: true),
                    WorkingHoursEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    ParallelWorkers = table.Column<int>(type: "int", nullable: false),
                    LoggingLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsLicensed = table.Column<bool>(type: "bit", nullable: false),
                    PayloadRetentionDays = table.Column<int>(type: "int", nullable: false),
                    LogRetentionDays = table.Column<int>(type: "int", nullable: false),
                    ErrorRetentionDays = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationError",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErrorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TechnicalMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LaymanMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApiEndpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationError", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationJob",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkflowType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CurrentStage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ApiEndpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutomationJobStep",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OperationName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    RequestPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponsePayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErpDocumentRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationJobStep", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationJobStep_AutomationJob_JobId",
                        column: x => x.JobId,
                        principalTable: "AutomationJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AutomationConfig_Module",
                table: "AutomationConfig",
                column: "Module",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationError_CreatedAtUtc",
                table: "AutomationError",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationError_JobId",
                table: "AutomationError",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJob_Claim",
                table: "AutomationJob",
                columns: new[] { "Status", "Priority", "CreatedAtUtc" })
                .Annotation("SqlServer:Include", new[] { "NotBeforeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJob_CreatedAtUtc",
                table: "AutomationJob",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJob_DocumentId",
                table: "AutomationJob",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_IdempotencyKey_Live",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[Status] <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJobStep_Job_Status",
                table: "AutomationJobStep",
                columns: new[] { "JobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJobStep_Job_Sequence",
                table: "AutomationJobStep",
                columns: new[] { "JobId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationLog_CorrelationId",
                table: "AutomationLog",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationLog_JobId",
                table: "AutomationLog",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationLog_StartedAtUtc",
                table: "AutomationLog",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationConfig");

            migrationBuilder.DropTable(
                name: "AutomationError");

            migrationBuilder.DropTable(
                name: "AutomationJobStep");

            migrationBuilder.DropTable(
                name: "AutomationLog");

            migrationBuilder.DropTable(
                name: "AutomationJob");
        }
    }
}
