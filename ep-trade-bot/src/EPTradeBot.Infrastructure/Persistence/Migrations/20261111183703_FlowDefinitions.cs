using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlowDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "flow_definitions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    nodes_json = table.Column<string>(type: "jsonb", nullable: false),
                    edges_json = table.Column<string>(type: "jsonb", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    webhook_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    schedule_time_ist = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    schedule_days = table.Column<int>(type: "integer", nullable: false),
                    exchange = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "NSE"),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_run_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_flow_definitions_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "flow_runs",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    logs_json = table.Column<string>(type: "jsonb", nullable: false),
                    errors_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_flow_runs_flow_definitions_flow_id",
                        column: x => x.flow_id,
                        principalSchema: "core",
                        principalTable: "flow_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_flow_definitions_account_id",
                schema: "core",
                table: "flow_definitions",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_flow_definitions_name",
                schema: "core",
                table: "flow_definitions",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_flow_definitions_webhook_token_hash",
                schema: "core",
                table: "flow_definitions",
                column: "webhook_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_flow_runs_flow_started",
                schema: "core",
                table: "flow_runs",
                columns: new[] { "flow_id", "started_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flow_runs",
                schema: "core");

            migrationBuilder.DropTable(
                name: "flow_definitions",
                schema: "core");
        }
    }
}
