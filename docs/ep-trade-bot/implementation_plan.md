# EP Trade bot: Implementation Plan (OpenAlgo to .NET 10 / PostgreSQL / Angular)

Status: **awaiting approval**. No code has been written. Each Task Group below
ends at a verification gate and a review stop; nothing in the next group starts
until the previous group's verification artifact is approved.

Companion file: [`task_list.md`](task_list.md) (the checklist, one line per task).

---

## 0. What the repository actually contains (survey, 2026-11-02)

| Item | Measured | Notes |
| --- | --- | --- |
| Python source | ~615,000 lines (`*.py`, excluding venv) | Includes tests, upgrade scripts, broker plugins |
| Frontend source | ~243,000 lines (`frontend/src/**/*.ts*`) | React 19, Vite, shadcn/ui |
| Broker plugins | 38 directories under `broker/` | Brief says 36; `dhan_sandbox` and `fivepaisaxts` are separate dirs. Final count confirmed in TG3 |
| REST API modules | 53 files in `restx_api/` | Covers orders, GTT, portfolio, market data, options, analyzer, telegram, whatsapp, sip, sector rotation |
| ORM tables | 74 distinct `__tablename__` values | Spread across `database/`, `sandbox/`, `services/` |
| Databases | 5 SQLite + 1 DuckDB | `openalgo.db`, `logs.db`, `latency.db`, `health.db`, `sandbox.db`, `historify.duckdb` |
| Licence | **AGPL-3.0** (`License.md`) | See section 1, decision D1 |
| Logo `image_5aa1c4.png` | **Not in the repository** | See decision D2 |
| Toolchain in this environment | Node 22, `psql` client present; **no `dotnet`, no PostgreSQL server, no Caddy** | NuGet, npm and dot.net are reachable; .NET 10 SDK and PostgreSQL will be installed into the session for verification |
| Latest stable Angular | 22.2.4 (npm `latest`) | Target Angular 22, zoneless is the default |

---

## 1. Decisions needed before Task Group 1

These change what gets built. Defaults are given so you can approve as-is.

**D1. Licence and attribution (AGPL-3.0).** A port of OpenAlgo to another language
is a derivative work. Rebranding is allowed, but the result must stay AGPL-3.0,
keep the upstream copyright notice, and offer its source to anyone who uses it
over the network.
*Default:* keep `License.md`, add `NOTICE.md` crediting OpenAlgo
(marketcalls/openalgo), and add an "About / Source" link in the Angular app.

**D2. Logo.** `image_5aa1c4.png` is not in the repo.
*Default:* you commit it to `ep-trade-bot/branding/logo-source.png`. The 192 and
512 icons are generated from it in TG5. Until then a plain text placeholder icon
is used, so nothing blocks earlier groups.

**D3. Where the new code lives.** You asked for no new git branch.
*Default:* a new top-level folder `ep-trade-bot/` on the current branch
(`claude/zealous-mayer-gopz1p`), holding `src/` (.NET), `web/` (Angular) and
`deploy/` (systemd, Caddy). The Python app stays untouched beside it as the
behavioural reference until cut-over, so every ported endpoint can be compared
against the original.

**D4. Broker scope per pass.** 38 broker plugins, each with its own auth, symbol
master, order mapping and streaming protocol, cannot be fully verified without
real accounts.
*Default:* in TG3, build the `IBrokerPlugin` contract, a shared test harness, and
six reference adapters ported in full: Zerodha, Dhan (+ Dhan Sandbox), Angel One,
Upstox, Fyers and Delta Exchange. The other 32 follow in batches of 4 to 6 per
review cycle using the same harness. Each adapter is marked **contract-verified**
(recorded broker responses) or **live-verified** (you confirm against a real
account). None is reported as working on the strength of a build alone.

