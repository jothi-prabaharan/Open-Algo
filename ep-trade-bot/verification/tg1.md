# Verification Gate 1: Scaffolding and code-first PostgreSQL data layer

Date: 2026-11-02. Environment: Ubuntu 24.04 container, .NET SDK 10.0.403
(runtime 10.0.14), PostgreSQL 17 (PGDG), EF Core 10.0.14, Npgsql provider 10.0.3.

Result: **passed**. Everything below was run in this session, and the outputs
are copied verbatim (long lines trimmed).

## 1. Build

`dotnet build -c Release` (warnings are errors, MSBuild warnings included):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 2. Migration applied by the app at startup

On an empty server (database dropped first), the API was started with
`ASPNETCORE_ENVIRONMENT=Development`. `DatabaseMigrator` created the database
and the history table through EF Core's own services, then ran `MigrateAsync()`:

```
Applying migration '20261102182845_InitialCreate'.
Database schema is up to date at migration 20261102182845_InitialCreate (1 applied).
```

Count of `fail` lines in the startup log: **0**. A second start is a no-op:

```
No migrations were applied. The database is already up to date.
Database schema is up to date at migration 20261102182845_InitialCreate (1 applied).
```

`GET /api/v1/environment` on that instance:

```json
{"product":"EP Trade bot","environment":"Development","liveTradingAllowed":false}
```

The migration file contains 90 `CreateTable` calls, 181 `CreateIndex` calls,
6 `EnsureSchema` calls and **0 `Sql()` calls**.

## 3. Schemas and tables, read back from PostgreSQL

| Schema | Tables | Contents |
| --- | --- | --- |
| `core` | 54 | 50 ported OpenAlgo tables, `broker_accounts` (LIVE/PAPER), `account_risk_settings`, `hosted_strategies`, and EF's `__ef_migrations_history` |
| `audit` | 15 | 12 ported (traffic, IP bans, 404 and invalid-key trackers, login attempts, API order log, analyzer log, alert log, Telegram and WhatsApp command logs, agent audit, Action Center queue), plus `order_audit_log`, `kill_switch_events`, `action_center_decisions` |
| `sandbox` | 9 | funds (1,00,00,000 INR default), orders, trades, positions, holdings, GTT, GTT legs, daily P&L, config |
| `market_data` | 8 | `candles` (replaces DuckDB `market_data`), watchlist, data catalogue, download jobs and items, symbol metadata, schedules and executions |
| `telemetry` | 4 | health metrics, health alerts, `bus_lane_status`, `ws_proxy_connections` |
| `latency` | 1 | `order_latency` |

BRIN indexes, all created through `HasMethod("brin")`:

```
audit.action_center_decisions   ix_action_center_decisions_occurred_at_brin
audit.kill_switch_events        ix_kill_switch_events_occurred_at_brin
audit.order_audit_log           ix_order_audit_log_occurred_at_brin
market_data.candles             ix_candles_ts_brin
telemetry.ws_proxy_connections  ix_ws_proxy_connections_connected_brin
```

## 4. Zero raw SQL

**Workspace search** across all of `ep-trade-bot/` (excluding `bin/` and `obj/`)
for `FromSqlRaw|FromSqlInterpolated|ExecuteSqlRaw|ExecuteSqlInterpolated|Dapper`.
Every match is either the ban list or the test that enforces it. No code calls
any of them:

```
./BannedSymbols.txt:1   M:...RelationalQueryableExtensions.FromSqlRaw...       (ban entry)
./BannedSymbols.txt:2   M:...RelationalQueryableExtensions.FromSqlInterpolated... (ban entry)
./BannedSymbols.txt:4-8 M:...ExecuteSqlRaw / ExecuteSqlRawAsync...              (ban entries)
./BannedSymbols.txt:9-10 M:...ExecuteSqlInterpolated(Async)...                  (ban entries)
./BannedSymbols.txt:20  T:Dapper.SqlMapper;Dapper is prohibited.                (ban entry)
./tests/EPTradeBot.Architecture.Tests/RawSqlBanTests.cs:9,46,61                 (the enforcing test)
```

**The ban fails the build.** A throwaway file calling all four methods was
added to Infrastructure and built, then deleted:

```
ZzProbe.cs(6,52): error RS0030: The symbol 'RelationalQueryableExtensions.FromSqlRaw<TEntity>(...)' is banned in this project: Raw SQL is prohibited. Use LINQ.
ZzProbe.cs(7,49): error RS0030: The symbol 'RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(...)' is banned in this project: Raw SQL is prohibited. ...
ZzProbe.cs(8,52): error RS0030: The symbol 'RelationalQueryableExtensions.FromSqlInterpolated<TEntity>(...)' is banned in this project: ...
ZzProbe.cs(9,49): error RS0030: The symbol 'RelationalDatabaseFacadeExtensions.ExecuteSqlInterpolated(...)' is banned in this project: ...
Build FAILED.
```

