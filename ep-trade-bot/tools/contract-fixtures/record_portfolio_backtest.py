"""Records OpenAlgo's whole portfolio backtest (services.portfolio_service.run_portfolio_backtest) for portfolio_backtest.fixtures.json.

Only price loading and the listing-name lookup are replaced: prices are generated and aligned by OpenAlgo's own _assemble, so the warnings, the inner join and the session checks are the real ones.
Long curves are sampled at the same paths in the recording and in the .NET comparison.
"""

import json
import math
import os
import random
import sys
import tempfile
from datetime import date, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-pb-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import pandas as pd  # noqa: E402

import services.portfolio_service as ps  # noqa: E402
from portfolio.data import MissingHistory, _assemble  # noqa: E402

OUT = Path(__file__).with_name("portfolio_backtest.fixtures.json")
HOLIDAYS = {date(2020, 3, 25), date(2021, 1, 26), date(2022, 8, 15), date(2019, 11, 12)}


def sessions(first, last):
    d, out = first, []
    while d <= last:
        if d.weekday() < 5 and d not in HOLIDAYS:
            out.append(d)
        d += timedelta(days=1)
    return out


def walk(days, start, seed, drift, vol, crash=None, split_at=None):
    rng, p, out = random.Random(seed), start, []
    for i, d in enumerate(days):
        shock = rng.gauss(drift, vol)
        if crash and crash[0] <= d <= crash[1]:
            shock -= 0.018
        p = max(1.0, p * (1 + shock))
        if split_at is not None and i == split_at:
            p = p / 2
        out.append(round(p, 2))
    return out


DAYS = sessions(date(2019, 1, 1), date(2022, 12, 30))
CRASH = (date(2020, 2, 21), date(2020, 3, 23))
UNIVERSE = {
    "NIFTYBEES": (DAYS, walk(DAYS, 110, 1, 0.0005, 0.010, CRASH)),
    "RELIANCE": (DAYS, walk(DAYS, 1300, 2, 0.0006, 0.016, CRASH)),
    "INFY": (DAYS, walk(DAYS, 700, 3, 0.0007, 0.015, CRASH)),
    "GOLDBEES": (DAYS, walk(DAYS, 28, 4, 0.0003, 0.007)),
    "SPLITCO": (DAYS, walk(DAYS, 500, 5, 0.0004, 0.014, None, 400)),
    "LATECO": (DAYS[300:], walk(DAYS[300:], 90, 6, 0.0008, 0.02)),
    "NIFTY": (DAYS, walk(DAYS, 11000, 7, 0.0004, 0.009, CRASH)),
    "NIFTYLATE": (DAYS[200:], walk(DAYS[200:], 12000, 8, 0.0004, 0.009)),
}


def derived(base, add=0.0, seed=None, mix=None):
    """A series whose daily returns are another's plus a constant, or a blend of two series' returns."""
    days, closes = base
    out, prev = [], None
    for i, c in enumerate(closes):
        if prev is None:
            out.append(c)
        else:
            r = c / prev - 1
            if mix is not None:
                other = mix[1][i] / mix[1][i - 1] - 1
                r = 0.7 * r + 0.3 * other
            out.append(round(max(1.0, out[-1] * (1 + r + add)), 2))
        prev = c
    return days, out


_rng = random.Random(99)
_gap_days = set(_rng.sample(range(5, len(DAYS) - 5), 25))
UNIVERSE["INFYLAG"] = derived(UNIVERSE["INFY"], add=0.00006)
UNIVERSE["CORRA"] = derived(UNIVERSE["RELIANCE"], mix=UNIVERSE["INFY"])
UNIVERSE["CORRB"] = derived(UNIVERSE["INFY"], mix=UNIVERSE["RELIANCE"])
UNIVERSE["GOLDCO"] = (DAYS, walk(DAYS, 60, 21, 0.0003, 0.012))
UNIVERSE["NIFTYGAP"] = ([d for i, d in enumerate(UNIVERSE["NIFTY"][0]) if i not in _gap_days], [c for i, c in enumerate(UNIVERSE["NIFTY"][1]) if i not in _gap_days])
NAMES = {"NIFTYBEES": "Nippon India ETF Nifty BeES", "RELIANCE": "Reliance Industries", "INFY": "Infosys", "GOLDBEES": "Gold BeES", "SPLITCO": "Split Co", "LATECO": "Late Co", "GOLDCO": "Gold Corp", "CORRA": "Corr A", "CORRB": "Corr B"}


def fake_load_prices(symbols, exchanges, start_date, end_date, **kw):
    start, end = date.fromisoformat(start_date), date.fromisoformat(end_date)
    series = {}
    for symbol in symbols:
        if symbol not in UNIVERSE:
            raise MissingHistory(f"{symbol}: no D history in Historify for {start_date}..{end_date}. Ingest it, or run with the broker API as the source.")
        days, closes = UNIVERSE[symbol]
        idx = [(d, c) for d, c in zip(days, closes, strict=True) if start <= d <= end]
        if not idx:
            raise MissingHistory(f"Historify holds no D bars for {symbol} between {start_date} and {end_date}")
        series[symbol] = pd.Series([c for _, c in idx], index=pd.DatetimeIndex([pd.Timestamp(d) for d, _ in idx]), name=symbol)
    return _assemble(series, symbols, start_date, end_date, kw.get("source", "db"), 2)


ps.load_prices = fake_load_prices
ps._symbol_names = lambda symbols: {s: NAMES[s] for s in symbols if s in NAMES}


def h(*rows):
    return [{"symbol": s, "exchange": "NSE", "weight": w} for s, w in rows]