**D5. Immutable audit without raw SQL.** Real database-level immutability means
triggers or `REVOKE UPDATE, DELETE`, and both need raw SQL. That conflicts with
the zero-raw-SQL rule.
*Default:* enforce immutability in the application. A `SaveChangesInterceptor`
throws on any `Modified` or `Deleted` entry of an `IAuditRecord`, and audit
repositories expose append and read only. Optionally, a separately documented
deployment step (outside the solution and its migrations) can apply database
role grants. Tell me if you want that step.

**D6. Existing data.** The brief does not say whether current OpenAlgo users'
SQLite and DuckDB data must carry over.
*Default:* add an optional one-shot importer (`EPTradeBot.Importer`). It reads
SQLite through EF Core's SQLite provider and DuckDB through its .NET provider
with LINQ, so it needs no raw SQL. It lands in TG6, after the schema is stable.

**D7. Agent and MCP clients.** "ChatGPT" via MCP means the remote HTTP MCP
surface (OAuth). Claude Desktop, Cursor and Windsurf use stdio or HTTP.
*Default:* port both. Stdio is a separate console project, and HTTP plus OAuth
is hosted inside the API.

**D8. Account type labels.** You specified `LIVE` / `PAPER`. The enum and the
API use exactly those values. In the UI, OpenAlgo's existing term for the
simulated destination is "Sandbox".
*Default:* show **Live** and **Paper** as you specified. Say if you prefer
"Sandbox".

---

## 2. Target solution layout

```
ep-trade-bot/
  EPTradeBot.sln
  src/
    EPTradeBot.Core/            domain entities, enums, interfaces, DTOs (Data Annotations), risk rules (pure, no I/O)
    EPTradeBot.Infrastructure/  EF Core DbContext, IEntityTypeConfiguration per entity, migrations, crypto, NetMQ bus,
                                broker plugins (one folder each), sandbox engine, Quartz jobs, Telegram, LLM providers
    EPTradeBot.Api/             ASP.NET Core host: Minimal API endpoint groups, middleware (guard, rate limit, IP ban,
                                traffic log, CSP), SignalR hub, WebSocket proxy listener on 8765, MCP HTTP, static PWA
    EPTradeBot.Mcp.Stdio/       stdio MCP server for desktop clients
    EPTradeBot.Importer/        optional legacy data importer (D6)
  tests/
    EPTradeBot.Core.Tests/            unit tests (risk rules, guard, Greeks, indicators, backtests)
    EPTradeBot.IntegrationTests/      WebApplicationFactory + Testcontainers/PostgreSQL
    EPTradeBot.Contract.Tests/        /api/v1 request/response parity against recorded OpenAlgo fixtures
    EPTradeBot.Architecture.Tests/    NetArchTest rules + raw-SQL ban (see 3.4)
  web/                          Angular 22 zoneless PWA
  deploy/
    systemd/ep-trade-dev.service, ep-trade-prod.service
    Caddyfile
  branding/
```

Rules carried over from OpenAlgo because they prevent real losses (they are
design inputs, not optional):

- Risk decisions live in one pure module (`EPTradeBot.Core/Risk`). It does no
  I/O, and every consumer translates into it. This is the port of
  `services/risk/`, and `test/risk/vectors.json` is reused as the contract test.
- An exit is claimed under the same lock that checks for a duplicate. A refused
  dispatch releases the claim.
- A fill is matched to its order id, never to the leg alone.
- A position's orders go to the broker and account that hold it, and are bound
  on the run.
- The feed is torn down only when the broker token actually changes (the
  multi-device login fix).
- The bus topology is fixed: the SUB socket binds, publishers connect, and the
  port never drifts.

---

## 3. Task Groups

### Task Group 1: Solution scaffolding and code-first PostgreSQL data layer

1. Install the .NET 10 SDK and PostgreSQL 17 into the session (for verification
   only; nothing is installed into the repo).
2. Create the solution and the projects in section 2. Turn on nullable, treat
   warnings as errors, central package management (`Directory.Packages.props`)
   and C# 14.
