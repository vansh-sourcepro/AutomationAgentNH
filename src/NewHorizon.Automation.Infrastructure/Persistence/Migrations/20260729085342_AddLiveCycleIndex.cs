using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveCycleIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_AutomationJob_LiveCycle",
                table: "AutomationJob",
                column: "WorkflowType",
                unique: true,
                filter: "[DocumentType] = 'Cycle' AND [Status] <> 'Completed' AND [Status] <> 'Cancelled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AutomationJob_LiveCycle",
                table: "AutomationJob");
        }
    }
}
