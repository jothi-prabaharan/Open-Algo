using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeysAndBrokerSymbols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "broker",
                schema: "core",
                table: "symtoken",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "account_api_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_api_keys_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_broker_brsymbol_exchange",
                schema: "core",
                table: "symtoken",
                columns: new[] { "broker", "brsymbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_broker_symbol_exchange",
                schema: "core",
                table: "symtoken",
                columns: new[] { "broker", "symbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_account_api_keys_account_id",
                schema: "core",
                table: "account_api_keys",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_account_api_keys_key_hash",
                schema: "core",
                table: "account_api_keys",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_api_keys",
                schema: "core");

            migrationBuilder.DropIndex(
                name: "ix_symtoken_broker_brsymbol_exchange",
                schema: "core",
                table: "symtoken");

            migrationBuilder.DropIndex(
                name: "ix_symtoken_broker_symbol_exchange",
                schema: "core",
                table: "symtoken");

            migrationBuilder.DropColumn(
                name: "broker",
                schema: "core",
                table: "symtoken");
        }
    }
}
