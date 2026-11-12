"""Records the numeric core of OpenAlgo's /tools back ends from its own services, for tools.fixtures.json.

Each service is run with its fetchers replaced by the recorded input and with a frozen clock, so what is compared is the calculation and the shape it returns, never a broker. The .NET tools replay the
same inputs and must give the same numbers. Add a tool by adding a recorder function and registering it in RECORDERS.
"""

import json
import os
import sys
import tempfile
from datetime import datetime as _real_datetime
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-tools-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

IST = ZoneInfo("Asia/Kolkata")
NOW = _real_datetime(2026, 11, 11, 10, 0, 0, tzinfo=IST)


class Frozen(_real_datetime):
    @classmethod
    def now(cls, tz=None):
        return NOW if tz is None else NOW.astimezone(tz)


def record_greeks():
    import services.option_greeks_service as g

    g.datetime = Frozen
    cases = []
    scenarios = [
        # symbol, exchange, spot, option price, rate, expiry_time
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 310.0, None, None),
        ("NIFTY26NOV2624000PE", "NFO", 24050.0, 250.5, None, None),
        ("NIFTY26NOV2624500CE", "NFO", 24050.0, 95.25, 6.5, None),
        ("NIFTY26NOV2623000PE", "NFO", 24050.0, 12.0, 6.5, None),
        ("BANKNIFTY26NOV2652000CE", "NFO", 52100.0, 640.0, 7.0, None),
        ("NIFTY26NOV2620000CE", "NFO", 24050.0, 4050.0, None, None),
        ("NIFTY26NOV2620000CE", "NFO", 24050.0, 4049.0, None, None),
        ("NIFTY26NOV2620000CE", "NFO", 24050.0, 4050.004, None, None),
        ("NIFTY26NOV2628000PE", "NFO", 24050.0, 3950.0, None, None),
        ("SENSEX26NOV2680000CE", "BFO", 80200.0, 450.0, None, None),
        ("USDINR26NOV2683.50CE", "CDS", 83.9, 0.62, None, None),
        ("GOLD26NOV2672000CE", "MCX", 72500.0, 1450.0, None, None),
        ("GOLD26NOV2672000CE", "MCX", 72500.0, 1450.0, None, "19:00"),
        ("M&M26NOV261500CE", "NFO", 1490.0, 45.0, None, None),
        ("NIFTY12NOV2624000CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY11NOV2624000CE", "NFO", 24050.0, 100.0, None, "15:30"),
        ("NIFTY11NOV2624000CE", "NFO", 24050.0, 100.0, None, "09:30"),
        ("NIFTY10NOV2624000CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 0.0, 100.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 0.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, -5.0, None, None),
        ("NIFTY26NOV260CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY26XYZ2624000CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY2624000CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 100.0, None, "25:00"),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 100.0, None, "1530"),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 100.0, None, "ab:cd"),
        ("nifty26nov2624000ce", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 2.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 23000.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 24100.0, None, None),
        ("NIFTY26NOV261.2.3CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY31FEB2724000CE", "NFO", 24050.0, 100.0, None, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 100.0, 0.0, None),
        ("NIFTY26NOV2624000CE", "NFO", 24050.0, 100.0, 7.123, None),
        ("RELIANCE26NOV261500PE", "NFO", 1490.0, 52.75, None, "-1:30"),
    ]
    for symbol, exchange, spot, price, rate, expiry_time in scenarios:
        ok, resp, code = g.calculate_greeks(symbol, exchange, spot, price, rate, expiry_time)
        cases.append({"symbol": symbol, "exchange": exchange, "spot": spot, "price": price, "rate": rate, "expiry_time": expiry_time, "ok": ok, "code": code, "response": resp})
    return {"now": NOW.isoformat(), "cases": cases}


def make_chain(underlying, spot, atm, step, count, expiry, lot, seed, exchange="NFO"):
    """A deterministic option chain in the shape get_option_chain returns, with the awkward rows a real one has (a missing leg, no open interest, no price)."""
    import random

    rng = random.Random(seed)
    chain = []
    for i in range(-count, count + 1):
        strike = atm + i * step
        row = {"strike": strike}
        for leg in ("ce", "pe"):
            intrinsic = max(spot - strike, 0) if leg == "ce" else max(strike - spot, 0)
            ltp = round(intrinsic + max(2.0, rng.uniform(5, 200) * (1 - abs(i) / (count + 2))), 2)
            row[leg] = {"symbol": f"{underlying}{expiry}{strike}{leg.upper()}", "ltp": ltp, "oi": rng.randint(0, 900000), "volume": rng.randint(0, 50000), "lotsize": lot,
                        "label": "ATM" if i == 0 else "ITM" if (i < 0) == (leg == "ce") else "OTM", "bid": ltp - 0.05, "ask": ltp + 0.05, "tag": leg}
        chain.append(row)
    # Awkward rows
    chain[1]["ce"]["oi"] = 0
    chain[2]["pe"]["ltp"] = 0
    chain[3]["ce"] = None
    chain[4]["pe"]["symbol"] = None
    chain[5]["ce"]["lotsize"] = None
    return {"status": "success", "underlying": underlying, "underlying_ltp": spot, "atm_strike": atm, "expiry_date": expiry, "chain": chain}


def record_gex():
    import services.gex_service as gx
    import services.option_greeks_service as g

    g.datetime = Frozen
    cases = []
    for name, underlying, spot, atm, step, count, expiry, lot, futures in [
        ("nifty", "NIFTY", 24050.0, 24050, 50, 8, "26NOV26", 75, 24110.5),
        ("banknifty", "BANKNIFTY", 52110.0, 52100, 100, 6, "26NOV26", 30, None),
    ]:
        chain = make_chain(underlying, spot, atm, step, count, expiry, lot, seed=len(name))
        gx.get_option_chain = lambda **kw: (True, chain, 200)
        gx._get_nearest_futures_price = lambda **kw: futures
        ok, resp, code = gx.get_gex_data(underlying, "NFO", expiry, "key")
        cases.append({"name": name, "exchange": "NFO", "expiry": expiry, "chain": chain, "futures": futures, "ok": ok, "code": code, "response": resp})
    # no price, a failed chain
    bad = make_chain("NIFTY", 0, 24050, 50, 3, "26NOV26", 75, 1)
    gx.get_option_chain = lambda **kw: (True, bad, 200)
    gx._get_nearest_futures_price = lambda **kw: None
    ok, resp, code = gx.get_gex_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "no-spot", "exchange": "NFO", "expiry": "26NOV26", "chain": bad, "futures": None, "ok": ok, "code": code, "response": resp})
    gx.get_option_chain = lambda **kw: (False, {"status": "error", "message": "Option chain unavailable"}, 503)
    ok, resp, code = gx.get_gex_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "chain-failed", "exchange": "NFO", "expiry": "26NOV26", "chain": None, "futures": None, "ok": ok, "code": code, "response": resp})
    return {"now": NOW.isoformat(), "cases": cases}


