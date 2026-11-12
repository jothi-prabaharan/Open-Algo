"""Records OpenAlgo's portfolio statistics (portfolio.analytics.summary and services.portfolio_service._series_analytics) for portfolio_stats.fixtures.json.

Both run on generated daily returns, each answer passed through the service's own _clean.
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
TMP = Path(tempfile.mkdtemp(prefix="oa-ps-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from portfolio.analytics import summary  # noqa: E402
from services.portfolio_service import _clean, _series_analytics  # noqa: E402

OUT = Path(__file__).with_name("portfolio_stats.fixtures.json")


def sessions(first, count, skip=()):
    d, out = first, []
    while len(out) < count:
        if d.weekday() < 5 and d not in skip:
            out.append(d)
        d += timedelta(days=1)
    return out


def rets(n, seed, drift, vol, crash=None):
    rng = random.Random(seed)
    out = []
    for i in range(n):
        r = rng.gauss(drift, vol)
        if crash and crash[0] <= i < crash[1]:
            r -= 0.01
        out.append(round(r, 6))
    return out


HOLIDAYS = {date(2023, 1, 26), date(2023, 3, 7), date(2023, 8, 15), date(2024, 1, 26), date(2024, 3, 25)}
LONG = sessions(date(2022, 6, 1), 700, HOLIDAYS)
SHORT = sessions(date(2024, 2, 1), 120)
GAPPED = sessions(date(2023, 1, 2), 90) + sessions(date(2023, 7, 3), 160)

CASES = [
    {"name": "long_with_benchmark", "dates": LONG, "r": rets(700, 1, 0.0005, 0.011, (150, 200)), "b": rets(700, 2, 0.0004, 0.009, (150, 200)), "rf": 0.0},
    {"name": "long_with_rf", "dates": LONG, "r": rets(700, 3, 0.0006, 0.012), "b": rets(700, 4, 0.0003, 0.010), "rf": 0.065},
    {"name": "long_no_benchmark", "dates": LONG, "r": rets(700, 5, 0.0004, 0.013), "b": None, "rf": 0.0},
    {"name": "short_with_benchmark", "dates": SHORT, "r": rets(120, 6, 0.0008, 0.015), "b": rets(120, 7, 0.0005, 0.011), "rf": 0.0},
    {"name": "gap_in_history_leaves_empty_periods", "dates": GAPPED, "r": rets(250, 14, 0.0005, 0.012), "b": rets(250, 15, 0.0004, 0.009), "rf": 0.0},
    {"name": "starts_in_drawdown_never_recovers", "dates": SHORT, "r": [-0.02, -0.01] + rets(118, 8, -0.001, 0.01), "b": rets(120, 9, 0.0002, 0.008), "rf": 0.0},
    {"name": "mostly_losing_with_no_negative_months_missing", "dates": LONG, "r": rets(700, 10, -0.0004, 0.01), "b": rets(700, 11, 0.0004, 0.01), "rf": 0.0},
    {"name": "benchmark_never_falls_in_a_year", "dates": LONG, "r": rets(700, 12, 0.0005, 0.011), "b": [abs(x) for x in rets(700, 13, 0.0003, 0.004)], "rf": 0.0},
]


def sample(rows, step=20):
    if not isinstance(rows, list) or len(rows) <= step * 2:
        return rows
    keep = [rows[i] for i in range(0, len(rows), step)]
    if keep[-1] is not rows[-1]:
        keep.append(rows[-1])
    return keep


def thin(series):
    out = dict(series)
    for key in ("drawdown", "weekly_returns"):
        if key in out:
            out[key] = sample(out[key])
    if "rolling" in out:
        out["rolling"] = {**out["rolling"], "sharpe": sample(out["rolling"]["sharpe"]), "volatility": sample(out["rolling"]["volatility"])}
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
    idx = pd.DatetimeIndex([pd.Timestamp(d) for d in case["dates"]])
    r = pd.Series(case["r"], index=idx)
    b = pd.Series(case["b"], index=idx) if case["b"] is not None else None
    metrics = _clean(summary(r, b, rf=case["rf"]))
    series = _series_analytics(r, case["rf"], b)
    out.append({
        "name": case["name"], "rf": case["rf"], "dates": [d.isoformat() for d in case["dates"]], "returns": case["r"], "benchmark": case["b"],
        "metrics": nan_free(metrics), "series": nan_free(thin(series)),
    })

OUT.write_text(json.dumps(out) + "\n", encoding="utf-8")
print(f"wrote {len(out)} cases to {OUT}")
