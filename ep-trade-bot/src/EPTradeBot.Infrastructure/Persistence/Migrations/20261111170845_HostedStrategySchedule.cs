using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostedStrategySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "exchange",
                schema: "core",
                table: "hosted_strategies",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "NSE");

            migrationBuilder.AddColumn<bool>(
                name: "manually_stopped",
                schema: "core",
                table: "hosted_strategies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "paused_reason",
                schema: "core",
                table: "hosted_strategies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "exchange",
                schema: "core",
                table: "hosted_strategies");

            migrationBuilder.DropColumn(
                name: "manually_stopped",
                schema: "core",
                table: "hosted_strategies");

            migrationBuilder.DropColumn(
                name: "paused_reason",
                schema: "core",
                table: "hosted_strategies");
        }
    }
}
