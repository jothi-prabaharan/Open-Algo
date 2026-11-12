# Task Group 4: verification artifact (in progress)

Task Group 4 is built piece by piece, each held to the Python it replaces. This file grows as the pieces land. Gate 4's end-to-end test, the numeric
parity report for the 20 tools, and the sign-off come when the last piece is in.

## 1. The 127 indicators

**What was built.** Every function of `openalgo.ta` (127 of them) in `Core/Indicators`: the compute kernels in `Kernels*.cs`, mirroring
`openalgo/indicators/_backend.py` and the numba classes it leaves to the wrappers, and `IndicatorRegistry`, which takes named series and named
parameters (with openalgo.ta's defaults) and returns the outputs in the order the Python function returns them. Booleans (`crossover`, `rising`, ...)
come back as 1 and 0. No I/O; safe to call from anywhere.

**How it is held to the Python.** `tools/contract-fixtures/record_indicators.py` calls each function on one fixed, seeded OHLCV data set of 400 bars
(defaults, then a second parameter set) and records every output. 255 cases:

- 127 with defaults (VWAP included) and 110 with a second parameter set;
- 18 more on the same data with a gap of NaN in bars 100 to 103, for the indicators where a gap matters (SMA, EMA, WMA, HMA, stdev, RSI, ROC,
  Bollinger Bands, MACD, ATR, OBV, MFI, Supertrend, CCI, Williams %R, DEMA, KAMA, linear regression);
- VWAP on 200 five-minute bars inside one session.

`IndicatorParityTests` requires each value to agree with the recording to 1e-9 (relative), NaN for NaN. A completeness test requires the registry to
hold exactly the 127 recorded indicators.

**Mutation check.** 20 mutants over the kernels in `tools/mutation/tg4-indicators.json` (a seed, a smoothing divisor, a warm-up index, a NaN rule, a
direction flag, a displacement), all red. One survived at first: the floor under a standard deviation's square root, which only matters where rounding
leaves a variance a hair below zero. `IndicatorKernelTests` now pins it at levels where that happens.

**Deliberate differences.**

- `highest` and `lowest` around a NaN: OpenAlgo's window is wrong for the 13 bars after a gap (it keeps a stale maximum, leaves out the current bar,
  then goes NaN). EP Trade bot takes the maximum of the real window and skips NaN. Not pinned by a recording.
- Where an input has a NaN, ADX, TEMA and the stochastic oscillator differ from OpenAlgo's compiled kernels, which leave the output stuck or NaN for
  good after a gap. EP Trade bot's recover as soon as the gap leaves the window. Not pinned by a recording.
- VWAP takes its sessions from an optional `session_starts` series; with none it is one session. OpenAlgo derives them from the index timestamps.

**Not done.** Sessions from timestamps for VWAP anchors other than the day, and the pandas-style Series input/outputs (arrays only here).

## Action Center (Auto / Semi-Auto)

**What was built.** Each API key has an order mode, `auto` (the default) or `semi_auto`, set per key with
`PUT /api/v1/accounts/{id}/apikeys/{keyId}/order-mode`. A key in Semi-Auto does not send entry orders (place, smart, basket, split, options,
options multi, place GTT): the request is stored in `audit.pending_orders`, minus the API key, and the caller is told "Order queued for approval in
Action Center" with a `pending_order_id`. Modify, cancel, cancel-all, close-positions and the two GTT changes are refused with 403 on such a key, as
in OpenAlgo.

The admin lists and decides through `/api/v1/action-center/orders` (filter by status or account), `.../{id}/approve`, `.../{id}/reject`,
`/approve-all` and `/reject-all` (bulk runs one order at a time, oldest first). Approval claims the row (pending to submitting, one winner), then
replays the stored request through the same service as an Auto order, so the guard, the kill switches and the account's risk limits are decided again
at that moment, not at the time it was queued. The order is recorded with source `ActionCenter` and the approver as its actor. Every decision is
written to the immutable `ActionCenterDecision` log with its reason.

