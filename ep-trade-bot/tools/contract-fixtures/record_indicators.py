"""Record indicator fixtures from openalgo.ta on fixed data.

Every indicator is called with its defaults (a required period gets one), then with a second parameter set, on one deterministic OHLCV data set. The
.NET indicators are held to these values. NaN is written as null.

Run from the repository root:
    uv run python ep-trade-bot/tools/contract-fixtures/record_indicators.py
"""

import inspect
import json
import math
from pathlib import Path

import numpy as np

from openalgo import ta

HERE = Path(__file__).resolve().parent
N = 400


def dataset():
    rng = np.random.default_rng(20261111)
    close = 100 + np.cumsum(rng.normal(0.05, 1.2, N))
    close = np.maximum(close, 5)
    open_ = np.concatenate([[close[0]], close[:-1]]) + rng.normal(0, 0.3, N)
    spread = np.abs(rng.normal(0.8, 0.3, N)) + 0.05
    high = np.maximum(open_, close) + spread
    low = np.minimum(open_, close) - spread
    volume = np.round(rng.uniform(1000, 9000, N) * (1 + 0.5 * np.sin(np.arange(N) / 15))).astype(float)
    market = 100 + np.cumsum(rng.normal(0.03, 0.9, N))
    return {"open": open_, "high": high, "low": low, "close": close, "volume": volume, "market": market}


D = dataset()
SERIES = {"open": "open", "open_prices": "open", "high": "high", "low": "low", "close": "close", "volume": "volume", "data": "close", "source": "close",
          "data1": "close", "asset": "close", "series1": "close", "primary": "close", "expr": "close", "data2": "market", "market": "market"}
SECOND = {"series2": ("sma", 10), "secondary": ("sma", 10), "array": ("sma", 5)}
REQUIRED_DEFAULTS = {"period": 14, "length": 5, "lookback": 20}


def jsonable(value):
    if isinstance(value, np.ndarray):
        return [None if (isinstance(x, float) and math.isnan(x)) or (isinstance(x, (float, np.floating)) and not math.isfinite(x)) else (bool(x) if isinstance(x, (bool, np.bool_)) else float(x)) for x in value.tolist()]
    if isinstance(value, (tuple, list)):
        return [jsonable(v) for v in value]
    if hasattr(value, "to_numpy"):
        return jsonable(value.to_numpy())
    if isinstance(value, (np.floating, float)):
        return None if not math.isfinite(float(value)) else float(value)
    if isinstance(value, (np.integer, int, bool, np.bool_)):
        return value if isinstance(value, bool) else int(value)
    return value


def bind(name, func, alt):
    params = inspect.signature(func).parameters
    args, scalars = {}, {}
    for pname, p in params.items():
        if pname in SERIES:
            args[pname] = SERIES[pname]
        elif pname in SECOND:
            args[pname] = ["sma", SECOND[pname][1]]
        elif p.default is inspect.Parameter.empty:
            scalars[pname] = REQUIRED_DEFAULTS.get(pname, 14)
        elif alt and isinstance(p.default, bool) is False and isinstance(p.default, (int, float)) and p.default not in (0,) and pname not in ("annual", "per", "divisor"):
            scalars[pname] = type(p.default)(max(2, round(p.default * 0.6))) if isinstance(p.default, int) else p.default * 0.8
    return args, scalars


def realise(args):
    out = {}
    for k, v in args.items():
        out[k] = D[v] if isinstance(v, str) else np.asarray(ta.sma(D["close"], v[1]), dtype=float)
    return out


cases, failures = [], []
for name in sorted(n for n in dir(ta) if not n.startswith("_") and callable(getattr(ta, n))):
    func = getattr(ta, name)
    for alt in (False, True):
        args, scalars = bind(name, func, alt)
        if alt and not scalars:
            continue
        try:
            result = func(**realise(args), **scalars)
        except Exception as exc:  # recorded, not hidden
            failures.append((name, alt, repr(exc)[:120]))
            continue
        cases.append({"name": f"{name}{'_alt' if alt else ''}", "indicator": name, "series": args, "params": scalars, "result": jsonable(result)})

# The same functions on data with a gap of NaN, which blanks only the windows that contain it (rolling) or is carried across (recursive).
gap = {k: v.copy() for k, v in D.items()}
for k in ("open", "high", "low", "close", "market"):
    gap[k][100:104] = np.nan
gap["volume"][100:104] = np.nan
for name in ("sma", "ema", "wma", "hma", "stdev", "rsi", "roc", "bbands", "macd", "atr", "obv", "mfi", "supertrend", "cci", "williams_r", "dema", "kama", "linreg"):
    func = getattr(ta, name)
    args, scalars = bind(name, func, False)
    result = func(**{k: gap[SERIES[k]] if isinstance(v, str) else None for k, v in args.items()}, **scalars)
    cases.append({"name": f"{name}_gap", "indicator": name, "dataset": "gap", "series": args, "params": scalars, "result": jsonable(result)})

# VWAP anchors to the session of a timestamp, so it is called on a time-indexed series: 200 five-minute bars, all inside one day.
import pandas as pd

index = pd.date_range("2026-01-05 00:00", periods=200, freq="5min")
vwap_args = {"high": "high", "low": "low", "close": "close", "volume": "volume"}
vwap_result = ta.vwap(*[pd.Series(D[k][:200], index=index) for k in ("high", "low", "close", "volume")])
cases.append({"name": "vwap", "indicator": "vwap", "series": vwap_args, "params": {}, "slice": 200, "result": jsonable(vwap_result)})
failures[:] = [f for f in failures if f[0] != "vwap"]

(HERE / "indicators.fixtures.json").write_text(json.dumps({"n": N, "data": {k: [float(x) for x in v] for k, v in D.items()}, "gap": {k: [None if math.isnan(x) else float(x) for x in v] for k, v in gap.items()}, "cases": cases}) + "\n", encoding="utf-8")
print(f"wrote {len(cases)} cases for {len({c['indicator'] for c in cases})} indicators")
for f in failures:
    print("FAILED", f)
