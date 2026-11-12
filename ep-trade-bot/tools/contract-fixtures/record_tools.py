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
        # OpenAlgo calls now() bare where it holds naive expiries (wall-clock Indian time) and now(tz) where it holds aware ones.
        return NOW.replace(tzinfo=None) if tz is None else NOW.astimezone(tz)


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


def record_vol_surface():
    import datetime as dt
    import random

    import services.option_greeks_service as g
    import services.vol_surface_service as vs

    g.datetime = Frozen
    real = dt.datetime
    dt.datetime = Frozen  # the surface reads datetime.now() through a local import
    try:
        cases = []

        def run(name, underlying, exchange, expiries, strike_count, ltp, strikes_by_expiry, quote_fail=None, no_quote=()):
            rng = random.Random(len(name))
            vs.get_quotes = (lambda **kw: (False, {"status": "error", "message": quote_fail[1]}, quote_fail[0])) if quote_fail else (lambda **kw: (True, {"data": {"ltp": ltp}}, 200))
            vs.get_available_strikes = lambda base, expiry, opt, exch: list(strikes_by_expiry.get(expiry, []))
            quotes = {}

            def multi(symbols, api_key):
                results = []
                for item in symbols:
                    sym = item["symbol"]
                    if sym in no_quote:
                        continue
                    if sym not in quotes:
                        quotes[sym] = round(rng.uniform(0, 400), 2) if rng.random() > 0.15 else 0
                    results.append({"symbol": sym, "data": {"ltp": quotes[sym]}})
                return True, {"results": results}, 200

            vs.get_multiquotes = multi
            ok, resp, code = vs.get_vol_surface_data(underlying, exchange, expiries, strike_count, "key")
            cases.append({"name": name, "underlying": underlying, "exchange": exchange, "expiries": expiries, "strike_count": strike_count, "ltp": ltp, "strikes": strikes_by_expiry,
                          "quote_fail": quote_fail, "quotes": quotes, "ok": ok, "code": code, "response": resp})

        wide = [23000 + 50 * i for i in range(0, 60)]
        narrow = [23500 + 100 * i for i in range(0, 20)]
        run("nifty-three-expiries", "NIFTY", "NSE_INDEX", ["26NOV26", "31DEC26", "28JAN27"], 6, 24057.0, {"26NOV26": wide, "31DEC26": wide, "28JAN27": narrow})
        run("single-expiry", "NIFTY", "NSE_INDEX", ["26NOV26"], 4, 24010.0, {"26NOV26": wide})
        run("banknifty-disjoint", "BANKNIFTY", "NSE_INDEX", ["26NOV26", "31DEC26"], 3, 52110.0, {"26NOV26": [51000 + 100 * i for i in range(30)], "31DEC26": [60000 + 100 * i for i in range(30)]})
        run("one-expiry-without-strikes", "NIFTY", "NSE_INDEX", ["26NOV26", "31DEC26"], 5, 24057.0, {"26NOV26": wide})
        run("fractional-strikes", "VEDL", "NFO", ["26NOV26"], 4, 292.4, {"26NOV26": [270 + 2.5 * i for i in range(20)]})
        run("sensex", "SENSEX", "BSE_INDEX", ["26NOV26"], 5, 80211.0, {"26NOV26": [78000 + 100 * i for i in range(40)]})
        run("no-expiry-given", "NIFTY", "NSE_INDEX", [], 5, 24057.0, {})
        run("no-strikes-anywhere", "NIFTY", "NSE_INDEX", ["26NOV26"], 5, 24057.0, {})
        run("no-ltp", "NIFTY", "NSE_INDEX", ["26NOV26"], 5, 0, {"26NOV26": wide})
        run("quote-failed", "NIFTY", "NSE_INDEX", ["26NOV26"], 5, 24057.0, {"26NOV26": wide}, quote_fail=(503, "Broker busy"))
        run("expired-expiry", "NIFTY", "NSE_INDEX", ["10NOV26"], 5, 24057.0, {"10NOV26": wide})
        return {"now": NOW.isoformat(), "cases": cases}
    finally:
        dt.datetime = real


