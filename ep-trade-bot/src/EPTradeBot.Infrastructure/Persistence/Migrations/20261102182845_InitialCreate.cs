using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EPTradeBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "telemetry");

            migrationBuilder.EnsureSchema(
                name: "market_data");

            migrationBuilder.EnsureSchema(
                name: "latency");

            migrationBuilder.EnsureSchema(
                name: "sandbox");

            migrationBuilder.CreateTable(
                name: "active_sessions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_info = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    broker = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    login_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_seen = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_active_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_audit",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tool = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    conversation_id = table.Column<int>(type: "integer", nullable: true),
                    run_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    args = table.Column<string>(type: "jsonb", nullable: true),
                    risk_verdict = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ok = table.Column<bool>(type: "boolean", nullable: true),
                    response = table.Column<string>(type: "jsonb", nullable: true),
                    order_ids = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_conversation",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    surface = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    agno_session_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_conversation", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_provider_model",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    supports_reasoning = table.Column<bool>(type: "boolean", nullable: false),
                    default_reasoning_effort = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    supports_vision = table.Column<bool>(type: "boolean", nullable: false),
                    tools_unreliable = table.Column<bool>(type: "boolean", nullable: false),
                    last_tested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_test_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_provider_model", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_secret",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ciphertext = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_secret", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_setting",
                schema: "core",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_setting", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "alert_log",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    alert_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    condition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    symbol = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    price = table.Column<double>(type: "double precision", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    delivered = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "analyzer_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    request_data = table.Column<string>(type: "text", nullable: false),
                    response_data = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analyzer_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    api_key_hash = table.Column<string>(type: "text", nullable: false),
                    api_key_encrypted = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    order_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    auth = table.Column<string>(type: "text", nullable: false),
                    feed_token = table.Column<string>(type: "text", nullable: true),
                    broker = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_revoked = table.Column<bool>(type: "boolean", nullable: true),
                    secret_api_key = table.Column<string>(type: "text", nullable: true),
                    primary_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    secondary_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    ip_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    aux_param1 = table.Column<string>(type: "text", nullable: true),
                    aux_param2 = table.Column<string>(type: "text", nullable: true),
                    aux_param3 = table.Column<string>(type: "text", nullable: true),
                    aux_param4 = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bot_config",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    token = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    bot_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    max_message_length = table.Column<int>(type: "integer", nullable: true),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: true),
                    broadcast_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bot_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "broker_accounts",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    broker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    account_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auth_token_encrypted = table.Column<string>(type: "text", nullable: true),
                    feed_token_encrypted = table.Column<string>(type: "text", nullable: true),
                    token_issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    kill_switch_engaged = table.Column<bool>(type: "boolean", nullable: false),
                    kill_switch_engaged_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    kill_switch_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "broker_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    api_key = table.Column<string>(type: "text", nullable: false),
                    api_secret = table.Column<string>(type: "text", nullable: false),
                    api_key_market = table.Column<string>(type: "text", nullable: false),
                    api_secret_market = table.Column<string>(type: "text", nullable: false),
                    redirect_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "broker_login_states",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nonce_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_broker_login_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bus_lane_status",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    lane_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    queue_depth = table.Column<int>(type: "integer", nullable: false),
                    messages_published = table.Column<long>(type: "bigint", nullable: false),
                    messages_dropped = table.Column<long>(type: "bigint", nullable: false),
                    last_message_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sampled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bus_lane_status", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candles",
                schema: "market_data",
                columns: table => new
                {
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<double>(type: "double precision", nullable: false),
                    high = table.Column<double>(type: "double precision", nullable: false),
                    low = table.Column<double>(type: "double precision", nullable: false),
                    close = table.Column<double>(type: "double precision", nullable: false),
                    volume = table.Column<long>(type: "bigint", nullable: false),
                    oi = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candles", x => new { x.symbol, x.exchange, x.interval, x.ts });
                });

            migrationBuilder.CreateTable(
                name: "chart_preferences",
                schema: "core",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chart_preferences", x => new { x.user_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "chartink_strategies",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    webhook_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    is_intraday = table.Column<bool>(type: "boolean", nullable: true),
                    start_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    end_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    squareoff_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chartink_strategies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_catalog",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    first_ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    record_count = table.Column<long>(type: "bigint", nullable: false),
                    last_download_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_catalog", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "download_jobs",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    job_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_symbols = table.Column<int>(type: "integer", nullable: false),
                    completed_symbols = table.Column<int>(type: "integer", nullable: false),
                    failed_symbols = table.Column<int>(type: "integer", nullable: false),
                    interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    start_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    end_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    config_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_download_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "error_404_tracker",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ip_address = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: true),
                    first_error_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paths_attempted = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_error_404_tracker", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "flow_workflows",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    nodes = table.Column<string>(type: "jsonb", nullable: true),
                    edges = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    schedule_job_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    webhook_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    webhook_secret = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    webhook_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    webhook_auth_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    api_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_workflows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "health_alerts",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    alert_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    metric_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    metric_value = table.Column<double>(type: "double precision", nullable: true),
                    threshold_value = table.Column<double>(type: "double precision", nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    acknowledged = table.Column<bool>(type: "boolean", nullable: true),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved = table.Column<bool>(type: "boolean", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "health_metrics",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    fd_count = table.Column<int>(type: "integer", nullable: true),
                    fd_limit = table.Column<int>(type: "integer", nullable: true),
                    fd_usage_percent = table.Column<double>(type: "double precision", nullable: true),
                    fd_available = table.Column<int>(type: "integer", nullable: true),
                    fd_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    memory_rss_mb = table.Column<double>(type: "double precision", nullable: true),
                    memory_vms_mb = table.Column<double>(type: "double precision", nullable: true),
                    memory_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_available_mb = table.Column<double>(type: "double precision", nullable: true),
                    memory_swap_mb = table.Column<double>(type: "double precision", nullable: true),
                    memory_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    db_connections_total = table.Column<int>(type: "integer", nullable: true),
                    db_connections = table.Column<string>(type: "jsonb", nullable: true),
                    db_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ws_connections_total = table.Column<int>(type: "integer", nullable: true),
                    ws_connections = table.Column<string>(type: "jsonb", nullable: true),
                    ws_total_symbols = table.Column<int>(type: "integer", nullable: true),
                    ws_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    thread_count = table.Column<int>(type: "integer", nullable: true),
                    stuck_threads = table.Column<int>(type: "integer", nullable: true),
                    thread_details = table.Column<string>(type: "jsonb", nullable: true),
                    thread_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    process_details = table.Column<string>(type: "jsonb", nullable: true),
                    overall_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_metrics", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "historify_schedules",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    schedule_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    interval_value = table.Column<int>(type: "integer", nullable: true),
                    interval_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    time_of_day = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    download_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    data_interval = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    lookback_days = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scheduler_job_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_run_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_run_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    total_runs = table.Column<int>(type: "integer", nullable: false),
                    successful_runs = table.Column<int>(type: "integer", nullable: false),
                    failed_runs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_historify_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invalid_api_key_tracker",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ip_address = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: true),
                    first_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    api_keys_tried = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invalid_api_key_tracker", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ip_bans",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ip_address = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ban_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ban_count = table.Column<int>(type: "integer", nullable: true),
                    banned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_permanent = table.Column<bool>(type: "boolean", nullable: true),
                    created_by = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ip_bans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leverage_config",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    leverage = table.Column<double>(type: "double precision", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_leverage_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_attempts",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    device_info = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    login_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    broker = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "market_holiday_exchanges",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    holiday_id = table.Column<int>(type: "integer", nullable: false),
                    exchange_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    start_time = table.Column<long>(type: "bigint", nullable: true),
                    end_time = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_holiday_exchanges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "market_holidays",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    holiday_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    holiday_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_holidays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "market_timings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    exchange_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    start_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    end_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    start_offset = table.Column<long>(type: "bigint", nullable: false),
                    end_offset = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_market_timings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "master_contract_status",
                schema: "core",
                columns: table => new
                {
                    broker = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true),
                    last_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    total_symbols = table.Column<string>(type: "text", nullable: true),
                    is_ready = table.Column<bool>(type: "boolean", nullable: true),
                    last_download_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    download_date = table.Column<DateOnly>(type: "date", nullable: true),
                    exchange_stats = table.Column<string>(type: "text", nullable: true),
                    download_duration_seconds = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_master_contract_status", x => x.broker);
                });

            migrationBuilder.CreateTable(
                name: "oauth_clients",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    redirect_uris = table.Column<string>(type: "text", nullable: false),
                    client_secret_hash = table.Column<string>(type: "text", nullable: true),
                    scopes_requested = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    approved = table.Column<bool>(type: "boolean", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_refresh_tokens",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    family_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_oauth_refresh_tokens_oauth_refresh_tokens_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "core",
                        principalTable: "oauth_refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_signing_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    public_jwk = table.Column<string>(type: "text", nullable: false),
                    private_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    rotated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_signing_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_latency",
                schema: "latency",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    order_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    order_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    rtt_ms = table.Column<double>(type: "double precision", nullable: true),
                    validation_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    response_latency_ms = table.Column<double>(type: "double precision", nullable: true),
                    overhead_ms = table.Column<double>(type: "double precision", nullable: true),
                    total_latency_ms = table.Column<double>(type: "double precision", nullable: false),
                    request_body = table.Column<string>(type: "jsonb", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_latency", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_type = table.Column<string>(type: "text", nullable: false),
                    request_data = table.Column<string>(type: "text", nullable: false),
                    response_data = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pending_orders",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    api_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    order_data = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_ist = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_at_ist = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    approved_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_at_ist = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    rejected_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    rejected_reason = table.Column<string>(type: "text", nullable: true),
                    broker_order_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    broker_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qty_freeze",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    exchange = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    freeze_qty = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qty_freeze", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_config",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    config_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    config_value = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_daily_pnl",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    realized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    positions_unrealized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    holdings_unrealized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    total_mtm = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    available_balance = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    used_margin = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    portfolio_value = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_daily_pnl", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_funds",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    total_capital = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    available_balance = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    used_margin = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    realized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    today_realized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    unrealized_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    total_pnl = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    last_reset_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reset_count = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_funds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_gtt",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gtt_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    strategy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    trigger_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    gtt_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    margin_blocked = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_gtt", x => x.id);
                    table.UniqueConstraint("ak_sandbox_gtt_gtt_id", x => x.gtt_id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_holdings",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    average_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ltp = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    pnl = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    pnl_percent = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    settlement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_holdings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_orders",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orderid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    strategy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    trigger_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    price_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    average_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    filled_quantity = table.Column<int>(type: "integer", nullable: true),
                    pending_quantity = table.Column<int>(type: "integer", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    margin_blocked = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    gtt_leg_id = table.Column<int>(type: "integer", nullable: true),
                    order_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    update_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_positions",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    average_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ltp = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    pnl = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    pnl_percent = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    accumulated_realized_pnl = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    today_realized_pnl = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    margin_blocked = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_positions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_trades",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tradeid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    orderid = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    strategy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    trade_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_trades", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scalping_sl_state",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    exchange = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    product = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    side = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    entry_price = table.Column<double>(type: "double precision", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    initial_sl = table.Column<double>(type: "double precision", nullable: true),
                    trailing_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trailing_step = table.Column<double>(type: "double precision", nullable: true),
                    highest_price = table.Column<double>(type: "double precision", nullable: true),
                    lowest_price = table.Column<double>(type: "double precision", nullable: true),
                    current_sl = table.Column<double>(type: "double precision", nullable: true),
                    target = table.Column<double>(type: "double precision", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scalping_sl_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scalping_tracked_symbol",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    exchange = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    product = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scalping_tracked_symbol", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_revocations",
                schema: "core",
                columns: table => new
                {
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    pre_broker_revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_revocations", x => x.username);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    analyze_mode = table.Column<bool>(type: "boolean", nullable: true),
                    smtp_server = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: true),
                    smtp_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_password_encrypted = table.Column<string>(type: "text", nullable: true),
                    smtp_use_tls = table.Column<bool>(type: "boolean", nullable: true),
                    smtp_from_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_helo_hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    security_auto_ban_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    security_404_threshold = table.Column<int>(type: "integer", nullable: true),
                    security_404_ban_duration = table.Column<int>(type: "integer", nullable: true),
                    security_api_threshold = table.Column<int>(type: "integer", nullable: true),
                    security_api_ban_duration = table.Column<int>(type: "integer", nullable: true),
                    security_repeat_offender_limit = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sm_strategy",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    strategy_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    universe_tab = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    underlying = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    underlying_exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    strategy_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entry_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    exit_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    product = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    pricetype = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    legs = table.Column<string>(type: "jsonb", nullable: false),
                    overall_sl_mtm = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    overall_target_mtm = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    lock_profit = table.Column<string>(type: "jsonb", nullable: true),
                    trail_sl_to_entry = table.Column<bool>(type: "boolean", nullable: false),
                    scheduler = table.Column<string>(type: "jsonb", nullable: true),
                    live_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    webhook_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    webhook_ip_allowlist = table.Column<string>(type: "jsonb", nullable: true),
                    webhook_locked = table.Column<bool>(type: "boolean", nullable: false),
                    daily_loss_limit_inr = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    current_run_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_strategy", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_order_tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orderid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    strategy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    applied_quantity = table.Column<double>(type: "double precision", nullable: false),
                    applied_notional = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_order_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_pending_fills",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    orderid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    filled_quantity = table.Column<double>(type: "double precision", nullable: false),
                    average_price = table.Column<double>(type: "double precision", nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_pending_fills", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_portfolio",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    watchlist = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    underlying = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expiry = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    legs_json = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_portfolio", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "strategy_positions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    strategy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<double>(type: "double precision", nullable: false),
                    average_price = table.Column<double>(type: "double precision", nullable: false),
                    realized_pnl = table.Column<double>(type: "double precision", nullable: false),
                    today_realized_pnl = table.Column<double>(type: "double precision", nullable: false),
                    trade_date = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strategy_positions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "symbol_metadata",
                schema: "market_data",
                columns: table => new
                {
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    expiry = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    strike = table.Column<double>(type: "double precision", nullable: true),
                    lot_size = table.Column<int>(type: "integer", nullable: true),
                    instrument_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    tick_size = table.Column<double>(type: "double precision", nullable: true),
                    last_updated_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_symbol_metadata", x => new { x.symbol, x.exchange });
                });

            migrationBuilder.CreateTable(
                name: "symtoken",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "text", nullable: false),
                    brsymbol = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    exchange = table.Column<string>(type: "text", nullable: true),
                    brexchange = table.Column<string>(type: "text", nullable: true),
                    token = table.Column<string>(type: "text", nullable: true),
                    expiry = table.Column<string>(type: "text", nullable: true),
                    strike = table.Column<double>(type: "double precision", nullable: true),
                    lotsize = table.Column<int>(type: "integer", nullable: true),
                    instrumenttype = table.Column<string>(type: "text", nullable: true),
                    tick_size = table.Column<double>(type: "double precision", nullable: true),
                    contract_value = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_symtoken", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telegram_users",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<int>(type: "integer", nullable: false),
                    openalgo_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    encrypted_api_key = table.Column<string>(type: "text", nullable: true),
                    host_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    first_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    last_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    telegram_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    notifications_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_command_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_users", x => x.id);
                    table.UniqueConstraint("ak_telegram_users_telegram_id", x => x.telegram_id);
                });

            migrationBuilder.CreateTable(
                name: "traffic_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    client_ip = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<double>(type: "double precision", nullable: false),
                    host = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_traffic_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    email = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    totp_secret = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_admin = table.Column<bool>(type: "boolean", nullable: true),
                    totp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    totp_required_for_login = table.Column<bool>(type: "boolean", nullable: false),
                    totp_required_for_mcp = table.Column<bool>(type: "boolean", nullable: false),
                    totp_required_for_password_reset = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "watchlist",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    added_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchlist", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "watchlists",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchlists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_config",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_blob = table.Column<byte[]>(type: "bytea", nullable: true),
                    own_jid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    own_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    bot_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    owner_user_id = table.Column<int>(type: "integer", nullable: true),
                    owner_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_paired = table.Column<bool>(type: "boolean", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    paired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_message_length = table.Column<int>(type: "integer", nullable: true),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: true),
                    broadcast_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_config", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_users",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    whatsapp_jid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    openalgo_username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    encrypted_api_key = table.Column<string>(type: "text", nullable: true),
                    host_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    notifications_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_command_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_users", x => x.id);
                    table.UniqueConstraint("ak_whatsapp_users_whatsapp_jid", x => x.whatsapp_jid);
                });

            migrationBuilder.CreateTable(
                name: "ws_proxy_connections",
                schema: "telemetry",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    connection_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    connected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    disconnected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    subscription_count = table.Column<int>(type: "integer", nullable: false),
                    messages_sent = table.Column<long>(type: "bigint", nullable: false),
                    disconnect_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ws_proxy_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ag_message",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    tools = table.Column<string>(type: "jsonb", nullable: true),
                    notices = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ag_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_ag_message_ag_conversation_conversation_id",
                        column: x => x.conversation_id,
                        principalSchema: "core",
                        principalTable: "ag_conversation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_risk_settings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_daily_loss = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_order_quantity = table.Column<int>(type: "integer", nullable: false),
                    max_position_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_risk_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_risk_settings_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "hosted_strategies",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    script_path = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    schedule_start_ist = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    schedule_stop_ist = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    schedule_days = table.Column<int>(type: "integer", nullable: false),
                    is_running = table.Column<bool>(type: "boolean", nullable: false),
                    process_id = table.Column<int>(type: "integer", nullable: true),
                    last_started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_stopped_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hosted_strategies", x => x.id);
                    table.ForeignKey(
                        name: "fk_hosted_strategies_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "kill_switch_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    occurred_at_ist = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    engaged = table.Column<bool>(type: "boolean", nullable: false),
                    actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    completed = table.Column<bool>(type: "boolean", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kill_switch_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_kill_switch_events_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_audit_log",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    occurred_at_ist = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    environment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    product = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    price_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    broker_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    request_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_audit_log_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chartink_symbol_mappings",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    strategy_id = table.Column<int>(type: "integer", nullable: false),
                    chartink_symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    exchange = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    product_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chartink_symbol_mappings", x => x.id);
                    table.ForeignKey(
                        name: "fk_chartink_symbol_mappings_chartink_strategies_strategy_id",
                        column: x => x.strategy_id,
                        principalSchema: "core",
                        principalTable: "chartink_strategies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_items",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    records_downloaded = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_items_download_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "market_data",
                        principalTable: "download_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "flow_workflow_executions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    workflow_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    logs = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flow_workflow_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_flow_workflow_executions_flow_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalSchema: "core",
                        principalTable: "flow_workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "historify_schedule_executions",
                schema: "market_data",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    schedule_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    download_job_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    symbols_processed = table.Column<int>(type: "integer", nullable: false),
                    symbols_success = table.Column<int>(type: "integer", nullable: false),
                    symbols_failed = table.Column<int>(type: "integer", nullable: false),
                    records_downloaded = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_historify_schedule_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_historify_schedule_executions_historify_schedules_schedule_~",
                        column: x => x.schedule_id,
                        principalSchema: "market_data",
                        principalTable: "historify_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "action_center_decisions",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    occurred_at_ist = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    pending_order_id = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    bulk_action = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_center_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_action_center_decisions_broker_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "core",
                        principalTable: "broker_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_action_center_decisions_pending_orders_pending_order_id",
                        column: x => x.pending_order_id,
                        principalSchema: "audit",
                        principalTable: "pending_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_gtt_legs",
                schema: "sandbox",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gtt_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    leg_number = table.Column<int>(type: "integer", nullable: false),
                    trigger_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    trigger_direction = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    pricetype = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    product = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    leg_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    triggered_order_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    leg_margin = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_gtt_legs", x => x.id);
                    table.ForeignKey(
                        name: "fk_sandbox_gtt_legs_sandbox_gtt_gtt_id",
                        column: x => x.gtt_id,
                        principalSchema: "sandbox",
                        principalTable: "sandbox_gtt",
                        principalColumn: "gtt_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sm_strategy_run",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    strategy_id = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    broker = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    broker_account = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    stopped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    stop_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    stop_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    stop_requested_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    pnl_realized = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_peak = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_trough = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    trigger_source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    webhook_event_id = table.Column<int>(type: "integer", nullable: true),
                    resolved_expiries = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_strategy_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_sm_strategy_run_sm_strategy_strategy_id",
                        column: x => x.strategy_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sm_webhook_event",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    strategy_id = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    mode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_webhook_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_sm_webhook_event_sm_strategy_strategy_id",
                        column: x => x.strategy_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "command_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<int>(type: "integer", nullable: false),
                    command = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    chat_id = table.Column<int>(type: "integer", nullable: true),
                    parameters = table.Column<string>(type: "text", nullable: true),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_command_logs_telegram_users_telegram_id",
                        column: x => x.telegram_id,
                        principalSchema: "core",
                        principalTable: "telegram_users",
                        principalColumn: "telegram_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_queue",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    telegram_id = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_queue", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_queue_telegram_users_telegram_id",
                        column: x => x.telegram_id,
                        principalSchema: "core",
                        principalTable: "telegram_users",
                        principalColumn: "telegram_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_preferences",
                schema: "core",
                columns: table => new
                {
                    telegram_id = table.Column<int>(type: "integer", nullable: false),
                    order_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    trade_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    pnl_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    daily_summary = table.Column<bool>(type: "boolean", nullable: true),
                    summary_time = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    timezone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_preferences", x => x.telegram_id);
                    table.ForeignKey(
                        name: "fk_user_preferences_telegram_users_telegram_id",
                        column: x => x.telegram_id,
                        principalSchema: "core",
                        principalTable: "telegram_users",
                        principalColumn: "telegram_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "watchlist_items",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    watchlist_id = table.Column<int>(type: "integer", nullable: false),
                    symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watchlist_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_watchlist_items_watchlists_watchlist_id",
                        column: x => x.watchlist_id,
                        principalSchema: "core",
                        principalTable: "watchlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_command_logs",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    whatsapp_jid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    command = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    parameters = table.Column<string>(type: "text", nullable: true),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_command_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_whatsapp_command_logs_whatsapp_users_whatsapp_jid",
                        column: x => x.whatsapp_jid,
                        principalSchema: "core",
                        principalTable: "whatsapp_users",
                        principalColumn: "whatsapp_jid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_notification_queue",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    whatsapp_jid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    media_path = table.Column<string>(type: "text", nullable: true),
                    media_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_notification_queue", x => x.id);
                    table.ForeignKey(
                        name: "fk_whatsapp_notification_queue_whatsapp_users_whatsapp_jid",
                        column: x => x.whatsapp_jid,
                        principalSchema: "core",
                        principalTable: "whatsapp_users",
                        principalColumn: "whatsapp_jid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_user_preferences",
                schema: "core",
                columns: table => new
                {
                    whatsapp_jid = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    order_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    trade_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    pnl_notifications = table.Column<bool>(type: "boolean", nullable: true),
                    daily_summary = table.Column<bool>(type: "boolean", nullable: true),
                    summary_time = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    timezone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_user_preferences", x => x.whatsapp_jid);
                    table.ForeignKey(
                        name: "fk_whatsapp_user_preferences_whatsapp_users_whatsapp_jid",
                        column: x => x.whatsapp_jid,
                        principalSchema: "core",
                        principalTable: "whatsapp_users",
                        principalColumn: "whatsapp_jid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sm_strategy_checkpoint",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<int>(type: "integer", nullable: false),
                    ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    pnl_realized = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_unrealized = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_peak = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pnl_trough = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    lock_floor = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    trail_to_entry_active = table.Column<bool>(type: "boolean", nullable: false),
                    leg_state = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_strategy_checkpoint", x => x.id);
                    table.ForeignKey(
                        name: "fk_sm_strategy_checkpoint_sm_strategy_run_run_id",
                        column: x => x.run_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sm_strategy_event",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<int>(type: "integer", nullable: true),
                    strategy_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    leg_id = table.Column<int>(type: "integer", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_strategy_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_sm_strategy_event_sm_strategy_run_run_id",
                        column: x => x.run_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sm_strategy_event_sm_strategy_strategy_id",
                        column: x => x.strategy_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sm_strategy_order",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    run_id = table.Column<int>(type: "integer", nullable: false),
                    leg_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    position_ref = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    broker_order_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    exchange = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    qty = table.Column<int>(type: "integer", nullable: false),
                    product = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    pricetype = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    trigger_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    placed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    filled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    avg_fill_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    filled_qty = table.Column<int>(type: "integer", nullable: true),
                    reject_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sm_strategy_order", x => x.id);
                    table.ForeignKey(
                        name: "fk_sm_strategy_order_sm_strategy_run_run_id",
                        column: x => x.run_id,
                        principalSchema: "core",
                        principalTable: "sm_strategy_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_account_risk_settings_account_id",
                schema: "core",
                table: "account_risk_settings",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_action_center_decisions_account_id",
                schema: "audit",
                table: "action_center_decisions",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_action_center_decisions_occurred_at_brin",
                schema: "audit",
                table: "action_center_decisions",
                column: "occurred_at_utc")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_action_center_decisions_pending_order_id",
                schema: "audit",
                table: "action_center_decisions",
                column: "pending_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_active_sessions_username",
                schema: "core",
                table: "active_sessions",
                column: "username");

            migrationBuilder.CreateIndex(
                name: "ux_active_sessions_session_id",
                schema: "core",
                table: "active_sessions",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ag_audit_conversation_id",
                schema: "audit",
                table: "ag_audit",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_ag_audit_conversation_id_ts",
                schema: "audit",
                table: "ag_audit",
                columns: new[] { "conversation_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_ag_audit_run_id",
                schema: "audit",
                table: "ag_audit",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_ag_audit_run_id_ts",
                schema: "audit",
                table: "ag_audit",
                columns: new[] { "run_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_ag_conversation_agno_session_id",
                schema: "core",
                table: "ag_conversation",
                column: "agno_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_ag_conversation_user_id",
                schema: "core",
                table: "ag_conversation",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ag_conversation_user_id_updated_at",
                schema: "core",
                table: "ag_conversation",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ag_message_conversation_id",
                schema: "core",
                table: "ag_message",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_ag_message_conversation_id_created_at",
                schema: "core",
                table: "ag_message",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ag_provider_model_enabled",
                schema: "core",
                table: "ag_provider_model",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "ux_ag_provider_model_provider_kind_model_name_base_url",
                schema: "core",
                table: "ag_provider_model",
                columns: new[] { "provider_kind", "model_name", "base_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ag_secret_name",
                schema: "core",
                table: "ag_secret",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_log_user_id_fired_at",
                schema: "audit",
                table: "alert_log",
                columns: new[] { "user_id", "fired_at" });

            migrationBuilder.CreateIndex(
                name: "ix_analyzer_logs_api_type",
                schema: "audit",
                table: "analyzer_logs",
                column: "api_type");

            migrationBuilder.CreateIndex(
                name: "ix_analyzer_logs_api_type_created_at",
                schema: "audit",
                table: "analyzer_logs",
                columns: new[] { "api_type", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_analyzer_logs_created_at",
                schema: "audit",
                table: "analyzer_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_created_at",
                schema: "core",
                table: "api_keys",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_order_mode",
                schema: "core",
                table: "api_keys",
                column: "order_mode");

            migrationBuilder.CreateIndex(
                name: "ux_api_keys_user_id",
                schema: "core",
                table: "api_keys",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_broker",
                schema: "core",
                table: "auth",
                column: "broker");

            migrationBuilder.CreateIndex(
                name: "ix_auth_is_revoked",
                schema: "core",
                table: "auth",
                column: "is_revoked");

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_id",
                schema: "core",
                table: "auth",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_auth_name",
                schema: "core",
                table: "auth",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_accounts_account_type",
                schema: "core",
                table: "broker_accounts",
                column: "account_type");

            migrationBuilder.CreateIndex(
                name: "ux_broker_accounts_broker_client_id",
                schema: "core",
                table: "broker_accounts",
                columns: new[] { "broker", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_broker_keys_broker",
                schema: "core",
                table: "broker_keys",
                column: "broker",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_broker_login_states_state_hash",
                schema: "core",
                table: "broker_login_states",
                column: "state_hash");

            migrationBuilder.CreateIndex(
                name: "ux_broker_login_states_nonce_hash",
                schema: "core",
                table: "broker_login_states",
                column: "nonce_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bus_lane_status_lane_time",
                schema: "telemetry",
                table: "bus_lane_status",
                columns: new[] { "lane_name", "sampled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_candles_ts_brin",
                schema: "market_data",
                table: "candles",
                column: "ts")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ux_chartink_strategies_webhook_id",
                schema: "core",
                table: "chartink_strategies",
                column: "webhook_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chartink_symbol_mappings_strategy_id",
                schema: "core",
                table: "chartink_symbol_mappings",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_command_logs_telegram_id",
                schema: "audit",
                table: "command_logs",
                column: "telegram_id");

            migrationBuilder.CreateIndex(
                name: "ux_data_catalog_symbol_exchange_interval",
                schema: "market_data",
                table: "data_catalog",
                columns: new[] { "symbol", "exchange", "interval" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_download_jobs_status",
                schema: "market_data",
                table: "download_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_error_404_tracker_error_count",
                schema: "audit",
                table: "error_404_tracker",
                column: "error_count");

            migrationBuilder.CreateIndex(
                name: "ix_error_404_tracker_first_error_at",
                schema: "audit",
                table: "error_404_tracker",
                column: "first_error_at");

            migrationBuilder.CreateIndex(
                name: "ix_error_404_tracker_ip_address",
                schema: "audit",
                table: "error_404_tracker",
                column: "ip_address");

            migrationBuilder.CreateIndex(
                name: "ix_flow_workflow_executions_workflow_id",
                schema: "core",
                table: "flow_workflow_executions",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "ux_flow_workflows_webhook_token",
                schema: "core",
                table: "flow_workflows",
                column: "webhook_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_historify_schedule_executions_schedule_id",
                schema: "market_data",
                table: "historify_schedule_executions",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_hosted_strategies_account_id",
                schema: "core",
                table: "hosted_strategies",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_hosted_strategies_name",
                schema: "core",
                table: "hosted_strategies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invalid_api_key_tracker_attempt_count",
                schema: "audit",
                table: "invalid_api_key_tracker",
                column: "attempt_count");

            migrationBuilder.CreateIndex(
                name: "ix_invalid_api_key_tracker_first_attempt_at",
                schema: "audit",
                table: "invalid_api_key_tracker",
                column: "first_attempt_at");

            migrationBuilder.CreateIndex(
                name: "ix_invalid_api_key_tracker_ip_address",
                schema: "audit",
                table: "invalid_api_key_tracker",
                column: "ip_address");

            migrationBuilder.CreateIndex(
                name: "ux_ip_bans_ip_address",
                schema: "audit",
                table: "ip_bans",
                column: "ip_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_items_job_id",
                schema: "market_data",
                table: "job_items",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_kill_switch_events_account_id",
                schema: "audit",
                table: "kill_switch_events",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_kill_switch_events_occurred_at_brin",
                schema: "audit",
                table: "kill_switch_events",
                column: "occurred_at_utc")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_login_attempts_status",
                schema: "audit",
                table: "login_attempts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_login_attempts_timestamp",
                schema: "audit",
                table: "login_attempts",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_login_attempts_username",
                schema: "audit",
                table: "login_attempts",
                column: "username");

            migrationBuilder.CreateIndex(
                name: "ix_market_holiday_exchanges_exchange_code",
                schema: "core",
                table: "market_holiday_exchanges",
                column: "exchange_code");

            migrationBuilder.CreateIndex(
                name: "ix_market_holiday_exchanges_holiday_id",
                schema: "core",
                table: "market_holiday_exchanges",
                column: "holiday_id");

            migrationBuilder.CreateIndex(
                name: "ix_market_holiday_exchanges_holiday_id_exchange_code",
                schema: "core",
                table: "market_holiday_exchanges",
                columns: new[] { "holiday_id", "exchange_code" });

            migrationBuilder.CreateIndex(
                name: "ix_market_holidays_holiday_date",
                schema: "core",
                table: "market_holidays",
                column: "holiday_date");

            migrationBuilder.CreateIndex(
                name: "ix_market_holidays_holiday_date_year",
                schema: "core",
                table: "market_holidays",
                columns: new[] { "holiday_date", "year" });

            migrationBuilder.CreateIndex(
                name: "ix_market_holidays_year",
                schema: "core",
                table: "market_holidays",
                column: "year");

            migrationBuilder.CreateIndex(
                name: "ux_market_timings_exchange_code",
                schema: "core",
                table: "market_timings",
                column: "exchange_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_queue_status",
                schema: "core",
                table: "notification_queue",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_notification_queue_telegram_id",
                schema: "core",
                table: "notification_queue",
                column: "telegram_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_approved_revoked_at",
                schema: "core",
                table: "oauth_clients",
                columns: new[] { "approved", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ux_oauth_clients_client_id",
                schema: "core",
                table: "oauth_clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_client_id",
                schema: "core",
                table: "oauth_refresh_tokens",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_family_id",
                schema: "core",
                table: "oauth_refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_parent_id",
                schema: "core",
                table: "oauth_refresh_tokens",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_revoked_at",
                schema: "core",
                table: "oauth_refresh_tokens",
                column: "revoked_at");

            migrationBuilder.CreateIndex(
                name: "ux_oauth_refresh_tokens_token_hash",
                schema: "core",
                table: "oauth_refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_oauth_signing_keys_kid",
                schema: "core",
                table: "oauth_signing_keys",
                column: "kid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_audit_log_account_time",
                schema: "audit",
                table: "order_audit_log",
                columns: new[] { "account_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_order_audit_log_occurred_at_brin",
                schema: "audit",
                table: "order_audit_log",
                column: "occurred_at_utc")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_pending_orders_created_at",
                schema: "audit",
                table: "pending_orders",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_pending_orders_user_id_status",
                schema: "audit",
                table: "pending_orders",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_qty_freeze_exchange",
                schema: "core",
                table: "qty_freeze",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_qty_freeze_symbol",
                schema: "core",
                table: "qty_freeze",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ux_qty_freeze_exchange_symbol",
                schema: "core",
                table: "qty_freeze",
                columns: new[] { "exchange", "symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_config_config_key",
                schema: "sandbox",
                table: "sandbox_config",
                column: "config_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_daily_pnl_date",
                schema: "sandbox",
                table: "sandbox_daily_pnl",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_daily_pnl_user_id",
                schema: "sandbox",
                table: "sandbox_daily_pnl",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_daily_pnl_user_id_date",
                schema: "sandbox",
                table: "sandbox_daily_pnl",
                columns: new[] { "user_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_funds_user_id",
                schema: "sandbox",
                table: "sandbox_funds",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_exchange",
                schema: "sandbox",
                table: "sandbox_gtt",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_gtt_status",
                schema: "sandbox",
                table: "sandbox_gtt",
                column: "gtt_status");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_symbol",
                schema: "sandbox",
                table: "sandbox_gtt",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_symbol_exchange",
                schema: "sandbox",
                table: "sandbox_gtt",
                columns: new[] { "symbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_user_id",
                schema: "sandbox",
                table: "sandbox_gtt",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_user_id_gtt_status",
                schema: "sandbox",
                table: "sandbox_gtt",
                columns: new[] { "user_id", "gtt_status" });

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_gtt_gtt_id",
                schema: "sandbox",
                table: "sandbox_gtt",
                column: "gtt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_legs_gtt_id",
                schema: "sandbox",
                table: "sandbox_gtt_legs",
                column: "gtt_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_gtt_legs_leg_status_claimed_at",
                schema: "sandbox",
                table: "sandbox_gtt_legs",
                columns: new[] { "leg_status", "claimed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_holdings_exchange",
                schema: "sandbox",
                table: "sandbox_holdings",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_holdings_symbol",
                schema: "sandbox",
                table: "sandbox_holdings",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_holdings_user_id",
                schema: "sandbox",
                table: "sandbox_holdings",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_holdings_user_id_symbol_exchange",
                schema: "sandbox",
                table: "sandbox_holdings",
                columns: new[] { "user_id", "symbol", "exchange" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_exchange",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_order_status",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "order_status");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_symbol",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_symbol_exchange",
                schema: "sandbox",
                table: "sandbox_orders",
                columns: new[] { "symbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_user_id",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_orders_user_id_order_status",
                schema: "sandbox",
                table: "sandbox_orders",
                columns: new[] { "user_id", "order_status" });

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_orders_gtt_leg_id",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "gtt_leg_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_orders_orderid",
                schema: "sandbox",
                table: "sandbox_orders",
                column: "orderid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_positions_exchange",
                schema: "sandbox",
                table: "sandbox_positions",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_positions_symbol",
                schema: "sandbox",
                table: "sandbox_positions",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_positions_user_id",
                schema: "sandbox",
                table: "sandbox_positions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_positions_user_id_product",
                schema: "sandbox",
                table: "sandbox_positions",
                columns: new[] { "user_id", "product" });

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_positions_user_id_symbol_exchange_product",
                schema: "sandbox",
                table: "sandbox_positions",
                columns: new[] { "user_id", "symbol", "exchange", "product" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_trades_exchange",
                schema: "sandbox",
                table: "sandbox_trades",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_trades_orderid",
                schema: "sandbox",
                table: "sandbox_trades",
                column: "orderid");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_trades_symbol",
                schema: "sandbox",
                table: "sandbox_trades",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_trades_user_id",
                schema: "sandbox",
                table: "sandbox_trades",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_trades_user_id_symbol",
                schema: "sandbox",
                table: "sandbox_trades",
                columns: new[] { "user_id", "symbol" });

            migrationBuilder.CreateIndex(
                name: "ux_sandbox_trades_tradeid",
                schema: "sandbox",
                table: "sandbox_trades",
                column: "tradeid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_scalping_sl_state_symbol_exchange_product_mode",
                schema: "core",
                table: "scalping_sl_state",
                columns: new[] { "symbol", "exchange", "product", "mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_scalping_tracked_symbol_symbol_exchange_product_mode",
                schema: "core",
                table: "scalping_tracked_symbol",
                columns: new[] { "symbol", "exchange", "product", "mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_status",
                schema: "core",
                table: "sm_strategy",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_user_id",
                schema: "core",
                table: "sm_strategy",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_user_id_status",
                schema: "core",
                table: "sm_strategy",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_sm_strategy_user_id_name",
                schema: "core",
                table: "sm_strategy",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sm_strategy_webhook_token_hash",
                schema: "core",
                table: "sm_strategy",
                column: "webhook_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_checkpoint_run_id",
                schema: "core",
                table: "sm_strategy_checkpoint",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_checkpoint_run_id_ts",
                schema: "core",
                table: "sm_strategy_checkpoint",
                columns: new[] { "run_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_event_run_id",
                schema: "core",
                table: "sm_strategy_event",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_event_run_id_ts",
                schema: "core",
                table: "sm_strategy_event",
                columns: new[] { "run_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_event_strategy_id",
                schema: "core",
                table: "sm_strategy_event",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_event_strategy_id_ts",
                schema: "core",
                table: "sm_strategy_event",
                columns: new[] { "strategy_id", "ts" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_event_user_id",
                schema: "core",
                table: "sm_strategy_event",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_order_broker_order_id",
                schema: "core",
                table: "sm_strategy_order",
                column: "broker_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_order_run_id",
                schema: "core",
                table: "sm_strategy_order",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_order_run_id_leg_id_position_ref",
                schema: "core",
                table: "sm_strategy_order",
                columns: new[] { "run_id", "leg_id", "position_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_order_run_id_placed_at",
                schema: "core",
                table: "sm_strategy_order",
                columns: new[] { "run_id", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_run_strategy_id",
                schema: "core",
                table: "sm_strategy_run",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_strategy_run_strategy_id_started_at",
                schema: "core",
                table: "sm_strategy_run",
                columns: new[] { "strategy_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sm_webhook_event_strategy_id",
                schema: "core",
                table: "sm_webhook_event",
                column: "strategy_id");

            migrationBuilder.CreateIndex(
                name: "ix_sm_webhook_event_strategy_id_received_at",
                schema: "core",
                table: "sm_webhook_event",
                columns: new[] { "strategy_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_strategy_order_tags_strategy",
                schema: "core",
                table: "strategy_order_tags",
                column: "strategy");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_order_tags_user_id",
                schema: "core",
                table: "strategy_order_tags",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_strategy_order_tags_orderid",
                schema: "core",
                table: "strategy_order_tags",
                column: "orderid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_strategy_pending_fills_orderid",
                schema: "core",
                table: "strategy_pending_fills",
                column: "orderid");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_portfolio_watchlist",
                schema: "core",
                table: "strategy_portfolio",
                column: "watchlist");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_positions_strategy",
                schema: "core",
                table: "strategy_positions",
                column: "strategy");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_positions_symbol",
                schema: "core",
                table: "strategy_positions",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_strategy_positions_user_id",
                schema: "core",
                table: "strategy_positions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_strategy_positions_user_id_strategy_symbol_exchange_product",
                schema: "core",
                table: "strategy_positions",
                columns: new[] { "user_id", "strategy", "symbol", "exchange", "product" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_brexchange",
                schema: "core",
                table: "symtoken",
                column: "brexchange");

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_brsymbol",
                schema: "core",
                table: "symtoken",
                column: "brsymbol");

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_brsymbol_exchange",
                schema: "core",
                table: "symtoken",
                columns: new[] { "brsymbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_exchange",
                schema: "core",
                table: "symtoken",
                column: "exchange");

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_symbol",
                schema: "core",
                table: "symtoken",
                column: "symbol");

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_symbol_exchange",
                schema: "core",
                table: "symtoken",
                columns: new[] { "symbol", "exchange" });

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_symbol_name",
                schema: "core",
                table: "symtoken",
                columns: new[] { "symbol", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_symtoken_token",
                schema: "core",
                table: "symtoken",
                column: "token");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_users_openalgo_username",
                schema: "core",
                table: "telegram_users",
                column: "openalgo_username");

            migrationBuilder.CreateIndex(
                name: "ux_telegram_users_telegram_id",
                schema: "core",
                table: "telegram_users",
                column: "telegram_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_traffic_logs_client_ip",
                schema: "audit",
                table: "traffic_logs",
                column: "client_ip");

            migrationBuilder.CreateIndex(
                name: "ix_traffic_logs_client_ip_timestamp",
                schema: "audit",
                table: "traffic_logs",
                columns: new[] { "client_ip", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_traffic_logs_status_code",
                schema: "audit",
                table: "traffic_logs",
                column: "status_code");

            migrationBuilder.CreateIndex(
                name: "ix_traffic_logs_timestamp",
                schema: "audit",
                table: "traffic_logs",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_traffic_logs_user_id",
                schema: "audit",
                table: "traffic_logs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                schema: "core",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_username",
                schema: "core",
                table: "users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_market_data_watchlist_symbol_exchange",
                schema: "market_data",
                table: "watchlist",
                columns: new[] { "symbol", "exchange" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_watchlist_items_watchlist_id",
                schema: "core",
                table: "watchlist_items",
                column: "watchlist_id");

            migrationBuilder.CreateIndex(
                name: "ux_watchlist_items_watchlist_id_symbol_exchange",
                schema: "core",
                table: "watchlist_items",
                columns: new[] { "watchlist_id", "symbol", "exchange" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_watchlists_user_id",
                schema: "core",
                table: "watchlists",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_watchlists_user_id_name",
                schema: "core",
                table: "watchlists",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_command_logs_whatsapp_jid",
                schema: "audit",
                table: "whatsapp_command_logs",
                column: "whatsapp_jid");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_notification_queue_status",
                schema: "core",
                table: "whatsapp_notification_queue",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_notification_queue_whatsapp_jid",
                schema: "core",
                table: "whatsapp_notification_queue",
                column: "whatsapp_jid");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_users_openalgo_username",
                schema: "core",
                table: "whatsapp_users",
                column: "openalgo_username");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_users_phone_number",
                schema: "core",
                table: "whatsapp_users",
                column: "phone_number");

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_users_whatsapp_jid",
                schema: "core",
                table: "whatsapp_users",
                column: "whatsapp_jid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ws_proxy_connections_connected_brin",
                schema: "telemetry",
                table: "ws_proxy_connections",
                column: "connected_at_utc")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ux_ws_proxy_connections_connection_id",
                schema: "telemetry",
                table: "ws_proxy_connections",
                column: "connection_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_risk_settings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "action_center_decisions",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "active_sessions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ag_audit",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "ag_message",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ag_provider_model",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ag_secret",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ag_setting",
                schema: "core");

            migrationBuilder.DropTable(
                name: "alert_log",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "analyzer_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "api_keys",
                schema: "core");

            migrationBuilder.DropTable(
                name: "auth",
                schema: "core");

            migrationBuilder.DropTable(
                name: "bot_config",
                schema: "core");

            migrationBuilder.DropTable(
                name: "broker_keys",
                schema: "core");

            migrationBuilder.DropTable(
                name: "broker_login_states",
                schema: "core");

            migrationBuilder.DropTable(
                name: "bus_lane_status",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "candles",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "chart_preferences",
                schema: "core");

            migrationBuilder.DropTable(
                name: "chartink_symbol_mappings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "command_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "data_catalog",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "error_404_tracker",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "flow_workflow_executions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "health_alerts",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "health_metrics",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "historify_schedule_executions",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "hosted_strategies",
                schema: "core");

            migrationBuilder.DropTable(
                name: "invalid_api_key_tracker",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "ip_bans",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "job_items",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "kill_switch_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "leverage_config",
                schema: "core");

            migrationBuilder.DropTable(
                name: "login_attempts",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "market_holiday_exchanges",
                schema: "core");

            migrationBuilder.DropTable(
                name: "market_holidays",
                schema: "core");

            migrationBuilder.DropTable(
                name: "market_timings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "master_contract_status",
                schema: "core");

            migrationBuilder.DropTable(
                name: "notification_queue",
                schema: "core");

            migrationBuilder.DropTable(
                name: "oauth_clients",
                schema: "core");

            migrationBuilder.DropTable(
                name: "oauth_refresh_tokens",
                schema: "core");

            migrationBuilder.DropTable(
                name: "oauth_signing_keys",
                schema: "core");

            migrationBuilder.DropTable(
                name: "order_audit_log",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "order_latency",
                schema: "latency");

            migrationBuilder.DropTable(
                name: "order_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "qty_freeze",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sandbox_config",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_daily_pnl",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_funds",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_gtt_legs",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_holdings",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_orders",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_positions",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sandbox_trades",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "scalping_sl_state",
                schema: "core");

            migrationBuilder.DropTable(
                name: "scalping_tracked_symbol",
                schema: "core");

            migrationBuilder.DropTable(
                name: "session_revocations",
                schema: "core");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sm_strategy_checkpoint",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sm_strategy_event",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sm_strategy_order",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sm_webhook_event",
                schema: "core");

            migrationBuilder.DropTable(
                name: "strategy_order_tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "strategy_pending_fills",
                schema: "core");

            migrationBuilder.DropTable(
                name: "strategy_portfolio",
                schema: "core");

            migrationBuilder.DropTable(
                name: "strategy_positions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "symbol_metadata",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "symtoken",
                schema: "core");

            migrationBuilder.DropTable(
                name: "traffic_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "user_preferences",
                schema: "core");

            migrationBuilder.DropTable(
                name: "users",
                schema: "core");

            migrationBuilder.DropTable(
                name: "watchlist",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "watchlist_items",
                schema: "core");

            migrationBuilder.DropTable(
                name: "whatsapp_command_logs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "whatsapp_config",
                schema: "core");

            migrationBuilder.DropTable(
                name: "whatsapp_notification_queue",
                schema: "core");

            migrationBuilder.DropTable(
                name: "whatsapp_user_preferences",
                schema: "core");

            migrationBuilder.DropTable(
                name: "ws_proxy_connections",
                schema: "telemetry");

            migrationBuilder.DropTable(
                name: "pending_orders",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "ag_conversation",
                schema: "core");

            migrationBuilder.DropTable(
                name: "chartink_strategies",
                schema: "core");

            migrationBuilder.DropTable(
                name: "flow_workflows",
                schema: "core");

            migrationBuilder.DropTable(
                name: "historify_schedules",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "download_jobs",
                schema: "market_data");

            migrationBuilder.DropTable(
                name: "broker_accounts",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sandbox_gtt",
                schema: "sandbox");

            migrationBuilder.DropTable(
                name: "sm_strategy_run",
                schema: "core");

            migrationBuilder.DropTable(
                name: "telegram_users",
                schema: "core");

            migrationBuilder.DropTable(
                name: "watchlists",
                schema: "core");

            migrationBuilder.DropTable(
                name: "whatsapp_users",
                schema: "core");

            migrationBuilder.DropTable(
                name: "sm_strategy",
                schema: "core");
        }
    }
}