3. Map all 74 OpenAlgo tables onto entities across the six schemas, plus the
   new entities. Every entity gets an `IEntityTypeConfiguration<T>` that calls
   `.ToTable("name", schema: "...")`. The DbContext uses
   `ApplyConfigurationsFromAssembly`.
   - `core`: users (Argon2id hash), TOTP secrets, `broker_accounts`
     (`AccountType` LIVE/PAPER, encrypted tokens), broker keys, API keys
     (peppered hash), master contracts (`sym_token`), qty freeze, leverage,
     settings, strategies and schedules, Flow workflows, watchlists, strategy
     portfolios, ChartInk, Telegram and WhatsApp config, OAuth clients, agent
     sessions, LLM keys, market calendar, **`account_risk_settings`** (new) and
     **`account_kill_switch`** state (new).
   - `sandbox`: funds (default 1,00,00,000 INR), orders, trades, positions,
     holdings, daily P&L, configuration.
   - `audit`: order audit log (IST timestamp stored as `timestamptz` plus an IST
     display column), Action Center records, traffic logs, endpoint stats, IP
     bans, 404 tracker, kill-switch events, login attempts.
   - `latency`: order round-trip metrics per broker and stage.
   - `telemetry`: health samples, bus lane status, WebSocket proxy connections.
   - `market_data`: `candles` (symbol, exchange, interval, ts, OHLCV, OI) with a
     composite primary key and a **BRIN index on `ts`** via
     `HasIndex(...).HasMethod("brin")`, plus the Historify catalogue and
     download jobs.
4. Data Annotations on every request DTO and options class.
   `ValidateDataAnnotations().ValidateOnStart()` covers options, and a shared
   endpoint filter returns a validation problem for each request.
5. Generate the `InitialCreate` migration with `dotnet ef migrations add`. At
   startup, `await db.Database.MigrateAsync()` runs inside a retry policy before
   the host starts listening.
6. Add the raw-SQL ban in two places, so it is enforced rather than just
   checked once:
   - an architecture test that scans every `.cs` file, migrations included, for
     `FromSqlRaw|FromSqlInterpolated|FromSql(|ExecuteSqlRaw|ExecuteSqlInterpolated|ExecuteSql(|SqlQuery|SqlQueryRaw|Dapper|NpgsqlCommand|SqlCommand|migrationBuilder.Sql(`
   - a Roslyn banned-API analyzer (`BannedSymbols.txt`), so the build itself fails.

**Verification Gate 1 (artifact: `verification/tg1.md`):** `dotnet build`
output, `dotnet ef migrations script` against a scratch database (applied with
`MigrateAsync`, not by hand), a listing of the six schemas and their tables read
back through EF metadata, and the raw-SQL search output showing zero matches.

### Task Group 2: Guardrails, risk engine, sandbox and kill switches

1. `ITradingGuardService`. In Development, any order whose target account is
   `LIVE` is refused. This covers manual orders, webhooks, strategy signals,
   Flow, agent proposals and Action Center approvals. It is enforced twice: by
   endpoint-filter middleware on order routes, and inside the single order
   dispatcher that every path funnels into. The middleware can be bypassed by
   internal callers; the dispatcher cannot. `GET /api/v1/environment` exposes
   the environment and the locked accounts to the UI.
2. `IAccountRiskManager`, with a pure rule core plus an adapter.
   `MaxDailyLoss` uses realised plus marked-to-market P&L for the IST trading
   day. `MaxOrderQuantity` is the smaller of the configured limit and the
   exchange freeze quantity; above the freeze, the auto-split path applies
   instead of a rejection, matching OpenAlgo. `MaxPositionValue` is the
   projected gross exposure after the order. The kill-switch flag is checked
   first. Each refusal has a trader-readable reason.
3. Sandbox engine port: margin and leverage per product and exchange, order
   matching on LTP, auto square-off times, and P&L. The order insert and the
   margin deduction commit in one EF Core transaction, at `Serializable` or with
   a row version on `sandbox.funds`, so two orders cannot both spend the same
   margin.