def make_greek_chain(spot, atm, step, count, expiry_epoch, server_ts, lot, seed, forward=None, with_greeks=True):
    """An option chain as get_option_chain(with_greeks=True) returns it: each leg carries open interest, implied volatility, delta and gamma."""
    import random

    from opengreeks import black76

    rng = random.Random(seed)
    years = max((expiry_epoch - server_ts) / (365 * 86400), 0.0001)
    fwd = forward or spot * 1.002
    rows = []
    for i in range(-count, count + 1):
        strike = atm + i * step
        row = {"strike": strike}
        for leg in ("ce", "pe"):
            iv = round(11 + abs(i) * 0.35 + (1.5 if leg == "pe" and i < 0 else 0), 2)
            sigma = iv / 100
            flag = "c" if leg == "ce" else "p"
            d = {"symbol": f"X{strike}{leg.upper()}", "ltp": round(black76.black(flag, fwd, strike, years, 0.0, sigma), 2), "oi": rng.randint(0, 600000) * (1 if abs(i) < count - 2 else 0),
                 "lotsize": lot, "implied_volatility": iv}
            if with_greeks:
                d["delta"] = round(black76.delta(flag, fwd, strike, years, 0.0, sigma), 4)
                gamma = round(black76.gamma(flag, fwd, strike, years, 0.0, sigma), 6)
                d["gamma"] = 0.0 if gamma != gamma else gamma  # a chain carries no NaN
            row[leg] = d
        rows.append(row)
    return {"status": "success", "underlying": "NIFTY", "underlying_ltp": spot, "atm_strike": atm, "expiry_ts": expiry_epoch, "server_ts": server_ts, "forward_price": fwd, "chain": rows}


def record_strike_profile():
    import services.strike_profile_service as sp

    sp.datetime = Frozen
    cases = []
    now_epoch = int(NOW.timestamp())
    day = 86400

    def run(name, expiries, spot, range_pct, per_expiry, fail_after=None, busy=False, no_server_ts=False):
        calls = []

        def fetch(underlying, exchange, expiry_date, strike_count, api_key, with_greeks):
            idx = expiries.index(expiry_date)
            if fail_after is not None and idx >= fail_after:
                return (False, {"status": "error", "message": "Option chain unavailable"}, 429 if busy else 404)
            spec = per_expiry[expiry_date]
            chain = make_greek_chain(spot, spec["atm"], spec["step"], strike_count, spec["epoch"], now_epoch, spec["lot"], seed=idx + 3, with_greeks=spec.get("greeks", True))
            if no_server_ts:
                del chain["server_ts"]
            calls.append({"expiry": expiry_date, "strike_count": strike_count, "chain": chain})
            return True, chain, 200

        sp.get_option_chain = fetch
        ok, resp, code = sp.get_strike_profile("NIFTY", "NFO", expiries, "key", range_pct)
        cases.append({"name": name, "expiries": expiries, "range_pct": range_pct, "calls": calls, "fail_after": fail_after, "busy": busy, "ok": ok, "code": code, "response": resp})

    e1 = now_epoch + 15 * day + 19800
    e2 = now_epoch + 50 * day + 19800
    spec = lambda epoch, atm=24050, step=50, lot=75, **kw: {"epoch": epoch, "atm": atm, "step": step, "lot": lot, **kw}
    run("two-expiries-5pct", ["26NOV26", "31DEC26"], 24057.0, 5.0, {"26NOV26": spec(e1), "31DEC26": spec(e2)})
    run("widest-window", ["26NOV26"], 24057.0, None, {"26NOV26": spec(e1)})
    run("narrow-window", ["26NOV26"], 24057.0, 1.0, {"26NOV26": spec(e1)})
    run("second-expiry-missing", ["26NOV26", "31DEC26"], 24057.0, 5.0, {"26NOV26": spec(e1), "31DEC26": spec(e2)}, fail_after=1)
    run("first-expiry-fails", ["26NOV26"], 24057.0, 5.0, {"26NOV26": spec(e1)}, fail_after=0)
    run("broker-busy", ["26NOV26", "31DEC26"], 24057.0, 5.0, {"26NOV26": spec(e1), "31DEC26": spec(e2)}, fail_after=1, busy=True)
    run("no-spot", ["26NOV26"], 0, 5.0, {"26NOV26": spec(e1)})
    run("no-expiries", [], 24057.0, 5.0, {})
    run("no-server-ts", ["26NOV26"], 24057.0, 5.0, {"26NOV26": spec(e1)}, no_server_ts=True)
    run("chain-without-greeks", ["26NOV26"], 24057.0, 5.0, {"26NOV26": spec(e1, greeks=False)})
    run("expiry-today", ["26NOV26"], 24057.0, 5.0, {"26NOV26": spec(now_epoch + 3600)})
    run("wide-step", ["26NOV26"], 52110.0, 3.0, {"26NOV26": spec(e1, atm=52100, step=100, lot=30)})
    return {"now": NOW.isoformat(), "cases": cases}