An order not decided by 03:00 IST expires. A LIVE account in Development is never queued (the 403 comes first, as for any live order).

**How it is held.** `ActionCenterTests` (10): Semi-Auto holds and Auto sends; approval sends once and records source and decision; the guard and
risk are re-decided at approval (kill switch on, max quantity lowered, each with a stated reason); a rejected order is never sent and cannot then be
approved, and an approved one cannot then be rejected, leaving one decision each; six concurrent approvals send once; approve-all and reject-all
run in order; blocked operations answer 403; expiry at the 03:00 IST boundary using the test clock; mode validation.

**Mutation check.** 12 mutants in `tools/mutation/tg4-action-center.json` (mode ignored, claim not exclusive, no re-decision at approval, decision
not logged, expiry boundary, bulk order, reject over any status, and others), all red. AC7 (a rejection may overwrite any status) survived at first:
no test rejected an order that was already decided. The test above now does, and AC7 is red.

**Not done.** A web screen (Task Group 5, Antigravity). Notifying the trader that an order is waiting (Telegram, TG4 later).

## Quartz.NET scheduling on India time

**What was built.** A hosted strategy (`core.hosted_strategies`) now carries an exchange, a start and stop time (HH:mm, India time), the days it
runs, a "stopped by hand" flag and a pause reason (migration `HostedStrategySchedule`). Quartz.NET 4 keeps one start and one stop trigger per enabled,
scheduled strategy, as a cron in the `Asia/Kolkata` zone on the strategy's days, plus two system jobs: the trading-day check at 00:01 IST and the
enforcer every minute. Misfires are skipped, not replayed. The database is the source of truth: Quartz holds its jobs in memory, `ScheduleRegistry`
rebuilds every trigger at start and re-syncs one strategy whenever it is created, changed or deleted. `Scheduling:Enabled` turns the scheduler off.

The rules are pure (`Core/Scheduling/ScheduleRules`), ported from OpenAlgo's `python_strategy.py`; `StrategyScheduleService` applies them and asks an
`IStrategyController` to start or stop. Endpoints (admin token): `/api/v1/hosted-strategies` create, list, read, delete; `PUT .../{id}/schedule`;
`POST .../{id}/start` and `.../stop`.

- A scheduled start is refused for a strategy the trader stopped by hand, on a day they did not choose, and on a day the exchange is closed. A
  weekend or holiday refusal pauses the strategy; the enforcer lifts that pause when the exchange trades again and the time is inside the window.
  Crypto never follows the calendar. A special session (Muhurat) on a weekend counts as a trading day, as does an MCX evening session on an equity
  holiday.
- The window is the trader's start to stop cut down to the exchange's session. The enforcer stops a running strategy only when the exchange has no
  session that day, never for the time of day: that is the scheduled stop's job.
- Stopping by hand holds until the trader starts it again by hand.
- One strategy failing in a check never stops the others being checked. A start or stop the controller reports as failed is never recorded as done.

**How it is held.** `ScheduleRulesTests` (37, pure) and `ScheduleTests` (18, real host and database; the clock is a test clock, the calendar is the
seeded one: 10 Nov 2026 is a real holiday, 8 Nov a Sunday Muhurat session). Trigger times are checked in IST against a week in 2030, because Quartz
starts a trigger at the real clock. 33 mutants in `tools/mutation/tg4-schedule.json`, all red (pure rules S1-S18, service and registry I1-I15). Three
of them (the last-second window, a scheduled stop clearing a pause, a failed stop during the enforcer pass) first needed a test of their own.

**Not done.** Nothing starts a process yet: until the strategy supervisor (next item) is registered, the default controller reports that strategy
hosting is not available and starts and stops nothing, so a strategy is never recorded as running when it is not. Restart catch-up (a start missed
while the app was down) is the deferred TG2 item.

## Hosted strategy supervisor

**What was built.** `StrategySupervisor` runs a hosted strategy as its own operating-system process and replaces the placeholder controller the
scheduler used (`Strategies:Enabled`, on by default). It ports the process handling in OpenAlgo's `blueprints/python_strategy.py`.

