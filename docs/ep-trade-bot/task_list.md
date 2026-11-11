# EP Trade bot: Task List

Status: plan approved 2026-11-02 with the default for every decision (D1 to D8).
Task Groups 1 and 2 done; Task Group 3 in progress (passes 1 to 7 verified). Evidence in [`tg1.md`](../../ep-trade-bot/verification/tg1.md), [`tg2.md`](../../ep-trade-bot/verification/tg2.md) and [`tg3.md`](../../ep-trade-bot/verification/tg3.md).

## Pre-flight
- [x] Decisions D1 to D8 answered (defaults accepted)
- [ ] Logo committed to `ep-trade-bot/branding/logo-source.png` (needed by TG5 only)

## TG1: Scaffolding and data layer
- [x] Install .NET 10 SDK and PostgreSQL 17 in the session (no Docker daemon: integration tests use a real server via EPTRADE_TEST_PG)
- [x] Solution, 3 of 5 projects (Core, Infrastructure, Api; Mcp.Stdio in TG4, Importer in TG6), 3 of 4 test projects (Contract.Tests in TG3), central package management
- [x] Entities and `IEntityTypeConfiguration` for 74 legacy tables + new risk/kill-switch/account entities
- [x] Six schemas: core, sandbox, audit, latency, telemetry, market_data
- [x] BRIN index on `market_data.candles.ts` via Fluent API
- [x] Data Annotations on all request DTOs and options; validation endpoint filter
- [x] `InitialCreate` migration; `MigrateAsync()` at startup
- [x] Raw-SQL ban: banned-API analyzer + architecture test
- [x] Gate 1 artifact

## TG2: Guardrails, risk, sandbox, kill switches
- [x] `ITradingGuardService` (endpoint filter + dispatcher, checked again under the account lock)
- [x] `IAccountRiskManager` (pure rules + adapter); OpenAlgo `services/risk` core ported, held to `test/risk/vectors.json` (35/35)
- [x] Sandbox engine with atomic order + margin transaction (market, limit, SL, SL-M, positions, P&L, cancel)
- [x] Account and global kill-switch endpoints with append-only IST audit
- [x] Audit immutability: SaveChanges interceptor + command interceptor (blocks ExecuteUpdate/ExecuteDelete)
- [x] Gate 2 artifact (xUnit on PostgreSQL 17, 14 mutation checks)
- [ ] Deferred to later groups: sandbox GTT, CNC holdings and T+1, catch-up after restart, auto square-off timers (TG4); admin token replaced by login (TG5)

## TG3: Brokers, /api/v1, streaming
- [x] Enumerate Flask `/api/v1` URL map; reconcile count (76 pairs, 74 application endpoints; the brief's 57 is out of date)
- [x] `IBrokerPlugin` contract, discovery gated by `Brokers:Enabled`, keys read when called, recorded-fixture harness
- [x] Per-account API keys (hashed), encrypted broker tokens bound to their account
- [x] 35 of 74 `/api/v1` pairs with contract tests against the Python implementation (353 fixtures: 32 Zerodha, 36 Dhan, 36 Dhan Sandbox, 32 Angel One, 33 Upstox, 33 Fyers, 30 Delta Exchange, 76 composite, 45 platform)
- [x] Symbol master and multi-order pairs: symbol, search, expiry, instruments, optionsymbol, basketorder, splitorder, optionsorder, optionsmultiorder (pass 5)
- [x] Freeze limits seeded from NSE's list at startup (empty table only)
- [x] multiquotes (batched per broker), syntheticfuture, optionchain (Fyers' own chain call; Greeks not yet) (pass 6)
- [x] market/holidays and market/timings (calendar seeded from OpenAlgo's fresh install), chart GET and POST, pnl/symbols (pass 7)
- [x] history and intervals contract-verified on Zerodha (24 fixtures); `IBrokerHistory` contract and the 350 ms spacing in place
- [x] history and intervals contract-verified on Angel One (21 fixtures)
- [x] history and intervals contract-verified on Upstox (19 fixtures)
- [x] history and intervals contract-verified on Fyers (26 fixtures)
- [ ] history for Dhan, Dhan Sandbox and Delta Exchange
- [ ] Remaining 6 broker and market-data pairs (ticker, margin, 4 GTT)
- [x] Zerodha (contract-verified; MCX, history, GTT, margin, login and ticker feed not yet)
- [x] Dhan, Dhan Sandbox (contract-verified; history, GTT, margin, login and feed not yet)
- [x] Angel One, Upstox (contract-verified; history, GTT, margin, login and feeds not yet)
- [x] Fyers, Delta Exchange (contract-verified; history, GTT, margin, login, feeds, Delta leverage and fractional quantities not yet)
- [ ] Remaining adapters in batches (tracked per broker: contract-verified / live-verified)
- [x] NetMQ bus (SUB binds, PUBs connect, startup fails on a taken port) and 8765 WebSocket proxy (OpenAlgo protocol)
- [ ] Broker market-data feed adapters onto the bus
- [x] SignalR order-update stream (and WebSocket `subscribe_orders`)
- [x] Gate 3 artifact, passes 1 to 7 (135 mutation checks)
- [ ] Gate 3 artifact, final

## TG4: Strategy host, Flow, Action Center, agent, MCP, tools
- [ ] The 31 /api/v1 pairs that sit on TG4 features (option greeks, portfolio, SIP, sector rotation, strategy, Telegram, WhatsApp)
- [ ] Quartz.NET with IST schedules and weekday controls
- [ ] Hosted strategy process supervisor
- [ ] Flow engine and node types
- [ ] 127 indicators with parity tests
- [ ] Action Center Auto / Semi-Auto, re-check at approval
- [ ] Agent on Microsoft.Extensions.AI, proposal-only order tools
- [ ] MCP stdio + HTTP/OAuth
- [ ] 20 tool back ends with numeric parity tests
- [ ] Gate 4 artifact

## TG5: Angular PWA (owned by Antigravity; not worked on in this session)
- [ ] Angular 22 zoneless workspace, Tailwind 4, SignalStore
- [ ] PWA manifest, service worker, icons from logo
- [ ] Dashboard: environment banner, account cards, risk settings, kill switches, Action Center
- [ ] Trading terminal (openalgo-charts), Flow editor, agent panel
- [ ] 20 tool views
- [ ] Gate 5 artifact (prod build, Lighthouse, Playwright walkthrough)

## TG6: Deployment (owned by Antigravity; not worked on in this session)
- [ ] systemd dev and prod units
- [ ] Caddyfile
- [ ] Optional legacy data importer
- [ ] Gate 6 artifact and final walkthrough
