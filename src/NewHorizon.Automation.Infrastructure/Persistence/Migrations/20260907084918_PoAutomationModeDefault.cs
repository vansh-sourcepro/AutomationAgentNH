using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PoAutomationModeDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000a"),
                column: "RunMode",
                value: "Api");

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000b"),
                column: "RunMode",
                value: "Api");

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000c"),
                column: "RunMode",
                value: "Api");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000a"),
                column: "RunMode",
                value: "Disabled");

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000b"),
                column: "RunMode",
                value: "Disabled");

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000c"),
                column: "RunMode",
                value: "Disabled");
        }
    }
}