- **Refusals, in plain words, before anything starts:** the strategy is switched off; it has no account; the account is switched off or its kill
  switch is on; the script is missing; the script resolves (through a symbolic link) outside the strategies folder; the most that may run at once
  (`Strategies:MaxRunning`, 20) are already running.
- **A key of its own.** Each run is given a new API key on its account, in the strategy's execution mode: a semi-auto strategy's orders wait in the
  Action Center, an auto one's go through the guard and risk checks. The key is revoked when the run ends, however it ends.
- **A clean environment.** The process gets only PATH, HOME, LANG, LC_ALL, TZ, VIRTUAL_ENV, plus `STRATEGY_ID`, `STRATEGY_NAME`, the exchange, the
  execution mode, the host address and its key (under both `EPTRADE_*` and the `OPENALGO_*` names, so a script written for OpenAlgo runs unchanged).
  The database connection string, application key, API-key pepper, admin token and broker credentials never reach it.
- **Logs** go to `log/strategies/<name>-<id>.log` through the shell's own redirection, not a pipe. `GET /api/v1/hosted-strategies/{id}/logs?lines=N`
  returns the end of the log.
- **Stop** asks the process to stop (SIGTERM), waits `Strategies:StopGraceSeconds`, then ends it. A strategy that ends by itself is recorded with its
  exit code and is not restarted; the schedule decides when it runs.
- **Restart and drift.** At start and every `Strategies:ReapSeconds`, a row marked running with no process is marked stopped, and a process left from
  before a restart (matched by its command line, so a reused process id is not mistaken for it) is stopped. Shutting the platform down stops every
  strategy it started.
- **Kill switches.** Engaging an account's kill switch stops that account's strategies before it squares the account off, and holds them stopped
  (the schedule does not bring them back). The global switch stops every running strategy, including one whose account was removed while it ran. A
  strategy that will not stop is reported as a failure of the switch.

**How it is held.** `StrategyHostingTests` (22, pure: the script-path rules, the environment whitelist) and `SupervisorTests` (16), which run a real
Python interpreter on small scripts in a temporary folder. 27 mutants in `tools/mutation/tg4-supervisor.json`, all red. Two path rules (a leading `/`
and a `..` step) are each also stopped by a second rule (an empty segment, a leading dot), so removing one alone survives by design; the mutants remove both.
The global-switch mutant first survived because the account switch already stopped every strategy that had an account; a test with a strategy whose
account is gone now needs the global path.

