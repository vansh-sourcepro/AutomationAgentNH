using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueToShopFloorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssueToShopFloorConversion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    SiteCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TotalAttempts = table.Column<int>(type: "int", nullable: false),
                    SuccessfulAttempts = table.Column<int>(type: "int", nullable: false),
                    IsTerminal = table.Column<bool>(type: "bit", nullable: false),
                    TerminalIssueNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueToShopFloorConversion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IssueToShopFloorOutcome",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IssueNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    TotalQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    RefusalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ChecksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ShortagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueToShopFloorOutcome", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueToShopFloorOutcome_AutomationJob_JobId",
                        column: x => x.JobId,
                        principalTable: "AutomationJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueToShopFloorConversion_DocNo",
                table: "IssueToShopFloorConversion",
                column: "DocumentNumber");

            migrationBuilder.CreateIndex(
                name: "IX_IssueToShopFloorConversion_SiteId",
                table: "IssueToShopFloorConversion",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "UX_IssueToShopFloorConversion_Document",
                table: "IssueToShopFloorConversion",
                columns: new[] { "IssueSource", "DocumentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueToShopFloorOutcome_DocNo",
                table: "IssueToShopFloorOutcome",
                column: "DocumentNumber");

            migrationBuilder.CreateIndex(
                name: "IX_IssueToShopFloorOutcome_IssueNo",
                table: "IssueToShopFloorOutcome",
                column: "IssueNumber");

            migrationBuilder.CreateIndex(
                name: "UX_IssueToShopFloorOutcome_JobId",
                table: "IssueToShopFloorOutcome",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueToShopFloorConversion");

            migrationBuilder.DropTable(
                name: "IssueToShopFloorOutcome");
        }
    }
}