def record_iv_smile():
    import services.iv_smile_service as sm
    import services.option_greeks_service as g

    g.datetime = Frozen
    cases = []
    base = [("nifty", "NIFTY", 24050.0, 24050, 50, 12, "26NOV26", 75, 1), ("banknifty", "BANKNIFTY", 52110.0, 52100, 100, 12, "26NOV26", 30, 2), ("sensex", "SENSEX", 80200.0, 80200, 100, 12, "26NOV26", 20, 3)]
    for name, underlying, spot, atm, step, count, expiry, lot, seed in base:
        chain = make_chain(underlying, spot, atm, step, count, expiry, lot, seed)
        sm.get_option_chain = lambda **kw: (True, chain, 200)
        ok, resp, code = sm.get_iv_smile_data(underlying, "NFO", expiry, "key")
        cases.append({"name": name, "exchange": "NFO", "expiry": expiry, "chain": chain, "ok": ok, "code": code, "response": resp})
    # An ATM strike with no legs at all, and one with only a put
    chain = make_chain("NIFTY", 24050.0, 24050, 50, 12, "26NOV26", 75, 5)
    chain["chain"][12]["ce"] = None
    chain["chain"][12]["pe"]["ltp"] = 0
    sm.get_option_chain = lambda **kw: (True, chain, 200)
    ok, resp, code = sm.get_iv_smile_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "atm-without-legs", "exchange": "NFO", "expiry": "26NOV26", "chain": chain, "ok": ok, "code": code, "response": resp})
    chain = make_chain("NIFTY", 24050.0, 24050, 50, 12, "26NOV26", 75, 6)
    chain["chain"][12]["ce"]["ltp"] = 0
    sm.get_option_chain = lambda **kw: (True, chain, 200)
    ok, resp, code = sm.get_iv_smile_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "atm-put-only", "exchange": "NFO", "expiry": "26NOV26", "chain": chain, "ok": ok, "code": code, "response": resp})
    chain = make_chain("NIFTY", 0, 24050, 50, 3, "26NOV26", 75, 7)
    sm.get_option_chain = lambda **kw: (True, chain, 200)
    ok, resp, code = sm.get_iv_smile_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "no-spot", "exchange": "NFO", "expiry": "26NOV26", "chain": chain, "ok": ok, "code": code, "response": resp})
    sm.get_option_chain = lambda **kw: (False, {"status": "error", "message": "Option chain unavailable"}, 503)
    ok, resp, code = sm.get_iv_smile_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "chain-failed", "exchange": "NFO", "expiry": "26NOV26", "chain": None, "ok": ok, "code": code, "response": resp})
    return {"now": NOW.isoformat(), "cases": cases}


