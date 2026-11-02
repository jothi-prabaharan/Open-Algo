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
| `tests/` | Core unit tests, architecture rules, PostgreSQL integration tests. |
| `tools/legacy-schema/` | The OpenAlgo schema snapshot and the one-shot generator for the 74 ported tables. |

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

## Adding a migration

```bash
dotnet ef migrations add <Name> --project src/EPTradeBot.Infrastructure --startup-project src/EPTradeBot.Infrastructure --output-dir Persistence/Migrations
```

`ModelRulesTests.Model_has_no_changes_pending_against_the_last_migration`
fails if the model changes without one.
