"""Records OpenAlgo's SIP back tester (services.sip_service.run_sip_backtest) for sip.fixtures.json.

Price loading is replaced by generated histories so what is compared is the schedule, the whole-share simulation, the cost model and every metric. Long series (curves, drawdown) are sampled at
every 50th point plus the last in both the recording and the .NET comparison, which keeps the fixture small while still pinning the whole shape.
"""

import json
import os
import random
import sys
import tempfile
import types
from datetime import date, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-sip-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import pandas as pd  # noqa: E402

import services.sip_service as ss  # noqa: E402
from portfolio.data import DataError  # noqa: E402

OUT = Path(__file__).with_name("sip.fixtures.json")
SAMPLE = 50


def sessions(first, last, holidays=()):
    d, out = first, []
    while d <= last:
        if d.weekday() < 5 and d not in holidays:
            out.append(d)
        d += timedelta(days=1)
    return out


def walk(days, start_price, seed, drift=0.0004, vol=0.012, crash=None):
    rng = random.Random(seed)
    price, prices = start_price, []
    for d in days:
        shock = rng.gauss(drift, vol)
        if crash and crash[0] <= d <= crash[1]:
            shock -= 0.006
        price = max(1.0, price * (1 + shock))
        prices.append(round(price, 2))
    return prices


def series(first, last, start_price, seed, holidays=(), **kw):
    days = sessions(first, last, holidays)
    return [d.isoformat() for d in days], walk(days, start_price, seed, **kw)


def sample(resp):
    """Keep every SAMPLE-th point plus the last of the long arrays."""
    def thin(rows):
        if not isinstance(rows, list) or len(rows) <= SAMPLE * 2:
            return rows
        keep = [r for i, r in enumerate(rows) if i % SAMPLE == 0]
        if keep[-1] is not rows[-1]:
            keep.append(rows[-1])
        return keep

    r = json.loads(json.dumps(resp))
    if "curves" in r:
        r["curves"] = {k: thin(v) for k, v in r["curves"].items()}
    if isinstance(r.get("drawdown"), dict) and "curve" in r["drawdown"]:
        r["drawdown"]["curve"] = thin(r["drawdown"]["curve"])
    if isinstance(r.get("benchmark"), dict) and "curve" in r["benchmark"]:
        r["benchmark"]["curve"] = thin(r["benchmark"]["curve"])
    for row in (r.get("rolling_xirr") or []):
        row["points"] = thin(row["points"])
    return r


cases = []


def run(name, symbol="INFY", dates=None, closes=None, warnings=None, load_error=None, bench=None, bench_error=False, **kw):
    kw.setdefault("start_date", "2020-01-01")
    kw.setdefault("end_date", "2022-12-30")
    kw.setdefault("amount", 10000)
    loads = []

    def load_prices(symbols, exchanges, start, end, source="db", allowed_exchanges=None, **creds):
        sym = symbols[0]
        loads.append({"symbol": sym, "exchange": exchanges[0], "start": start, "end": end, "source": source, "benchmark": allowed_exchanges is not None})
        if allowed_exchanges is not None:
            if bench_error:
                raise DataError("no history for benchmark")
            d, c = bench
        else:
            if load_error is not None:
                raise load_error
            d, c = dates, closes
        frame = pd.DataFrame({sym: c}, index=pd.DatetimeIndex(pd.to_datetime(d)))
        return types.SimpleNamespace(closes=frame, source="historify", warnings={symbol: list(warnings or [])}, start=None, end=None)

    ss.load_prices = load_prices
    exchange = kw.pop("exchange", "NSE")
    ok, resp, code = ss.run_sip_backtest(symbol, exchange, kw.pop("start_date"), kw.pop("end_date"), kw.pop("amount"), **kw)
    cases.append({"name": name, "symbol": symbol, "exchange": exchange, "args": {k: v for k, v in kw.items()}, "start_date": None, "dates": dates, "closes": closes, "warnings": warnings or [], "bench": bench,
                  "bench_error": bench_error, "load_error": None if load_error is None else [type(load_error).__name__, str(load_error)], "loads": loads, "ok": ok, "code": code, "response": sample(resp)})
    return cases[-1]


def run2(name, start_date, end_date, amount, **kw):
    """Same as run but records the request's date and amount fields verbatim (validation cases)."""
    c = run(name, start_date=start_date, end_date=end_date, amount=amount, **kw)
    c["start_date"] = start_date
    c["end_date"] = end_date
    c["amount"] = amount
    return c


d_long, c_long = series(date(2006, 6, 1), date(2026, 11, 10), 400.0, 1, crash=(date(2008, 1, 8), date(2009, 3, 9)))
d_mid, c_mid = series(date(2015, 1, 1), date(2026, 11, 10), 600.0, 2)
d_short, c_short = series(date(2024, 1, 1), date(2025, 12, 31), 1500.0, 3, holidays={date(2024, 3, 1), date(2024, 7, 1), date(2025, 1, 1)})
bench_mid = series(date(2015, 1, 1), date(2026, 11, 10), 8000.0, 4)
bench_short = series(date(2024, 1, 1), date(2025, 12, 31), 20000.0, 5)

d_170, c_170 = series(date(2012, 9, 3), date(2026, 11, 10), 900.0, 7)
d_rise = sessions(date(2022, 1, 3), date(2023, 12, 29))
c_rise = [round(100 + i * 0.5, 2) for i in range(len(d_rise))]
d_covid, c_covid = series(date(2018, 1, 1), date(2021, 4, 20), 700.0, 8, crash=(date(2020, 2, 20), date(2020, 3, 23)))
d_rise = [d.isoformat() for d in d_rise]

full = lambda name, **kw: run2(name, kw.pop("start_date", "2012-01-02"), kw.pop("end_date", "2026-11-10"), kw.pop("amount", 10000), **kw)

