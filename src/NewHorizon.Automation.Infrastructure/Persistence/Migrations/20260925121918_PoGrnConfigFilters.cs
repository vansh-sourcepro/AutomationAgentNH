using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHorizon.Automation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PoGrnConfigFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PoNumbers",
                table: "PoGrnAutomationConfig",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PoTypes",
                table: "PoGrnAutomationConfig",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "PoGrnAutomationConfig",
                keyColumn: "Id",
                keyValue: new Guid("7a2e5d30-0000-0000-0000-000000000001"),
                columns: new[] { "PoNumbers", "PoTypes" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PoNumbers",
                table: "PoGrnAutomationConfig");

            migrationBuilder.DropColumn(
                name: "PoTypes",
                table: "PoGrnAutomationConfig");
        }
    }
}