The same list also bans `SqlQuery`/`SqlQueryRaw`, `MigrationBuilder.Sql`,
`HasDefaultValueSql`, `HasComputedColumnSql`, `HasFilter`, `GetDbConnection`,
`NpgsqlCommand`, `DbCommand`, `SqlCommand` and `Dapper.SqlMapper`.

## 5. Tests

`EPTRADE_TEST_PG=... dotnet test` against PostgreSQL 17:

```
Passed!  - Failed: 0, Passed: 35, Skipped: 0, Total: 35 - EPTradeBot.Core.Tests.dll
Passed!  - Failed: 0, Passed: 32, Skipped: 0, Total: 32 - EPTradeBot.Architecture.Tests.dll
Passed!  - Failed: 0, Passed:  2, Skipped: 0, Total:  2 - EPTradeBot.IntegrationTests.dll
```

What they cover:

- **Architecture (32).**
  - Raw-SQL token scan (19 tokens) over every `.cs` file, migrations included, plus a check that no project references Dapper or an ADO.NET client.
  - Every table sits in one of the six schemas, and each schema has tables.
  - Every entity has its own `IEntityTypeConfiguration`, and all 74 OpenAlgo tables are present.
  - BRIN index on `candles.ts`.
  - `AccountType` is stored as text and is non-nullable.
  - No SQL defaults, computed columns or index filters.
  - Every row version maps to `xmin`.
  - The model has no changes pending against the last migration.
  - Core does not reference EF Core, ASP.NET or Npgsql.
- **Core (35).** Data Annotations parity with OpenAlgo's marshmallow schemas:
  - allowed values and the OpenAlgo defaults
  - positive quantities, fractional quantity allowed only on CRYPTO
  - basket items validated and reported by position (`orders[1].quantity`)
  - options leg limit of 1 to 20, the option offset rule, the OCO GTT rules, no MIS on GTT
  - strategy start refused without `mode` or with `mode: "paper"`
  - bounded event limit, real calendar dates on history requests
  - LIVE/PAPER required on accounts, risk-setting ranges, the `KILL ALL` confirmation
  - IST conversion and the IST trading-day rollover
- **Integration (2).**
  - The real API host boots against an empty database, and startup alone applies every migration.
  - A PAPER account with risk settings, and a candle, round-trip through `core` and `market_data`.

Each test database is created and dropped by the test. After the run, 0 remain.

### Do the tests guard what they claim? (mutation check)

Each guard was broken on purpose, the test that claims to cover it was run,
and the code was restored. Every one went red:

| Mutation | Test run | Result |
| --- | --- | --- |
| Raw-SQL call text added in a comment | `RawSqlBanTests` | 1 failed |
| Row version no longer mapped to `xmin` | `Every_row_version_maps_to_the_xmin_system_column` | 1 failed |
| A column length changed with no migration | `Model_has_no_changes_pending_against_the_last_migration` | 1 failed |
| Snake-case column convention removed | `Model_has_no_changes_pending_against_the_last_migration` | 1 failed |
| Fractional-quantity rule disabled | `Fractional_quantity_is_refused_outside_crypto_and_allowed_on_crypto` | 1 failed |
| Validator no longer walks into list items | `Basket_items_are_validated_and_reported_by_position` | 1 failed |

After restoring: 35 + 32 + 2 passed again.

## 6. Defect found and fixed during this gate

The first integration run failed: `null value in column "version" of relation
"broker_accounts"`. The snake_case column convention had renamed the
optimistic-concurrency property to an ordinary `version` column. It must map to
PostgreSQL's `xmin` system column, which nothing writes. Fixed with an explicit
`xmin` mapping, the migration regenerated, and the new architecture test
`Every_row_version_maps_to_the_xmin_system_column` keeps it fixed.

## 7. Deviations from the plan, and limits

- **No Testcontainers.** The container has a Docker client but no daemon. The
  integration tests use a real PostgreSQL 17 server named by `EPTRADE_TEST_PG`
  and report as skipped, not passed, when it is unset. Testcontainers can be
  added when the tests run somewhere with Docker.
- **Candle time is `timestamptz`**, not Historify's epoch-seconds `BIGINT`. The
  importer (TG6) converts.
- **DuckDB's columnar speed is not reproduced.** BRIN plus the primary-key B-tree
  is the access path; performance is measured in TG4.
- **Audit immutability** (decision D5) is designed for here (`IImmutableAuditRecord`,
  identity-always keys, no update API) and enforced in TG2 by a SaveChanges
  interceptor and a command interceptor.
- **Ported tables keep OpenAlgo's column types.** For example, sandbox amounts are
  `numeric` where OpenAlgo used `DECIMAL`, and `double precision` where it used
  `FLOAT`. Changing a type is a decision for the port of the code that uses it.