**Not done.** Memory, CPU and file limits on a strategy process (OpenAlgo applies them with `resource` limits in the child; here the process runs with
the platform's own limits until a cgroup or `prlimit` wrapper is added in deployment, which is Task Group 6). A strategy that starts its own child
processes: only the strategy's own process is stopped. A live log stream (the tail endpoint is polled). Restart catch-up of a start missed while the
platform was down is the deferred Task Group 2 item.

## Flow engine core

**What was built.** `Core/Flow` is the workflow graph walker of OpenAlgo's Flow (`services/flow_executor_service.py`), with no I/O: the graph model
(`FlowGraph`, nodes and edges as the editor writes them), the run's variables and `{{path}}` substitution (`FlowContext`: dotted and bracketed paths, the
built-in variables from the India clock including the 03:00 `session_date`, values written as Python writes them), the arithmetic evaluator
(`MathEvaluator`: `+ - * / % **`, brackets, `floor()`, Python's modulo sign and the cap of 100 on an exponent), and the node types that need nothing from
outside: triggers and group, log, variable (set, get with a JSON path, add, subtract, multiply, divide, increment, decrement, append, parse and
stringify), math, delay and wait-until (bounded at 300 s and 1800 s, the clock and the wait are supplied), variable condition, time window (overnight
windows included) and time condition. `FlowRunner` keeps the protections that OpenAlgo added after real incidents:

- depth bound 100 and visit bound 500;
- a condition or gate answers once per run however many paths reach it, so a diamond cannot place two orders from one trigger;
- a gate waits until every wired input has a result, and refuses to evaluate when fewer inputs are wired than it was configured for;
- a condition that cannot be evaluated takes neither branch and is never recorded as settled, so a typo cannot fire the else path or feed a gate;
- a failed node stops its branch and fails the run; plain pass-through wires (logging) still run;
- an order node with an unresolved `{{reference}}` in a field that decides what is sent (symbol, quantity, price type, ...) is refused, never sent with a default;
- true/false and yes/no handles are synonyms.

**How it is held.** `tools/contract-fixtures/record_flow.py` runs OpenAlgo's own `WorkflowContext`, `NodeExecutor` and `execute_node_chain` (frozen clock,
no broker) on 32 substitution cases, 39 arithmetic cases, 13 time strings and 168 graphs, recorded in `flow.fixtures.json`. `FlowParityTests` replays all
of it: substituted text and value, arithmetic result or refusal, final variables, the log lines in order, errors (node, type, and the wording wherever this
engine writes it), the waits requested and the bound errors. 254 cases agree. 42 mutants in `tools/mutation/tg4-flow-core.json`, all red; one more
(a null value counted as found) is equivalent, since the pattern match after it treats null as unresolved, and was dropped. Four mutants first survived or
could not be killed until a scenario existed for them (the visit bound, yes/no handles, a list set through a variable, a gate met twice).

**Deliberate differences.** Python exception text (a missing key, a bad float) is not reproduced; the node and type of the failure are. A list or dict
substituted into text is written as Python writes it; a value printed by `stringify` is written as `json.dumps` does.

**Not done here.** The order, data, alert and streaming node types, persistence, triggers and the HTTP surface: next item.

## Flow: nodes, workflows, triggers, endpoints

**What was built.** 35 of the editor's 61 node types now run (the 3 gates, the trigger nodes and group, log, variable, math, delay, wait-until, variable condition, time window and time condition from the core, and
here place, smart and modify order, cancel and cancel-all, close positions, indicator, quote, depth, order status, open position, order book, trade book, position book, holdings, funds, price condition,
position check and fund check). Their behaviour is ported from OpenAlgo's executor:

- Order fields are read by `RuntimeOrderResolver`: a default is used only when the field is absent; a supplied value must resolve and be valid, so a typo never becomes quantity 1 or MARKET. A limit or stop
  order without its price, and a stop without its trigger, is refused before anything is sent. The product defaults to NRML on a derivatives segment and MIS elsewhere; a chosen product always wins.
- A modify reads the live order and changes only what the node names, never guessing action, product or price type. A close of one symbol squares off only the matching product, the other way.
- Price, position and fund checks decide only on data they could read: an unreadable quote, book or balance is an error that takes neither branch, and a position check with no symbol or a fund check with no minimum
  fails closed.
- The indicator node (`IndicatorNode`, `IndicatorInputs` generated from the recorded fixtures) computes any of the 127 indicators over a symbol's bounded history (at most 200 bars, 50 of them warm-up) or over
  another indicator's series, reporting latest, previous, a fixed tail and the value N bars back; an indicator that produces nothing is an error.
- `FlowValidator` ports the structural checks (shape, ids, known types, edges, one trigger, no cycles, nothing unreachable, size limits) and adds one: a node type the editor knows but this engine cannot run is refused
  by name at switch-on and at run, never skipped, so a workflow cannot send the orders around a basket order it never sent.