full("long-default-with-benchmark", dates=d_long, closes=c_long, start_date="2007-01-02", end_date="2026-11-10", benchmark="NIFTY", benchmark_exchange="NSE_INDEX",
     **{"bench": bench_mid if False else series(date(2006, 6, 1), date(2026, 11, 10), 5000.0, 6, crash=(date(2008, 1, 8), date(2009, 3, 9)))})
full("mid-step-up-and-brokerage", dates=d_mid, closes=c_mid, start_date="2016-03-01", end_date="2026-11-10", step_up_percent=10.0, brokerage_percent=0.1, brokerage_flat=5.0, amount=25000)
full("quarterly-day-15", dates=d_mid, closes=c_mid, start_date="2016-01-01", end_date="2026-06-30", frequency="quarterly", day_of_month=15, amount=50000)
full("weekly", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", frequency="weekly", amount=2500)
full("fortnightly-holidays", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", frequency="fortnightly", amount=5000)
full("bse-cost-schedule", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", exchange="BSE", cost_exchange="BSE", gst_rate=0.12, slippage=0.0005, amount=15000)
full("charge-overrides", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", charge_overrides={"stt": {"rate": 0.002}, "brokerage": {"flat": 15.0, "cap": 50.0}, "nope": {"rate": 1}}, amount=20000)
full("warnings-pass-through", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", warnings=["INFY: 2024-06-03 moved -48%, possible unadjusted split"], amount=8000)
full("benchmark-fails", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", benchmark="NIFTY", bench_error=True, amount=8000)
full("benchmark-short", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", benchmark="SENSEX", benchmark_exchange="bse_index", bench=bench_short, amount=8000)
full("no-grids", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", include_grids=False, amount=9000)
full("amount-too-small-to-buy", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", amount=100)
full("step-up-whole-share-cash-carry", dates=d_short, closes=c_short, start_date="2024-01-15", end_date="2025-12-31", amount=1700, step_up_percent=25.0)
full("window-with-no-sessions", dates=d_short, closes=c_short, start_date="2030-01-01", end_date="2030-12-31")
full("window-starts-mid-history", dates=d_short, closes=c_short, start_date="2024-06-14", end_date="2024-12-20", amount=7500)
full("price-error", load_error=DataError("INFY has no usable history between 2012-01-02 and 2026-11-10"))
full("price-generic-error", load_error=RuntimeError("historify offline"))
full("unknown-cost-exchange", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", cost_exchange="XYZ")
full("negative-slippage", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", slippage=-0.1)
full("negative-charge-override", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", charge_overrides={"stt": {"rate": -1}})
full("negative-step-up", dates=d_short, closes=c_short, start_date="2024-01-01", end_date="2025-12-31", step_up_percent=-5.0)
full("non-positive-prices", dates=["2024-01-01", "2024-01-02", "2024-02-01"], closes=[100.0, 0.0, 101.0], start_date="2024-01-01", end_date="2024-03-01")

full("heatmap-cell-cap-boundary", dates=d_170, closes=c_170, start_date="2012-09-03", end_date="2026-11-10", amount=12000)
full("rising-prices-never-draw-down", dates=d_rise, closes=c_rise, start_date="2022-01-03", end_date="2023-12-29", amount=5000)
full("crisis-with-short-runway", dates=d_covid, closes=c_covid, start_date="2018-02-01", end_date="2021-04-20", amount=10000)

# Validation, in the order the service checks them
v = dict(dates=d_short, closes=c_short)
run2("bad-start-date", "01/01/2024", "2025-12-31", 1000, **v)
run2("bad-end-date", "2024-01-01", "soon", 1000, **v)
run2("non-string-start-date", None, "2025-12-31", 1000, **v)
run2("quote-in-bad-date", "it's", "2025-12-31", 1000, **v)
run2("end-not-after-start", "2024-01-01", "2024-01-01", 1000, **v)
run2("window-too-long", "1990-01-01", "2024-01-01", 1000, **v)
run2("amount-zero", "2024-01-01", "2025-12-31", 0, **v)
run2("amount-negative", "2024-01-01", "2025-12-31", -5, **v)
run2("amount-text", "2024-01-01", "2025-12-31", "lots", **v)
run2("amount-null", "2024-01-01", "2025-12-31", None, **v)
run2("amount-infinite", "2024-01-01", "2025-12-31", "inf", **v)
run2("amount-numeric-text", "2024-01-01", "2025-12-31", " 12000 ", **v)
run2("bad-frequency", "2024-01-01", "2025-12-31", 1000, frequency="daily", **v)
run2("day-29", "2024-01-01", "2025-12-31", 1000, day_of_month=29, **v)
run2("day-0", "2024-01-01", "2025-12-31", 1000, frequency="quarterly", day_of_month=0, **v)
run2("day-is-decimal", "2024-01-01", "2025-12-31", 1000, day_of_month=5.5, **v)
run2("day-is-bool", "2024-01-01", "2025-12-31", 1000, day_of_month=True, **v)
run2("day-ignored-for-weekly", "2024-01-01", "2025-12-31", 1000, frequency="weekly", day_of_month=99, **v)
run2("bad-exchange", "2024-01-01", "2025-12-31", 1000, exchange="NFO", **v)
run2("blank-exchange", "2024-01-01", "2025-12-31", 1000, exchange="", **v)
run2("bad-benchmark-exchange", "2024-01-01", "2025-12-31", 1000, benchmark="X", benchmark_exchange="NFO", **v)

OUT.write_text(json.dumps({"cases": cases}, indent=0, default=str))
print(len(cases), "cases;", sum(1 for c in cases if c["ok"]), "ok;", OUT.stat().st_size // 1024, "KB")