def record_oi():
    import services.oi_tracker_service as oi

    cases = []
    specs = [("nifty", "NIFTY", 24050.0, 24050, 50, 23, "26NOV26", 75, 11, 24110.5), ("banknifty", "BANKNIFTY", 52110.0, 52100, 100, 10, "26NOV26", 30, 12, None),
             ("small", "FINNIFTY", 23000.0, 23000, 50, 3, "26NOV26", 65, 13, 23010.0)]
    for name, underlying, spot, atm, step, count, expiry, lot, seed, fut in specs:
        chain = make_chain(underlying, spot, atm, step, count, expiry, lot, seed)
        oi.get_option_chain = lambda **kw: (True, chain, 200)
        oi._get_nearest_futures_price = lambda **kw: fut
        row = {"name": name, "exchange": "NFO", "expiry": expiry, "underlying": underlying, "chain": chain, "futures": fut}
        ok, resp, code = oi.get_oi_data(underlying, "NFO", expiry, "key")
        row["oi"] = {"ok": ok, "code": code, "response": resp}
        ok, resp, code = oi.calculate_max_pain(underlying, "NFO", expiry, "key")
        row["pain"] = {"ok": ok, "code": code, "response": resp}
        cases.append(row)
    # No open interest anywhere, so no ratio and every pain is zero
    chain = make_chain("NIFTY", 24050.0, 24050, 50, 4, "26NOV26", 75, 14)
    for r in chain["chain"]:
        for leg in ("ce", "pe"):
            if r[leg]:
                r[leg]["oi"] = 0
                r[leg]["volume"] = 0
    # Strikes that are not numbers or not positive, and an empty chain
    for label, strikes in [("no-oi", None), ("bad-strikes", [0, -50, "x"]), ("empty", [])]:
        c = json.loads(json.dumps(chain))
        if strikes is not None:
            c["chain"] = [{"strike": s, "ce": {"oi": 100, "volume": 5, "lotsize": 50}, "pe": {"oi": 200, "volume": 6, "lotsize": 50}} for s in strikes]
        oi.get_option_chain = lambda **kw: (True, c, 200)
        oi._get_nearest_futures_price = lambda **kw: None
        row = {"name": label, "exchange": "NFO", "expiry": "26NOV26", "underlying": "NIFTY", "chain": c, "futures": None}
        ok, resp, code = oi.get_oi_data("NIFTY", "NFO", "26NOV26", "key")
        row["oi"] = {"ok": ok, "code": code, "response": resp}
        ok, resp, code = oi.calculate_max_pain("NIFTY", "NFO", "26NOV26", "key")
        row["pain"] = {"ok": ok, "code": code, "response": resp}
        cases.append(row)
    oi.get_option_chain = lambda **kw: (False, {"status": "error", "message": "Option chain unavailable"}, 503)
    row = {"name": "chain-failed", "exchange": "NFO", "expiry": "26NOV26", "underlying": "NIFTY", "chain": None, "futures": None}
    ok, resp, code = oi.get_oi_data("NIFTY", "NFO", "26NOV26", "key")
    row["oi"] = {"ok": ok, "code": code, "response": resp}
    ok, resp, code = oi.calculate_max_pain("NIFTY", "NFO", "26NOV26", "key")
    row["pain"] = {"ok": ok, "code": code, "response": resp}
    cases.append(row)
    return {"now": NOW.isoformat(), "cases": cases}


RECORDERS = {"greeks": record_greeks, "gex": record_gex, "iv_smile": record_iv_smile, "oi": record_oi}

if __name__ == "__main__":
    out = {name: fn() for name, fn in RECORDERS.items()}
    (Path(__file__).with_name("tools.fixtures.json")).write_text(json.dumps(out, indent=1, default=str))
    for name, v in out.items():
        print(name, len(v["cases"]), "cases;", sum(1 for c in v["cases"] if c.get("ok", c.get("oi", {}).get("ok"))), "ok")
