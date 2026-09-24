using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSkippedJobStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[ConversionId] IS NOT NULL AND [Status] <> 'Completed' AND [Status] <> 'Cancelled' AND [Status] <> 'Failed' AND [Status] <> 'Skipped'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_LiveIndentConversion",
                table: "AutomationJob",
                column: "IdempotencyKey",
                unique: true,
                filter: "[ConversionId] IS NOT NULL AND [Status] <> 'Completed' AND [Status] <> 'Cancelled' AND [Status] <> 'Failed'");
        }
    }
}