4. Kill switches:
   - `POST /api/v1/killswitch/account/{accountId}` toggles the flag. When
     engaging, it blocks new orders, cancels open and pending orders, and
     squares off positions.
   - `POST /api/v1/killswitch/global` requires a confirmation token, fans out
     over every account in parallel with per-account isolation (one broker
     failing does not stop the others), and halts strategies, Flow and Quartz
     jobs.
   - Each produces an append-only audit record with an IST timestamp and a
     per-account outcome. A failed square-off is reported as failed and the
     account stays flagged, never "success".

**Verification Gate 2 (artifact: `verification/tg2.md`):** an xUnit integration
run on Testcontainers PostgreSQL proving:

- a LIVE order is refused in Development from each entry path, and allowed in
  Production
- each risk limit refuses at the boundary and allows just under it
- concurrent sandbox orders cannot overspend margin (a barrier test)
- both kill switches cancel orders, square off positions and write audit rows
- an audit row cannot be updated or deleted

### Task Group 3: Broker abstraction, /api/v1 and streaming

1. `IBrokerPlugin` with the capability segments OpenAlgo plugins expose: auth,
   orders, GTT, portfolio, funds, quotes, depth, history, symbol master,
   streaming and margin. Plugins are discovered at startup and gated by
   `ValidBrokers` configuration. Keys are read when called, never cached in
   statics.
2. Every `/api/v1` method and path from `restx_api/`, with the same JSON request
   and response shapes. The API key is accepted in the body or in `X-API-KEY`,
   because external platforms cannot set headers. The final pair count is
   enumerated from the Flask app's URL map, and the brief's figure of 57 is
   reconciled in the artifact.
3. Contract fixtures: recorded request/response pairs from the Python
   implementation, replayed against the .NET endpoints.
4. Six reference broker adapters (D4), then the remaining ones in batches.
5. NetMQ bus: the proxy's SUB socket binds `ZmqPort`, and every publisher
   connects. There is no port fallback. A WebSocket proxy on 8765 keeps the same
   client protocol (auth, subscribe and unsubscribe, LTP/Quote/Depth modes,
   throttling). Order updates go out over SignalR to the Angular app.
   `/websocket/test` equivalents are kept for checking a feed.

**Verification Gate 3:** a contract-test report (each endpoint, pass or fail
against its fixture), a WebSocket health test (connect, auth, subscribe, receive
a simulated tick through the NetMQ bus, unsubscribe), and per-broker
verification status.

### Task Group 4: Strategy host, Flow, Action Center, agent, MCP, 20 tools

