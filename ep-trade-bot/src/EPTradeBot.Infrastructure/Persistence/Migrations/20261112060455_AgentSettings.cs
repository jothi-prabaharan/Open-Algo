using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    endpoint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    protected_api_key = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_settings", x => x.id);
                    table.CheckConstraint("ck_agent_settings_single_row", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_settings",
                schema: "core");
        }
    }
}