def record_gamma_density():
    import services.gamma_density_service as gd
    import services.option_greeks_service as g

    g.datetime = Frozen
    cases = []

    def run(name, chain, exchange="NFO", expiry="26NOV26", forward=None, rate=None, fail=None):
        gd.get_option_chain = (lambda **kw: (False, {"status": "error", "message": fail[1]}, fail[0])) if fail else (lambda **kw: (True, chain, 200))
        gd._resolve_forward_price = lambda *a, **k: forward
        ok, resp, code = gd.calculate_gamma_density("NIFTY", exchange, expiry, "key", rate)
        cases.append({"name": name, "exchange": exchange, "expiry": expiry, "forward": forward, "rate": rate, "chain": chain, "fail": fail, "ok": ok, "code": code, "response": resp})

    chain = make_chain("NIFTY", 24050.0, 24050, 50, 23, "26NOV26", 75, 21)
    run("nifty-with-forward", chain, forward=24110.5)
    run("nifty-spot-as-forward", chain)
    run("with-rate", chain, forward=24110.5, rate=6.5)
    run("expiry-tomorrow", make_chain("NIFTY", 24050.0, 24050, 50, 23, "12NOV26", 75, 22), expiry="12NOV26", forward=24055.0)
    run("expired", make_chain("NIFTY", 24050.0, 24050, 50, 23, "10NOV26", 75, 23), expiry="10NOV26", forward=24055.0)
    run("mcx", make_chain("GOLD", 72500.0, 72500, 100, 12, "26NOV26", 100, 24), exchange="MCX", forward=72610.0)
    dead = make_chain("NIFTY", 24050.0, 24050, 50, 6, "26NOV26", 75, 25)
    for r in dead["chain"]:
        for leg in ("ce", "pe"):
            if r[leg]:
                r[leg]["ltp"] = 0
    run("no-usable-premium", dead, forward=24100.0)
    off = make_chain("NIFTY", 24050.0, 24050, 50, 10, "26NOV26", 75, 26)
    off["atm_strike"] = 99999
    run("atm-not-in-chain", off, forward=24100.0)
    junk = make_chain("NIFTY", 24050.0, 24050, 50, 6, "26NOV26", 75, 27)
    junk["chain"] += [{"strike": 0, "ce": {"oi": 5, "ltp": 1}, "pe": None}, {"strike": "x", "ce": None, "pe": None}, {"strike": -5, "ce": None, "pe": None}]
    run("junk-strikes", junk, forward=24100.0)
    run("no-spot", make_chain("NIFTY", 0, 24050, 50, 3, "26NOV26", 75, 28))
    empty = make_chain("NIFTY", 24050.0, 24050, 50, 3, "26NOV26", 75, 29)
    empty["chain"] = []
    run("empty-chain", empty)
    run("bad-month", chain, expiry="26XYZ26")
    run("chain-failed", None, fail=(503, "Option chain unavailable"))
    return {"now": NOW.isoformat(), "cases": cases}


RECORDERS = {"gamma_density": record_gamma_density, "strike_profile": record_strike_profile, "vol_surface": record_vol_surface, "greeks": record_greeks, "gex": record_gex, "iv_smile": record_iv_smile, "oi": record_oi}

if __name__ == "__main__":
    out = {name: fn() for name, fn in RECORDERS.items()}
    (Path(__file__).with_name("tools.fixtures.json")).write_text(json.dumps(out, indent=1, default=str))
    for name, v in out.items():
        print(name, len(v["cases"]), "cases;", sum(1 for c in v["cases"] if c.get("ok", c.get("oi", {}).get("ok"))), "ok")
