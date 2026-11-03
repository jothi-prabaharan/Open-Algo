using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SandboxRowVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_positions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_orders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_funds",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_positions");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_orders");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "sandbox",
                table: "sandbox_funds");
        }
    }
}
