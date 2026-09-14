using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SbConsole.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionTestResultColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastTestError",
                table: "Connections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastTestSucceeded",
                table: "Connections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastTestedAt",
                table: "Connections",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTestError",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastTestSucceeded",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastTestedAt",
                table: "Connections");
        }
    }
}
