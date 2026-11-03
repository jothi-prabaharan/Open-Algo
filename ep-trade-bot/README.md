# EP Trade bot

A port of OpenAlgo to .NET 10 (C# 14), EF Core 10 code-first on a single
PostgreSQL 17 database, and an Angular zoneless PWA. The plan and progress
live in [`docs/ep-trade-bot/`](../docs/ep-trade-bot/implementation_plan.md);
each Task Group's evidence is under [`verification/`](verification/).

Licence: AGPL-3.0, derived from OpenAlgo. See [`NOTICE.md`](NOTICE.md).

## Layout

| Path | What it is |
| --- | --- |
| `src/EPTradeBot.Core` | Entities, request DTOs with Data Annotations, options, pure rules. No EF Core, no ASP.NET. |
| `src/EPTradeBot.Infrastructure` | `EpTradeDbContext`, one `IEntityTypeConfiguration` per entity, migrations, startup migrator. |
| `src/EPTradeBot.Api` | ASP.NET Core host (Minimal APIs, systemd integration). |
| `tests/` | Core unit tests, architecture rules, PostgreSQL integration tests, contract tests against the Python implementation. |
| `tools/legacy-schema/` | The OpenAlgo schema snapshot and the one-shot generator for the 74 ported tables. |
| `tools/contract-fixtures/` | Records /api/v1 fixtures from the Python app (`record.py <broker>`), and lists its URL map (`urlmap.py`). |
| `tools/mutation/` | Breaks one guard at a time and checks its tests go red. |

## Database

One PostgreSQL database, six schemas: `core`, `sandbox`, `audit`, `latency`,
`telemetry`, `market_data`. The schema is EF Core migrations only and is
applied by the app at startup (`DatabaseMigrator`, which also creates the
database if the role may). There is no SQL to run by hand.

**Zero raw SQL.** `BannedSymbols.txt` makes every raw-SQL entry point
(`FromSqlRaw`, `ExecuteSqlRaw`, their interpolated forms, `SqlQuery`,
`MigrationBuilder.Sql`, `HasDefaultValueSql`, `HasComputedColumnSql`,
`HasFilter`, ADO.NET commands, Dapper) a build error, and
`tests/EPTradeBot.Architecture.Tests/RawSqlBanTests.cs` scans every file for
them as well. A consequence: no CHECK constraints, SQL defaults or partial
indexes. Defaults are set in C#, ranges by Data Annotations.

## Build and test

```bash
dotnet build
```

```bash
EPTRADE_TEST_PG="Host=localhost;Port=5432;Username=ep_trade;Password=...;Database=postgres" dotnet test
```

Without `EPTRADE_TEST_PG` the PostgreSQL tests report as skipped. The role needs
`CREATEDB`: each test creates and drops its own database.

## Configuration

Settings come from `appsettings.json`, `appsettings.{Environment}.json` and
environment variables (`Section__Key`). The app refuses to start when a required
one is missing.

| Setting | What it is |
| --- | --- |
| `Database:ConnectionString` | The PostgreSQL database. |
| `Security:AppKey` | 32+ characters. Derives the key that encrypts broker tokens. Changing it makes stored tokens unreadable (log in to the broker again). |
| `Security:ApiKeyPepper` | 32+ characters. /api/v1 keys are stored as HMAC-SHA256 under it. Changing it invalidates every API key. |
| `Management:AdminToken` | 32+ characters. Guards account, key, risk and kill-switch endpoints until login arrives. Keep the API on localhost meanwhile. |
| `Brokers:Enabled` | Broker plugins to offer, for example `["zerodha"]`. |
| `Brokers:Keys:<broker>:ApiKey`, `...:ApiSecret` | A broker's application keys, read on every call. |
| `Streaming:BusPort` | Market-data bus port (5555). Fixed: if it is taken the app does not start. Give each instance its own. |
| `Streaming:WebSocketPort`, `Streaming:WebSocketHost` | WebSocket proxy (8765 on 127.0.0.1). The same protocol is served at `/ws` on the main port. |

## Using /api/v1

OpenAlgo's request and response bodies. Each key belongs to one account, and the
key a request carries is the account it trades:

```bash
curl -s -X POST localhost:5000/api/v1/accounts/<account-id>/apikeys -H "X-Admin-Token: <token>" -H "Content-Type: application/json" -d '{"label":"tradingview"}'
```

A LIVE account also needs its broker token (until the login screens arrive):

```bash
curl -s -X PUT localhost:5000/api/v1/accounts/<account-id>/broker-session -H "X-Admin-Token: <token>" -H "Content-Type: application/json" -d '{"auth_token":"<api_key>:<access_token>"}'
```

Outside Production a LIVE account cannot trade, whichever way the order arrives.

## Adding a migration

```bash
dotnet ef migrations add <Name> --project src/EPTradeBot.Infrastructure --startup-project src/EPTradeBot.Infrastructure --output-dir Persistence/Migrations
```

`ModelRulesTests.Model_has_no_changes_pending_against_the_last_migration`
fails if the model changes without one.
