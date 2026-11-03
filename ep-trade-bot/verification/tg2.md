# Verification Gate 2: Guardrails, risk engine, sandbox and kill switches

Date: 2026-11-03. Same toolchain as Gate 1 (.NET SDK 10.0.403, PostgreSQL 17,
EF Core 10.0.14). Result: **passed**. Everything below was run in this session.

## 1. What was built

| Piece | Where | What it does |
| --- | --- | --- |
| Environment guard | `Core/Trading/TradingGuard.cs`, `Api/Security/TradingGuardFilter.cs`, `Infrastructure/Trading/OrderDispatcher.cs` | Only `Production` may trade a LIVE account. Written as "Production allows", so `Staging`, `production` (wrong case), an empty name or a typo all lock LIVE. Enforced three times: by the HTTP filter (403), by the dispatcher, and again under the account lock. |
| Order dispatcher | `Infrastructure/Trading/OrderDispatcher.cs` | The one path every order takes (manual, API, webhook, strategy, Flow, agent, Action Center, kill switch): guard, then risk, then sandbox or broker, then audit. The per-account lock is held from the risk check to the write. |
| Account risk rules | `Core/Trading/AccountRiskRules.cs` | Kill switch, freeze limit, `MaxOrderQuantity`, `MaxDailyLoss` (realised plus marked-to-market for the IST day), `MaxPositionValue` (projected gross after the order). |
| Risk core (port) | `Core/Risk/` | OpenAlgo's `services/risk`: stop, target, continuous and stepped trailing, aggregate limits, lock profit, trail to entry. Pure. |
| Sandbox engine | `Core/Sandbox/SandboxRules.cs`, `Infrastructure/Sandbox/SandboxEngine.cs` | 1,00,00,000 INR per PAPER account; OpenAlgo's leverage defaults and fill rules; positions, average price, realised P&L, pro-rata margin release, flips, cancel. |
| Kill switches | `Infrastructure/Trading/KillSwitchService.cs`, `POST /api/v1/killswitch/account/{id}`, `POST /api/v1/killswitch/global` | Flag first (blocks new orders at once), cancel open orders, square off through the dispatcher (split by freeze quantity), halt hosted strategies (global), append an IST audit record. |
| Audit immutability | `Infrastructure/Persistence/Interceptors/AuditImmutabilityInterceptors.cs` | Refuses a tracked update or delete of an audit record, and any UPDATE, DELETE or TRUNCATE command against the three immutable audit tables, which is what `ExecuteUpdate` and `ExecuteDelete` produce. |
| Management API | `Api/Endpoints/AccountEndpoints.cs` | Accounts (LIVE/PAPER), risk settings, orders, positions, funds, kill switches; `/api/v1/environment` lists locked accounts. Protected by `X-Admin-Token` until login lands in TG5. |

Design rules worth knowing:

- **An exit is never refused by a loss or exposure limit.** An order that only
  reduces a position passes the daily-loss and position-value checks, and a
  closing order needs no margin. A sell larger than the long it closes is new
  exposure for the excess and is checked.
- **A check that cannot be computed refuses.** No price for an opening order
  means `price_unavailable`. A LIVE account whose positions cannot be read
  means `exposure_unavailable`. A LIVE account with no limits set is refused
  until limits are set; PAPER accounts without limits may trade.
- **A kill switch that could not close something says so.** The flag stays on,
  `completed` is false, and the failure is recorded. In this build that is
  every LIVE account, because broker connections arrive in TG3.

## 2. Build

`dotnet build -c Release`:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## 3. Tests

`EPTRADE_TEST_PG=... dotnet test` against PostgreSQL 17:

```
Passed!  - Failed: 0, Passed: 116, Skipped: 0, Total: 116 - EPTradeBot.Core.Tests.dll
Passed!  - Failed: 0, Passed:  32, Skipped: 0, Total:  32 - EPTradeBot.Architecture.Tests.dll
Passed!  - Failed: 0, Passed:  16, Skipped: 0, Total:  16 - EPTradeBot.IntegrationTests.dll
```

The Gate 2 claims, and the test that proves each:

| Claim | Test |
| --- | --- |
| A LIVE order over HTTP in Development gets 403 before the handler, and nothing is placed | `GuardTests.Live_order_over_http_in_Development_is_refused_with_403_before_the_handler` |
| A LIVE order in Development is refused from every entry path (8 sources), and each refusal is audited with its IST time | `GuardTests.Live_order_in_Development_is_refused_from_every_source_and_audited` |
| Development marks LIVE accounts locked for the UI | `GuardTests.Development_reports_live_accounts_as_locked` |
| Production lets LIVE past the guard (it then fails closed, since no broker is connected yet) and fills PAPER | `GuardTests.Production_lets_live_orders_past_the_guard_and_paper_orders_fill` |
| Max order quantity: 100 allowed, 101 refused | `RiskAndSandboxTests.Max_order_quantity_boundary` |
| Max daily loss blocks new positions but lets the exit through, and a realised loss keeps counting | `RiskAndSandboxTests.Max_daily_loss_blocks_new_positions_but_not_the_exit` |
| Max position value counts what is already held; the exact limit is allowed | `RiskAndSandboxTests.Max_position_value_boundary_counts_what_is_already_held` |
| **Ten concurrent orders cannot spend the same margin**: exactly two of ten fit, funds, positions, trades and orders all agree. This calls the sandbox directly, bypassing the account lock, so the row versions alone hold the line | `RiskAndSandboxTests.Concurrent_orders_cannot_spend_the_same_margin` |
| A limit order blocks margin, releases it on cancel, and fills at its limit when the price crosses | `RiskAndSandboxTests.A_limit_order_blocks_margin_until_it_fills_or_is_cancelled` |
| Account kill switch cancels the open order, squares off a long and a short, frees all margin, blocks the next order, writes an IST event, and releasing it allows trading again | `KillSwitchAndAuditTests.Account_kill_switch_cancels_squares_off_blocks_and_audits` |
| Global kill switch refuses without `KILL ALL`; flattens every PAPER account; halts hosted strategies; reports the LIVE account (no broker yet) as not completed rather than done | `KillSwitchAndAuditTests.Global_kill_switch_needs_confirmation_and_never_reports_a_failed_account_as_done` |
| Audit records survive a tracked edit, a tracked delete, `ExecuteDelete` and `ExecuteUpdate` | `KillSwitchAndAuditTests.Audit_records_cannot_be_updated_or_deleted_by_any_ef_path` |
| The C# risk core matches OpenAlgo on all 35 shared vectors (`test/risk/vectors.json`, read in place) | `RiskVectorTests` |