1. Quartz.NET with a persistent EF-backed job store (or a custom Quartz job
   store adapter on EF, so Quartz's own ADO SQL scripts are not needed), IST
   schedules and per-weekday controls. Hosted strategies run as isolated child
   processes, as in `/python` today: the user's Python scripts keep running
   under `uv`, supervised by a `BackgroundService` with log capture and reaping.
   Running arbitrary C# instead would be a different product; say if you want
   that.
2. Flow engine: port the node types and their evaluation, driven by bus ticks
   and indicator nodes.
3. Indicators: port the 127 indicators used by the agent and Flow. Values are
   verified against `openalgo.ta` output on fixed data sets.
4. Action Center with Auto and Semi-Auto modes per strategy or webhook. Single
   and bulk approval re-run the guard and risk check at approval time, not only
   at queue time.
5. Agent on Microsoft.Extensions.AI with a provider-agnostic `IChatClient` and
   tool functions over internal services. Every order tool produces a *proposal*
   that waits for explicit UI approval; no tool can dispatch an order.
6. MCP: stdio project plus HTTP/OAuth inside the API, with the tool set of
   `mcp/mcpserver.py`.
7. The 20 `/tools` back ends (Greeks via Black-76, max pain, GEX/DEX, IV
   surface, backtesters with XIRR, sector rotation RRG, and so on), each
   verified numerically against the Python implementation on the same inputs.

**Verification Gate 4:** an end-to-end test where a simulated tick crosses an
indicator condition in a Flow graph. In Auto mode the order reaches the sandbox
through guard and risk; in Semi-Auto it waits in the Action Center, then
dispatches on approval; it is refused when a risk limit is breached; and an
agent proposal never dispatches without approval. Includes a numeric parity
report for the 20 tools.

### Task Group 5: Angular 22 zoneless PWA

1. `provideZonelessChangeDetection` (the default in 22), standalone components,
   Signals, NgRx SignalStore, `httpResource`, Tailwind CSS 4, and `@angular/pwa`
   (Angular service worker plus `manifest.webmanifest`). A hand-written `sw.js`
   is not needed; it is generated as `ngsw-worker.js`. Say if you specifically
   want a file named `sw.js`.
2. Branding: "EP Trade bot" everywhere a user reads, and 192 and 512 icons plus a
   maskable variant generated from the logo (D2).
3. Library mapping. React-only packages need Angular equivalents:

   | Need | React today | Angular plan |
   | --- | --- | --- |
   | Charting | `openalgo-charts` 2.6.9 | Framework-agnostic package used directly, inside an Angular wrapper component |
   | OpenScript | `openalgo-script` 0.8.1 | Used directly |
   | Plotly | `react-plotly.js` | `plotly.js-dist-min` behind a thin directive |
   | Code editor | `@uiw/react-codemirror` | CodeMirror 6 directly |
   | Flow canvas | `@xyflow/react` | `ngx-vflow` (or Foblex Flow); node graph JSON format kept identical so existing workflows import |

4. Screens: dashboard (environment banner, account cards with risk settings,
   per-account kill switch with locked LIVE accounts in Dev, header GLOBAL KILL
   SWITCH with typed confirmation, Action Center queue), trading terminal, Flow
   editor, agent panel, and the 20 tools.

**Verification Gate 5:** `ng build --configuration production` output, a
Lighthouse PWA installability check, and a Playwright walkthrough (screenshots
plus a video) covering install readiness, LIVE locked in Dev, a risk refusal and
a kill switch.

### Task Group 6: Ubuntu deployment package

1. `ep-trade-dev.service` and `ep-trade-prod.service` (`Type=notify` through
   `UseSystemd()`, a dedicated user, `Restart=on-failure`, hardening options
   such as `ProtectSystem=strict`, an `EnvironmentFile` for secrets, and
   separate ports and databases so Dev and Prod cannot cross).
2. Caddyfile: the API, the 8765 WebSocket, the PWA static files with the right
   cache headers for `ngsw-worker.js` and the manifest, automatic TLS, CSP,
   `X-Content-Type-Options: nosniff`, HSTS and CORS.
3. Optional legacy data importer (D6).

**Verification Gate 6:** `systemd-analyze verify` on both units, `caddy validate`
on the Caddyfile, and the final migration walkthrough.

---

## 4. Risks and honest limits

- **Size.** This is a rewrite of roughly 850,000 lines across two languages. It
  will take many review cycles, not one session. The gates exist so each piece
  is checked before the next one depends on it.
- **Broker adapters** can be compiled and contract-tested here, but only you can
  live-verify them against real accounts with SEBI static-IP whitelisting.
- **Behavioural parity.** OpenAlgo carries many hard-won fixes: multi-device feed
  teardown, broker-switch fences, exit claim locks, and fill matching by order
  id. The plan ports the rules and their tests, not just the happy paths. Expect
  the contract and parity suites to be the main way regressions are caught.
- **Postgres vs DuckDB.** Columnar analytics over long candle histories
  (backtests, sector rotation) will be slower on row-store PostgreSQL. BRIN plus
  a covering B-tree index on `(symbol, exchange, interval, ts)` should be
  adequate for single-user volumes; measured in TG4.
- **Verification artifacts** are produced with this session's tools: build logs,
  test logs, and Playwright screenshots and video. They are not Antigravity
  browser recordings.
