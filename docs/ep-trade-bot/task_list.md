# EP Trade bot: Task List

Status: awaiting approval of [`implementation_plan.md`](implementation_plan.md),
including decisions D1 to D8.

## Pre-flight
- [ ] Decisions D1 to D8 answered (or defaults accepted)
- [ ] Logo committed to `ep-trade-bot/branding/logo-source.png` (needed by TG5 only)

## TG1: Scaffolding and data layer
- [ ] Install .NET 10 SDK and PostgreSQL 17 in the session
- [ ] Solution, 5 projects, 4 test projects, central package management
- [ ] Entities and `IEntityTypeConfiguration` for 74 legacy tables + new risk/kill-switch/account entities
- [ ] Six schemas: core, sandbox, audit, latency, telemetry, market_data
- [ ] BRIN index on `market_data.candles.ts` via Fluent API
- [ ] Data Annotations on all request DTOs and options; validation endpoint filter
- [ ] `InitialCreate` migration; `MigrateAsync()` at startup
- [ ] Raw-SQL ban: banned-API analyzer + architecture test
- [ ] Gate 1 artifact

## TG2: Guardrails, risk, sandbox, kill switches
- [ ] `ITradingGuardService` (middleware + dispatcher)
- [ ] `IAccountRiskManager` (pure core + adapter; reuse `test/risk/vectors.json`)
- [ ] Sandbox engine with atomic order + margin transaction
- [ ] Account and global kill-switch endpoints with append-only IST audit
- [ ] Audit immutability interceptor
- [ ] Gate 2 artifact (xUnit + Testcontainers)

## TG3: Brokers, /api/v1, streaming
- [ ] Enumerate Flask `/api/v1` URL map; reconcile count
- [ ] `IBrokerPlugin` contract and test harness
- [ ] All `/api/v1` endpoints with recorded-fixture contract tests
- [ ] Reference adapters: Zerodha, Dhan, Dhan Sandbox, Angel One, Upstox, Fyers, Delta Exchange
- [ ] Remaining adapters in batches (tracked per broker: contract-verified / live-verified)
- [ ] NetMQ bus (SUB binds, PUBs connect) and 8765 WebSocket proxy
- [ ] SignalR order-update stream
- [ ] Gate 3 artifact

## TG4: Strategy host, Flow, Action Center, agent, MCP, tools
- [ ] Quartz.NET with IST schedules and weekday controls
- [ ] Hosted strategy process supervisor
- [ ] Flow engine and node types
- [ ] 127 indicators with parity tests
- [ ] Action Center Auto / Semi-Auto, re-check at approval
- [ ] Agent on Microsoft.Extensions.AI, proposal-only order tools
- [ ] MCP stdio + HTTP/OAuth
- [ ] 20 tool back ends with numeric parity tests
- [ ] Gate 4 artifact

## TG5: Angular PWA
- [ ] Angular 22 zoneless workspace, Tailwind 4, SignalStore
- [ ] PWA manifest, service worker, icons from logo
- [ ] Dashboard: environment banner, account cards, risk settings, kill switches, Action Center
- [ ] Trading terminal (openalgo-charts), Flow editor, agent panel
- [ ] 20 tool views
- [ ] Gate 5 artifact (prod build, Lighthouse, Playwright walkthrough)

## TG6: Deployment
- [ ] systemd dev and prod units
- [ ] Caddyfile
- [ ] Optional legacy data importer
- [ ] Gate 6 artifact and final walkthrough
