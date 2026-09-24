using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndentNumbersToPoAutomationConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IndentNumbers",
                table: "IndentPoAutomationConfig",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000a"),
                column: "IndentNumbers",
                value: null);

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000b"),
                column: "IndentNumbers",
                value: null);

            migrationBuilder.UpdateData(
                table: "IndentPoAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("6f1d4c20-0000-0000-0000-00000000000c"),
                column: "IndentNumbers",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IndentNumbers",
                table: "IndentPoAutomationConfig");
        }
    }
}