## 4. Do the tests catch what they claim? (mutation check)

Each guard was broken on purpose, only its tests were run, and the code was
restored (`tools/mutation/mutate.py tools/mutation/tg2.json`; each pattern must match exactly once):

| Mutation | Result |
| --- | --- |
| M1 Guard allows LIVE in every environment | 2 failed |
| M2 Dispatcher skips its first guard check | **survived**: the re-check under the account lock refused (see below) |
| M2b Dispatcher skips both guard checks | 1 failed |
| M3 HTTP guard filter disabled | 1 failed |
| M4 Funds row version removed | 1 failed (the concurrency test overspent) |
| M5 Exits treated as new exposure | 1 failed |
| M6 Kill switch skips the square-off | 1 failed |
| M7 Kill switch reports failures as completed | 1 failed |
| M8 Command interceptor never matches | 1 failed |
| M9 SaveChanges interceptor never refuses | 1 failed |
| M10 Dispatcher audits only accepted orders | 2 failed |
| M11 Trailing stop no longer clamped to the peak | 1 of 35 vectors failed |
| M12 Zero accepted as a price | 15 of 35 vectors failed |
| M13 Closing orders charged margin | 1 failed |

M2 is not a missing test. The dispatcher checks the guard before taking the
account lock and again after, so an account switched to LIVE, or disabled, a
moment earlier is still caught. Removing one check left the other refusing;
removing both turned the test red.

## 5. Zero raw SQL, revisited

TG2 needed an interceptor that **reads** the SQL EF Core generated, and the
Gate 1 ban on the whole `DbCommand` type stopped it compiling. The ban now
targets what raw SQL needs instead: creating a command
(`DbConnection.CreateCommand`), executing one (`ExecuteNonQuery`,
`ExecuteReader`, `ExecuteScalar` and their async forms), `NpgsqlCommand`,
`NpgsqlBatch` and `NpgsqlDataSource`, plus every EF Core raw-SQL method as
before. A throwaway probe file was built and deleted:

```
ZzProbe.cs(5,53):  error RS0030: The symbol 'DbConnection.CreateCommand()' ...
ZzProbe.cs(5,134): error RS0030: The symbol 'DbCommand.ExecuteNonQuery()' ...
ZzProbe.cs(6,47):  error RS0030: The symbol 'DbCommand.ExecuteScalar()' ...
ZzProbe.cs(7,58):  error RS0030: The symbol 'DbCommand.ExecuteReaderAsync(CancellationToken)' ...
ZzProbe.cs(8,58):  error RS0030: The symbol 'NpgsqlCommand' ...
```

The same probe showed that a ban entry on the `CommandText` setter had no
effect, so it was removed rather than left looking like protection. Setting
text on a command is harmless when no command can be created or executed.

The new migration `SandboxRowVersions` maps the sandbox row versions to
PostgreSQL's `xmin`. Its generated SQL only records itself in the history table:

```
START TRANSACTION;
INSERT INTO core.__ef_migrations_history ("MigrationId", "ProductVersion")
VALUES ('20261103051239_SandboxRowVersions', '10.0.14');
COMMIT;
```

## 6. Corrections and limits

- **Plan corrected.** The plan said OpenAlgo auto-splits orders above the freeze
  limit. It does not: it only reports the freeze quantity, and 0 means unknown.
  The port refuses above the freeze (pointing to split orders), never reads 0
  as 1, and the kill switch splits its own square-off by the freeze quantity.
- **LIVE accounts cannot trade yet in any environment.** In Production they
  pass the guard, then the risk manager cannot read their positions and refuses
  (`exposure_unavailable`). Broker plugins arrive in TG3.
- **Not yet ported from OpenAlgo's sandbox:** GTT orders, CNC holdings and T+1
  settlement, catch-up after a restart, the auto square-off timers, the weekly
  capital reset, and fractional (crypto) quantities. Tracked for TG4.
- **Matching runs on demand.** `SandboxEngine.MatchAsync` fills open orders
  against a quote. In TG2 quotes come from the Development-only
  `POST /api/v1/sandbox/quotes`; the market feed drives it from TG3.
- **The admin token is a stopgap.** It is required (startup fails without a
  32-character `Management:AdminToken`) and compared in constant time, but it
  is one shared secret. Login and sessions replace it in TG5. Until then, do
  not expose the API beyond localhost.
- **A refusal at the HTTP filter is not written to the audit log.** The filter
  answers 403 before an order intent exists. Every refusal inside the
  dispatcher is audited, which covers every non-HTTP source.
- **No Testcontainers**, as in Gate 1: the tests use the server named by
  `EPTRADE_TEST_PG`. Each test creates and drops its own database. After a
  normal run, none remain. One database left behind by an interrupted mutation
  run was removed by hand.
