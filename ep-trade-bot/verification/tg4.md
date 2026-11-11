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

- 127 with defaults and 112 with a second parameter set;
- 15 more on the same data with a gap of NaN in bars 100 to 103, for the indicators where a gap matters (SMA, EMA, WMA, HMA, stdev, RSI, ROC,
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
