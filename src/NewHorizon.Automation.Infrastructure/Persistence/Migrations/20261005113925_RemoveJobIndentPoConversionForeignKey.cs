using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveJobIndentPoConversionForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationJob_IndentPoConversion_ConversionId",
                table: "AutomationJob");

            migrationBuilder.Sql(
                """
                UPDATE j
                SET j.ConversionId = c.Id
                FROM AutomationJob j
                INNER JOIN IssueToShopFloorConversion c ON j.DocumentId = c.DocumentNumber
                WHERE j.WorkflowType = 'IssueToShopFloor' AND j.ConversionId IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_AutomationJob_IndentPoConversion_ConversionId",
                table: "AutomationJob",
                column: "ConversionId",
                principalTable: "IndentPoConversion",
                principalColumn: "Id");
        }
    }
}
