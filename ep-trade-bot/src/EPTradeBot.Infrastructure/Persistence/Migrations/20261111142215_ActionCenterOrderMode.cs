using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActionCenterOrderMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_mode",
                schema: "core",
                table: "account_api_keys",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_api_keys_order_mode",
                schema: "core",
                table: "account_api_keys",
                sql: "order_mode in ('auto', 'semi_auto')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_api_keys_order_mode",
                schema: "core",
                table: "account_api_keys");

            migrationBuilder.DropColumn(
                name: "order_mode",
                schema: "core",
                table: "account_api_keys");
        }
    }
}
