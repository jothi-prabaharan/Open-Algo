using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_authorization_codes",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    order_mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_authorization_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_authorization_codes_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_calls",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    params_hash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_clients",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    redirect_uris_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_tokens",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    order_mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    access_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    refresh_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_refresh_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    access_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    refresh_expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_tokens_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_authorization_codes_account_id",
                schema: "core",
                table: "mcp_authorization_codes",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_mcp_authorization_codes_code_hash",
                schema: "core",
                table: "mcp_authorization_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_calls_at",
                schema: "audit",
                table: "mcp_calls",
                column: "at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_mcp_clients_client_id",
                schema: "core",
                table: "mcp_clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mcp_tokens_account_id",
                schema: "core",
                table: "mcp_tokens",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_mcp_tokens_access_hash",
                schema: "core",
                table: "mcp_tokens",
                column: "access_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_mcp_tokens_refresh_hash",
                schema: "core",
                table: "mcp_tokens",
                column: "refresh_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_authorization_codes",
                schema: "core");

            migrationBuilder.DropTable(
                name: "mcp_calls",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "mcp_clients",
                schema: "core");

            migrationBuilder.DropTable(
                name: "mcp_tokens",
                schema: "core");
        }
    }
}
