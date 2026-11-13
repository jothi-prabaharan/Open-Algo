# EP Trade bot: Task List

Status: plan approved 2026-11-02 with the default for every decision (D1 to D8).
Task Groups 1 and 2 done; Task Group 3 in progress (passes 1 to 9 verified; active brokers Zerodha, Angel One, Paytm Money; the rest deferred to TG7). Evidence in [`tg1.md`](../../ep-trade-bot/verification/tg1.md), [`tg2.md`](../../ep-trade-bot/verification/tg2.md) and [`tg3.md`](../../ep-trade-bot/verification/tg3.md).

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
- [x] Sandbox GTT, CNC holdings and T+1, catch-up after restart and auto square-off timers, deferred from here, were done in TG4. Still open: admin token replaced by login (TG5)

## TG3: Brokers, /api/v1, streaming (active brokers: Zerodha, Angel One, Paytm Money)

Scope change: only Zerodha, Angel One and Paytm Money are finished inside this group. Every other broker is deferred to TG7, including the five reference brokers already built (their work so far is kept and listed there).

Shared pieces, done:
- [x] Enumerate Flask `/api/v1` URL map; reconcile count (76 pairs, 74 application endpoints; the brief's 57 is out of date)
- [x] `IBrokerPlugin` contract, discovery gated by `Brokers:Enabled`, keys read when called, recorded-fixture harness
- [x] Per-account API keys (hashed), encrypted broker tokens bound to their account
- [x] Symbol master and multi-order pairs: symbol, search, expiry, instruments, optionsymbol, basketorder, splitorder, optionsorder, optionsmultiorder (pass 5)
- [x] Freeze limits seeded from NSE's list at startup (empty table only)
- [x] multiquotes (batched per broker), syntheticfuture, optionchain (Greeks not yet; they arrive with TG4) (pass 6)
- [x] market/holidays and market/timings, chart GET and POST, pnl/symbols (pass 7)
- [x] history, intervals, ticker (JSON and plain text) and margin endpoints (passes 8 and 9)
- [x] NetMQ bus (SUB binds, PUBs connect, startup fails on a taken port) and 8765 WebSocket proxy (OpenAlgo protocol)
- [x] SignalR order-update stream (and WebSocket `subscribe_orders`)

Per active broker (contract-verified means 100% of its recorded fixtures match OpenAlgo, apart from recorded deliberate differences):
- [x] Zerodha: orders, books, funds, quotes, depth, multiquotes, history (24 fixtures), ticker (20), margin (23)
- [x] Zerodha: MCX (units to contracts at the Kite boundary, lot from the master contract; 21 fixtures, 16 mutation checks)
- [x] Zerodha: GTT (60 fixtures, single and two-leg triggers, MARKET protected into LIMIT, MCX in contracts)
- [x] Zerodha: login (request token exchanged at /session/token, kept as api_key:access_token as Kite expects; 5 fixtures)
- [x] Zerodha: market-data feed (Kite ticker; 25 scenarios against the Python adapter) onto the bus
- [x] Angel One: orders, books, funds, quotes, depth, multiquotes, history (21 fixtures), margin (15)
- [x] Angel One: GTT (51 fixtures; an OCO is two rules joined by a dash, rolled back if the pair fails)
- [x] Angel One: login (client code, PIN and one-time code; token and feed token; 5 fixtures)
- [x] Angel One: market-data feed (SmartAPI stream v2; 24 scenarios against the Python adapter) onto the bus
- [x] Paytm Money: orders, books, funds, quotes, depth, multiquotes, smart orders, close-all, cancel-all (41 fixtures); the broker has no history, margin calculator or GTT
- [x] Paytm Money: login (request token exchanged for the access and public tokens; 5 fixtures)
- [x] Paytm Money: market-data feed (broadcast stream; 21 scenarios against the Python adapter) onto the bus

Shared endpoints still open:
- [x] The 4 GTT endpoints (place, modify, cancel, order book), for Zerodha and Angel One; sandbox accounts are told GTT is not there yet (TG4)
- [x] Gate 3 artifact, final (covers the three active brokers and the shared pieces; `verification/tg3.md` sections 1 to 23)

## TG7: Remaining brokers (deferred out of TG3)

Not worked on until Zerodha, Angel One and Paytm Money are finished and the next pending groups are done. Each broker follows the same recipe: orders and books, funds, quotes and depth, history and margin where the broker has them, GTT, login, feed, with recorded fixtures and mutation checks.

Built already during TG3, kept as they are (contract-verified for the pieces listed):
- [x] Dhan (orders, books, funds, quotes, history, margin), Dhan Sandbox (orders, books, funds, quotes, history; its margin calculator is not ported)
- [x] Upstox (orders, books, funds, quotes, history, margin)
- [x] Fyers (orders, books, funds, quotes, history, margin, option chain call)
- [x] Delta Exchange (orders, books, funds, quotes, history, margin)
- [x] Login for Upstox, Fyers, Delta Exchange and Dhan (`verification/tg7.md`)
- [x] Upstox GTT (`verification/tg7.md`)
- [x] Dhan GTT (`verification/tg7.md`)
- [x] Fyers GTT (`verification/tg7.md`)
- [x] Dhan Sandbox margin (`verification/tg7.md`)
- [ ] For the rest of those five: market-data feeds (Dhan, Upstox, Fyers; Delta's is done), Delta leverage and fractional quantities

Not started (30): fivepaisa, fivepaisaxts, aliceblue, arrow, compositedge, definedge, firstock, flattrade, groww, hdfcsecurities, hdfcsky, ibulls, iifl, iiflcapital, indmoney, jainamxts, kotak, motilal, mstock, nubra, pocketful, rmoney, rupeezy, sahi, samco, shoonya, tradejini, tradesmart, wisdom, zebu
- [ ] Gate 7 artifact (per-broker verification status for all of the above)

## TG4: Strategy host, Flow, Action Center, agent, MCP, tools
- [x] The 31 /api/v1 pairs that sit on TG4 features: option greeks (2), portfolio (4), SIP (2), sector rotation (2), Telegram (9), WhatsApp (1) are complete; strategy (9) is complete for batch strategies: the 5 read routes and the 4 lifecycle routes, on a run engine with the ported risk core plus the strategy schedule, the webhook and signal mode (tick-source switching and the separate recovery routine are not brought across; see `verification/tg4.md`)
- [x] Quartz.NET with IST schedules and weekday controls (`Core/Scheduling`, `Infrastructure/Scheduling`; 55 tests, 33 mutants)
- [x] Hosted strategy process supervisor (`Infrastructure/Scheduling/StrategySupervisor`; 38 tests, 27 mutants; resource limits left to TG6)
- [x] Flow engine and all 61 node types (`Core/Flow`, `Infrastructure/Flow`; 110+ mutants)
- [x] 127 indicators with parity tests (`Core/Indicators`; 255 recorded cases against `openalgo.ta`, 21 mutation checks)
- [x] Action Center Auto / Semi-Auto, re-check at approval
- [x] Agent on Microsoft.Extensions.AI, proposal-only order tools (`Infrastructure/Agent`; 25 tests, 22 mutants; no tool can send an order)
- [x] MCP stdio + HTTP/OAuth
- [x] 20 tool back ends with numeric parity tests (`Core/Options`, `Core/Portfolio`; about 400 recorded cases, 100+ mutants); `/sip`, `/sectorrotation` and `/portfolio` routes (tearsheet answers 501)
- [x] Gate 4 artifact (`ep-trade-bot/verification/tg4.md`)
- [x] Strategy module run engine: batch runs, signal mode, the schedule and the webhook, entry and exit orders, the risk monitor with the ported risk core, daily loss limit, exit time

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
