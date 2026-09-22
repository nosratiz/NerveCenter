using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SbConsole.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionSummaryColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SummaryJson",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummaryJson",
                table: "Connections");
        }
    }
}