FULL = dict(start_date="2019-01-01", end_date="2022-12-30")
CASES = [
    {"name": "three_holdings_benchmark", "args": dict(holdings=h(("NIFTYBEES", 50), ("RELIANCE", 30), ("INFY", 20)), benchmark="NIFTY", **FULL)},
    {"name": "monthly_band_brokerage_gst_rf", "args": dict(holdings=h(("NIFTYBEES", 40), ("RELIANCE", 25), ("INFY", 20), ("GOLDBEES", 15)), benchmark="nifty", rebalance="monthly", drift_band=0.04, brokerage_pct=0.0003, gst_rate=0.18,
                                                      charge_overrides={"stt": {"rate": 0.002}}, risk_free_rate=0.05, mc_simulations=300, **FULL)},
    {"name": "flat_bps_quarterly_no_benchmark", "args": dict(holdings=h(("RELIANCE", 1), ("INFY", 1), ("GOLDBEES", 2)), rebalance="quarterly", cost_model="flat_bps", cost_bps=15, slippage=0.001, initial_capital=500000.0, mc_simulations=200, **FULL)},
    {"name": "single_holding", "args": dict(holdings=h(("NIFTYBEES", 100)), benchmark="NIFTY", mc_simulations=100, **FULL)},
    {"name": "short_window_notes", "args": dict(holdings=h(("NIFTYBEES", 60), ("INFY", 40)), benchmark="NIFTY", start_date="2022-03-01", end_date="2022-07-29", mc_simulations=100)},
    {"name": "late_listing_and_late_benchmark", "args": dict(holdings=h(("NIFTYBEES", 50), ("LATECO", 50)), benchmark="NIFTYLATE", rebalance="yearly", mc_simulations=100, **FULL)},
    {"name": "unadjusted_split_warns", "args": dict(holdings=h(("SPLITCO", 50), ("INFY", 50)), benchmark="NIFTY", rebalance="yearly", mc_simulations=100, **FULL)},
    {"name": "benchmark_missing_degrades", "args": dict(holdings=h(("NIFTYBEES", 50), ("INFY", 50)), benchmark="NOSUCHINDEX", mc_simulations=100, **FULL)},
    {"name": "clusters_and_gold_named_holding", "args": dict(holdings=h(("CORRA", 30), ("CORRB", 30), ("GOLDCO", 20), ("NIFTYBEES", 20)), benchmark="NIFTY", mc_simulations=100, **FULL)},
    {"name": "small_lag_behind_benchmark", "args": dict(holdings=h(("INFY", 100)), benchmark="INFYLAG", mc_simulations=100, **FULL)},
    {"name": "benchmark_with_missing_days", "args": dict(holdings=h(("NIFTYBEES", 50), ("INFY", 50)), benchmark="NIFTYGAP", mc_simulations=100, **FULL)},
    {"name": "empty_holdings", "args": dict(holdings=[], **FULL)},
    {"name": "duplicate_symbol", "args": dict(holdings=h(("INFY", 1), ("INFY", 2)), **FULL)},
    {"name": "missing_history", "args": dict(holdings=h(("INFY", 1), ("NOSUCH", 2)), **FULL)},
    {"name": "bad_rebalance_rule", "args": dict(holdings=h(("INFY", 1), ("RELIANCE", 2)), rebalance="weekly", **FULL)},
    {"name": "zero_weights", "args": dict(holdings=h(("INFY", 0), ("RELIANCE", 0)), **FULL)},
    {"name": "window_with_one_session_in_common", "args": dict(holdings=h(("INFY", 1), ("LATECO", 1)), start_date="2019-01-01", end_date="2020-02-27")},
]

SAMPLED = ["equity", "benchmark_equity", "series.drawdown", "series.weekly_returns", "series.rolling.sharpe", "series.rolling.volatility", "allocation.dates"]


def sample(rows, step=25):
    if not isinstance(rows, list) or len(rows) <= step * 2:
        return rows
    keep = [rows[i] for i in range(0, len(rows), step)]
    if keep[-1] is not rows[-1]:
        keep.append(rows[-1])
    return keep


def thin(payload):
    out = json.loads(json.dumps(payload))
    for path in SAMPLED:
        node, parts = out, path.split(".")
        for part in parts[:-1]:
            node = node.get(part) if isinstance(node, dict) else None
            if node is None:
                break
        if isinstance(node, dict) and parts[-1] in node:
            node[parts[-1]] = sample(node[parts[-1]])
    if "allocation" in out and out["allocation"].get("series"):
        out["allocation"]["series"] = {k: sample(v) for k, v in out["allocation"]["series"].items()}
    if "rebalancing_sweep" in out:
        out["rebalancing_sweep"]["curves"] = {k: sample(v) for k, v in out["rebalancing_sweep"]["curves"].items()}
    return out


def nan_free(x):
    if isinstance(x, float) and not math.isfinite(x):
        return None
    if isinstance(x, dict):
        return {k: nan_free(v) for k, v in x.items()}
    if isinstance(x, list):
        return [nan_free(v) for v in x]
    return x


out = []
for case in CASES:
    args = dict(case["args"])
    holdings = args.pop("holdings")
    ok, payload, status = ps.run_portfolio_backtest(holdings, args.pop("start_date"), args.pop("end_date"), **args)
    out.append({"name": case["name"], "request": {**case["args"]}, "ok": ok, "status": status, "payload": nan_free(thin(payload))})
    print(status, case["name"], len(json.dumps(out[-1]["payload"])))

series = {sym: {"dates": [d.isoformat() for d in days], "closes": closes} for sym, (days, closes) in UNIVERSE.items()}
OUT.write_text(json.dumps({"universe": series, "names": NAMES, "cases": out}) + "\n", encoding="utf-8")
print(f"wrote {len(out)} cases to {OUT}, {OUT.stat().st_size // 1024} KB")
