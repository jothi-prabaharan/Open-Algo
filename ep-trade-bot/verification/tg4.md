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