A workflow (`core.flow_definitions`, `core.flow_runs`; the OpenAlgo tables stay for importing) belongs to one account and runs in auto or semi-auto mode through the same service layer as `/api/v1`, so the
environment guard, the kill switches, the risk limits and the Action Center apply exactly as to any order. Triggers: manual run, a webhook (a token in the address, kept as a hash, shown once, rotatable; one
answer for every way the address can be wrong), a schedule (Quartz, India time, on the chosen days and only when the exchange trades), and a price alert (`PriceAlertRules` ported from OpenAlgo's monitor: the 13
conditions in both vocabularies, once-only latch and every-time re-entry, fed by bus ticks; a once-only alert is spent when it fires and its workflow switched off). A workflow that is already running is answered
"already running", never queued to run again. Endpoints (`/api/v1/flows`): list, read, create, update, delete, export, activate, deactivate, run, webhook token, runs and one run with its logs, and the public
`/api/v1/flow-webhook/{id}/{token}`.

**How it is held.** Core: `FlowApiNodesTests` (every node's request shape and each refusal), `FlowIndicatorNodeTests` (against the recorded `openalgo.ta` values), `FlowValidatorTests`, `PriceAlertRulesTests`, plus the
254 recorded cases of the engine core. Integration: `FlowTests` (14): save and switch-on rules, a run into the sandbox, a condition not met, semi-auto then approval, an over-limit order refused, missing account and
kill switch, already-running, webhook with payload / missing field / wrong token / rotation, a tick-driven price alert and its spending, an alert that can never fire, restore after restart, schedule in India time
and the holiday skip, delete. 60 mutants in `tools/mutation/tg4-flow-nodes.json`, all red; five first survived and needed a test (the product filter on close and on open position, the 200-bar ceiling, the zero
offset, the group exemption).

**Not done.** The other 26 node types (basket, split and options orders, history, symbol/expiry/option chain/synthetic future/multi quotes, calendar, holidays, timings, margin, strategy P&L, prior-period OHLC,
bar offset, the alert nodes, HTTP request, the streaming subscriptions): a workflow containing one is refused by name. Order-update triggers and the moving-percent price alerts need the account's order feed.

**Test stability found while verifying the Flow work.** Three failures that did not reproduce in isolation were each traced to a cause, not re-run into green. (1) A real race in the broker
feed: two clients letting go of the last instrument at once both reached "nothing held"; the second removed whatever connection was registered, which could be a newer one (left running with
nothing able to stop it), and stopped the old one twice (`ObjectDisposedException`). The release now removes a connection only if it is still the registered one, and a connection's stop is
idempotent; `A_late_release_on_a_closed_connection_leaves_the_newer_connection_alone_and_a_connection_stops_only_once` replays the second client's late call by name, and
`tools/mutation/tg4-feed-race.json` (two mutants) reverts each half and turns it red. (2) The test hosts chose a "free" bus port by binding port 0 and releasing it; an outgoing connection
could take that port before the host bound it. Ports now come from a range below the operating system's outgoing range, and are never handed out twice by one process. (3) The orphan-process
test reconciled a process that had only just been forked, whose command line still showed its parent's; it now waits until the process has started, as an orphan from an earlier start has.

## Agent (Microsoft.Extensions.AI)

**What was built.** The assistant is a conversation with a language model on `Microsoft.Extensions.AI`: `AgentService` builds the chat client from the provider saved under Agent settings (an
OpenAI-compatible service: address, model and key; the key is stored encrypted with the application key, never read back, and kept when a later save leaves it out; a plain-http address is refused
unless it is on this computer), wraps it in the function-invocation loop (bounded in rounds, `Agent:MaxToolRounds`, and in time, `Agent:TimeoutSeconds`), and returns the answer, the tools it
called and the orders it suggested. Endpoints: `GET` and `PUT /api/v1/agent/provider`, `POST /api/v1/agent/chat` (admin token).

- **It can read, and it can only suggest.** Its tools are quote, depth, history, one indicator over recent candles (any of the 127), the list of indicators, funds, positions, holdings, orders and
  trades, all through the platform's own API for the chosen account, and two order tools, `propose_order` and `propose_close`. A proposal is validated in the words an order node uses (a limit
  order without a price is not even suggested), refused if the environment guard would refuse the account's orders, and then put in the Action Center as a pending order marked `agent`. Nothing is sent:
  the trader approves or rejects it, and approval decides again with the guard, the kill switches and the risk limits as of that moment (the Action Center's own rule). There is no tool that
  places, modifies or cancels, which a test pins by name against the tools the model is actually given.
- **Limits that bound a mistaken model:** at most `Agent:MaxProposalsPerAnswer` (3) suggestions per answer, tool answers cut at 6,000 characters, history capped at 100 candles, the conversation
  limited to the trader's and the assistant's own turns (a `system`, `tool` or `developer` message in the request is refused, so the caller cannot write the model's instructions) and ending on
  the trader's turn. The system prompt tells the model that everything its tools return is data, not instructions; the test with a quote whose payload says "sell everything" shows that even a model
  that obeys it can only add a pending suggestion.
- **Provider refusals are explained, not coded:** a refused key, an unknown model, no credit or too many requests, an unreachable service, and a missing key or model each have a sentence saying what
  to check, and none shows a status code.

**How it is held.** `AgentTests` (25, with a scripted language model so the loop is deterministic, and the real OpenAI-compatible client against a fake service for the request it sends and the
errors it gets back). 22 mutants in `tools/mutation/tg4-agent.json`, all red; two first survived (the exchange check and the default product by segment) and needed a test.

**Not done.** Streaming answers, saved conversations, voice, and the chart, option-visualisation, web-search, strategy and OpenScript generation tools of OpenAlgo's agent. The tools that exist are the
ones the Gate 4 rule needs (read, and propose only); more are additive.

## MCP (stdio and HTTP with OAuth)

**What was built.** One JSON-RPC 2.0 server (`McpServer`) shared by two transports: `EPTradeBot.Mcp` (standard input and output, for a desktop assistant on the same computer, reaching the platform
through `HttpPlatformApi` with an API key) and `POST /mcp` on the platform (bearer token). Tools (`McpTools.All`) call the same `/api/v1` endpoints as everything else: data, account, orders (place,
smart, split, basket, options, modify, cancel, cancel all, close all) and market information.

- **Reads and writes are separate.** Each tool is read-only or a write. A read-only server lists and runs no write (by name either), a toolset can be narrowed, a write needs the `mcp:write`
  scope (a missing scope is a 403 on HTTP), and writes are rationed apart from reads (5 and 60 a minute by default, sliding window per token).
- **Arguments are checked before anything is sent:** unknown arguments, wrong types, fractions where a whole number is wanted, and values outside the allowed words (case-insensitive) are refused
  with a sentence naming the argument. Orders default to MARKET and MIS and carry the strategy label `python mcp`, as OpenAlgo's MCP does.
- **Answers are marked as data.** Every result sits under a `data` key with a note that it is data, not instructions. A refused or timed-out write is never described as safe to repeat.
- **OAuth 2.1 for the HTTP side:** dynamic client registration (https return addresses, or http on this computer only, no fragments, a cap on applications), authorization code with PKCE S256
  (required), an approval page the trader confirms with the admin token (read granted always, write only when ticked, orders default to the Action Center), codes that are hashed, single-use,
  bound to the application and return address and expire, access tokens that expire and can be revoked, rotating refresh tokens whose replay revokes the whole grant, and a switched-off
  account losing access at once. Token answers are never cached, the approval page cannot be framed and escapes every name, browser origins are checked against a list, and a missing token is told
  where to sign in.
- **Every call is audited** (tool, outcome, client, token id) and the grant's order mode applies: a semi-auto grant's orders wait in the Action Center.

**How it is held.** `McpServerTests` (63) and `McpTests` (33, against a real PostgreSQL through the full host). 56 mutants in `tools/mutation/tg4-mcp.json`, all red. Four first survived or did not
build: the target position of a smart order, upper-casing of the exchange, separate counts for reads and writes (each needed a test), and the API key sent by the HTTP client (the mutant did not
compile and was rewritten so it does).

**Not done.** Tools for charts, strategy and OpenScript generation, and the Telegram and WhatsApp tools wait for their endpoints (next item). Consent for individual tools is by scope, not per call.
