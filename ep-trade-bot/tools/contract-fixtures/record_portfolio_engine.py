"""Records OpenAlgo's portfolio engine (portfolio.engine.run_backtest) for portfolio_engine.fixtures.json.

Prices are generated; what is compared is the drift and reset simulation, the costs and the per-holding items.
"""

import json
import os
import random
import sys
import tempfile
from datetime import date, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-pe-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import pandas as pd  # noqa: E402

from portfolio.costs import schedule_for  # noqa: E402
from portfolio.data import PriceMatrix  # noqa: E402
from portfolio.engine import Costs, run_backtest  # noqa: E402
from portfolio.rebalance import RebalancePolicy  # noqa: E402

OUT = Path(__file__).with_name("portfolio_engine.fixtures.json")


def sessions(first, last):
    d, out = first, []
    while d <= last:
        if d.weekday() < 5:
            out.append(d)
        d += timedelta(days=1)
    return out


def walk(days, start, seed, drift, vol):
    rng, p, out = random.Random(seed), start, []
    for _ in days:
        p = max(1.0, p * (1 + rng.gauss(drift, vol)))
        out.append(round(p, 2))
    return out


def matrix(first, last, specs):
    days = sessions(first, last)
    frame = pd.DataFrame({sym: walk(days, *spec) for sym, spec in specs.items()}, index=pd.DatetimeIndex([pd.Timestamp(d) for d in days]))
    return days, frame


DAYS, FRAME = matrix(date(2023, 1, 2), date(2024, 12, 31), {"AAA": (100, 11, 0.0006, 0.012), "BBB": (250, 12, 0.0002, 0.018), "CCC": (40, 13, 0.0009, 0.025)})
SYMS = list(FRAME.columns)

SCENARIOS = [
    {"name": "buy_and_hold", "weights": {"AAA": 50, "BBB": 30, "CCC": 20}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "monthly_flat", "weights": {"AAA": 0.5, "BBB": 0.3, "CCC": 0.2}, "rule": "monthly", "drift": 0.0, "costs": {"kind": "flat", "bps": 10, "slippage": 0.0005}},
    {"name": "quarterly_schedule", "weights": {"AAA": 40, "BBB": 40, "CCC": 20}, "rule": "quarterly", "drift": 0.0, "costs": {"kind": "schedule", "name": "india_delivery_nse", "overrides": {}, "slippage": 0.0005}},
    {"name": "yearly_schedule_bse", "weights": {"AAA": 1, "BBB": 1, "CCC": 1}, "rule": "yearly", "drift": 0.0, "costs": {"kind": "schedule", "name": "india_delivery_bse", "overrides": {"brokerage": {"flat": 0.0, "rate": 0.0003, "cap": 20.0}}, "slippage": 0.0}},
    {"name": "drift_band", "weights": {"AAA": 34, "BBB": 33, "CCC": 33}, "rule": "never", "drift": 0.05, "costs": {"kind": "schedule", "name": "india_delivery_nse", "overrides": {}, "slippage": 0.001}},
    {"name": "monthly_and_band", "weights": {"AAA": 60, "BBB": 20, "CCC": 20}, "rule": "monthly", "drift": 0.03, "costs": {"kind": "flat", "bps": 5, "slippage": 0.0}, "capital": 2500000},
    {"name": "one_zero_weight", "weights": {"AAA": 100, "BBB": 0, "CCC": 0}, "rule": "monthly", "drift": 0.0, "costs": {"kind": "flat", "bps": 5, "slippage": 0.0}},
    {"name": "idle_holding_not_ordered", "weights": {"AAA": 50, "BBB": 50, "CCC": 0}, "rule": "monthly", "drift": 0.0, "costs": {"kind": "schedule", "name": "india_delivery_nse", "overrides": {"brokerage": {"flat": 20.0, "rate": 0.0, "cap": 20.0}}, "slippage": 0.0}},
    {"name": "bad_rule", "weights": {"AAA": 1, "BBB": 1, "CCC": 1}, "rule": "weekly", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "bad_band", "weights": {"AAA": 1, "BBB": 1, "CCC": 1}, "rule": "never", "drift": 1.5, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "missing_weight", "weights": {"AAA": 1, "BBB": 1}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "extra_weight", "weights": {"AAA": 1, "BBB": 1, "CCC": 1, "ZZZ": 2}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "negative_weight", "weights": {"AAA": 1, "BBB": -1, "CCC": 1}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "zero_weights", "weights": {"AAA": 0, "BBB": 0, "CCC": 0}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}},
    {"name": "bad_capital", "weights": {"AAA": 1, "BBB": 1, "CCC": 1}, "rule": "never", "drift": 0.0, "costs": {"kind": "flat", "bps": 0, "slippage": 0}, "capital": 0},
]


def build_costs(spec):
    if spec["kind"] == "flat":
        return Costs(bps=spec["bps"], slippage=spec["slippage"])
    import dataclasses

    schedule = schedule_for(spec["name"], spec["overrides"] or None)
    return dataclasses.replace(schedule, slippage=spec["slippage"])


def sample(values, step=25):
    idx = list(range(0, len(values), step))
    if idx[-1] != len(values) - 1:
        idx.append(len(values) - 1)
    return [[i, values[i]] for i in idx]


out = []
for sc in SCENARIOS:
    entry = {"name": sc["name"], "weights": sc["weights"], "rule": sc["rule"], "drift_band": sc["drift"], "costs": sc["costs"], "capital": sc.get("capital", 100000.0)}
    try:
        prices = PriceMatrix(closes=FRAME, source="db", start=DAYS[0], end=DAYS[-1])
        res = run_backtest(prices, sc["weights"], policy=RebalancePolicy(rule=sc["rule"], drift_band=sc["drift"]), costs=build_costs(sc["costs"]), initial_capital=entry["capital"])
        entry["ok"] = True
        entry["equity"] = sample([float(v) for v in res.equity.tolist()])
        entry["weights_path"] = sample([[float(x) for x in row] for row in res.weights.to_numpy().tolist()])
        entry["rebalance_dates"] = [d.date().isoformat() for d in res.rebalance_dates]
        entry["turnover"] = [float(v) for v in res.turnover.tolist()]
        entry["cost_drag"] = float(res.cost_drag)
        entry["cost_breakdown"] = {k: float(v) for k, v in res.cost_breakdown.items()}
        entry["orders"] = res.meta["orders"]
        entry["items"] = [{"symbol": s, **{k: float(v) for k, v in row.items()}} for s, row in res.items.iterrows()]
    except Exception as exc:  # noqa: BLE001
        entry["ok"] = False
        entry["error"] = str(exc)
    out.append(entry)

OUT.write_text(json.dumps({"symbols": SYMS, "dates": [d.isoformat() for d in DAYS], "closes": FRAME.to_numpy().tolist(), "scenarios": out}) + "\n", encoding="utf-8")
print(f"wrote {len(out)} scenarios, {sum(1 for e in out if e['ok'])} ok, to {OUT}")
