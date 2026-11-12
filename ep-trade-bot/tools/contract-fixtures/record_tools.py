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
    chain = make_chain("NIFTY", 24050.0, 24050, 200, 12, "26NOV26", 75, 8)
    sm.get_option_chain = lambda **kw: (True, chain, 200)
    ok, resp, code = sm.get_iv_smile_data("NIFTY", "NFO", "26NOV26", "key")
    cases.append({"name": "wide-step-skew-reaches-five-percent", "exchange": "NFO", "expiry": "26NOV26", "chain": chain, "ok": ok, "code": code, "response": resp})
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
        run("exactly-two-common-strikes", "NIFTY", "NSE_INDEX", ["26NOV26", "31DEC26"], 3, 24057.0,
            {"26NOV26": [24000 + 50 * i for i in range(-10, 10)], "31DEC26": [23800, 24050, 24100, 24300, 24400, 24500]})
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

    def run(name, expiries, spot, range_pct, per_expiry, fail_after=None, busy=False, no_server_ts=False, tweak=None):
        calls = []

        def fetch(underlying, exchange, expiry_date, strike_count, api_key, with_greeks):
            idx = expiries.index(expiry_date)
            if fail_after is not None and idx >= fail_after:
                return (False, {"status": "error", "message": "Option chain unavailable"}, 429 if busy else 404)
            spec = per_expiry[expiry_date]
            chain = make_greek_chain(spot, spec["atm"], spec["step"], strike_count, spec["epoch"], now_epoch, spec["lot"], seed=idx + 3, with_greeks=spec.get("greeks", True))
            if no_server_ts:
                del chain["server_ts"]
            if tweak:
                tweak(chain)
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
    def tie(chain):
        rows = [r for r in chain["chain"] if r["strike"] in (24000, 24100)]
        for r in rows:
            for leg in ("ce", "pe"):
                r[leg].update({"oi": 5000, "gamma": 0.001, "delta": 0.4 if leg == "ce" else -0.4, "implied_volatility": 12.0})
        for r in chain["chain"]:
            if r["strike"] not in (24000, 24100):
                for leg in ("ce", "pe"):
                    r[leg].update({"oi": 10, "gamma": 0.00001})

    run("tied-call-and-put-walls", ["26NOV26"], 24057.0, 5.0, {"26NOV26": spec(e1)}, tweak=tie)
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


def record_oi_profile():
    import types

    import services.oi_profile_service as op
    import services.strategy_chart_service as sc

    op.datetime = Frozen
    sc.datetime = Frozen
    sleeps = []
    op.time = types.SimpleNamespace(sleep=lambda s: sleeps.append(s), monotonic=lambda: 0.0)
    cases = []
    base_epoch = int(NOW.timestamp())

    def candles(n_days, ms=False, with_time=False):
        rows = []
        for d in range(n_days):
            day_start = base_epoch - (n_days - 1 - d) * 86400 - 10 * 3600  # 09:15-ish IST of each day
            for m in range(6):
                ts = day_start + m * 300
                row = {"open": 24000 + d + m, "high": 24010 + d, "low": 23990 + d, "close": 24005 + m, "volume": 1000 + m, "timestamp": ts * 1000 if ms else ts}
                if with_time:
                    row["time"] = ts
                rows.append(row)
        return rows

    def run(name, chain, days=3, interval="5m", futures=("NIFTY26NOVFUT", "NFO"), fut_candles=None, behaviours=None, exchange="NFO"):
        sleeps.clear()
        calls = []
        behaviours = behaviours or {}
        attempts = {}

        def history(symbol, exchange, interval, start_date, end_date, api_key):
            calls.append({"symbol": symbol, "exchange": exchange, "interval": interval, "start": start_date, "end": end_date})
            if interval != "D":
                return (True, {"data": fut_candles}, 200) if fut_candles is not None else (False, {"message": "none"}, 404)
            n = attempts[symbol] = attempts.get(symbol, 0) + 1
            kind = behaviours.get(symbol, "ok")
            if kind == "ok":
                return True, {"data": [{"oi": 100}, {"oi": 250 + len(symbol)}, {"oi": 999}]}, 200
            if kind == "one-row":
                return True, {"data": [{"oi": 5}]}, 200
            if kind == "empty":
                return True, {"data": []}, 200
            if kind == "always-429":
                return False, {"message": "slow down"}, 429
            if kind == "429-once":
                return (False, {"message": "slow down"}, 429) if n == 1 else (True, {"data": [{"oi": 40}, {"oi": 50}]}, 200)
            if kind == "raises-429":
                if n == 1:
                    raise Exception("HTTP 429 Too Many Requests")
                return True, {"data": [{"oi": 70}, {"oi": 80}]}, 200
            if kind == "raises":
                raise Exception("boom")
            if kind == "failed":
                return False, {"message": "no data"}, 500
            raise AssertionError(kind)

        op.get_option_chain = lambda **kw: (True, chain, 200)
        op._find_futures_symbol = lambda *a, **k: ({"symbol": futures[0], "exchange": futures[1]} if futures else None)
        op.get_history = history
        ok, resp, code = op.get_oi_profile_data("NIFTY", exchange, "26NOV26", interval, days, "key")
        cases.append({"name": name, "exchange": exchange, "interval": interval, "days": days, "futures": futures, "fut_candles": fut_candles, "behaviours": behaviours, "chain": chain, "calls": calls,
                      "sleeps": list(sleeps), "ok": ok, "code": code, "response": resp})

    chain = make_chain("NIFTY", 24050.0, 24050, 50, 20, "26NOV26", 75, 31)
    syms = [r[leg]["symbol"] for r in chain["chain"] for leg in ("ce", "pe") if r[leg] and r[leg].get("symbol") and r[leg].get("oi", 0) > 0]
    run("all-ok", chain, fut_candles=candles(4))
    run("milliseconds-and-cap", chain, days=2, fut_candles=candles(5, ms=True))
    run("candles-already-timed", chain, days=1, fut_candles=candles(2, with_time=True))
    run("no-futures", chain, futures=None)
    run("no-candles", chain, fut_candles=None)
    run("mixed-history", chain, fut_candles=candles(1), behaviours={syms[0]: "one-row", syms[1]: "empty", syms[2]: "always-429", syms[3]: "429-once", syms[4]: "raises-429", syms[5]: "raises", syms[6]: "failed"})
    small = make_chain("NIFTY", 24050.0, 24050, 50, 3, "26NOV26", 75, 32)
    run("small-chain", small, fut_candles=candles(1))
    run("bfo", make_chain("SENSEX", 80200.0, 80200, 100, 3, "26NOV26", 20, 33), exchange="BSE_INDEX", futures=("SENSEX26NOVFUT", "BFO"), fut_candles=candles(2))
    ops = {"status": "error", "message": "Option chain unavailable"}
    op.get_option_chain = lambda **kw: (False, ops, 503)
    ok, resp, code = op.get_oi_profile_data("NIFTY", "NFO", "26NOV26", "5m", 3, "key")
    cases.append({"name": "chain-failed", "exchange": "NFO", "interval": "5m", "days": 3, "futures": None, "fut_candles": None, "behaviours": {}, "chain": None, "calls": [], "sleeps": [], "ok": ok, "code": code, "response": resp})
    return {"now": NOW.isoformat(), "cases": cases}


def _strategy_chart_cases(which):
    """Shared harness for the two time-series tools that merge per-leg history: the combined-premium chart and the per-leg open-interest chart."""
    import services.multi_strike_oi_service as ms
    import services.strategy_chart_service as sc

    import services.strategy_builder_reference_service as ref

    REAL_REFERENCE = ref.resolve_strategy_builder_reference
    sc.datetime = Frozen
    mod = sc if which == "strategy_chart" else ms
    fn = sc.get_strategy_chart_data if which == "strategy_chart" else ms.get_multi_strike_oi_data
    midnight = int(NOW.replace(hour=0, minute=0, second=0).timestamp())
    cases = []

    def series(days_back, n=8, base=100.0, step=1.5, ms_ts=False, skip=(), no_close=(), oi=None, start_min=9 * 60 + 15, gap=300):
        rows = []
        for d in days_back:
            t0 = midnight - d * 86400 + start_min * 60 - 19800
            for i in range(n):
                if (d, i) in skip:
                    continue
                ts = t0 + i * gap
                row = {"timestamp": ts * 1000 if ms_ts else ts, "open": base + i, "high": base + i + 1, "low": base + i - 1, "close": round(base + i * step + d, 2), "volume": 100 + i}
                if (d, i) in no_close:
                    del row["close"]
                if oi is not None:
                    row["oi"] = oi + i * 10 + d
                rows.append(row)
        return rows

    def run(name, history, legs, days=3, interval="5m", under="NIFTY", exch="NSE_INDEX", u_sym=None, u_exch=None, start=None, end=None, quote=("ok", 24057.5), reference="real"):
        calls = []

        def get_history(symbol, exchange, interval, start_date, end_date, api_key):
            calls.append({"symbol": symbol, "exchange": exchange, "interval": interval, "start": start_date, "end": end_date})
            value = history.get(symbol, "missing")
            if value == "missing" or value == "fail":
                return False, {"status": "error", "message": "no history"}, 404
            if value == "no-data-key":
                return True, {"status": "success"}, 200
            return True, {"status": "success", "data": json.loads(json.dumps(value))}, 200

        resolved = []

        def resolve(*a, **k):
            r = None if reference is None else REAL_REFERENCE(*a, **k)
            resolved.append(r)
            return r

        mod.get_history = get_history
        mod.get_quotes = (lambda **kw: (True, {"data": {"ltp": quote[1]}}, 200)) if quote[0] == "ok" else (lambda **kw: (False, {"message": "x"}, 500))
        mod.resolve_strategy_builder_reference = resolve
        ok, resp, code = fn(under, exch, legs, interval, "key", days=days, underlying_symbol=u_sym, underlying_exchange=u_exch, start_date=start, end_date=end)
        cases.append({"name": name, "underlying": under, "exchange": exch, "legs": legs, "interval": interval, "days": days, "u_sym": u_sym, "u_exch": u_exch, "start": start, "end": end,
                      "history": history, "quote": list(quote), "reference": resolved[-1] if resolved else None, "reference_enabled": reference is not None, "calls": sorted(calls, key=lambda c: (c["symbol"], c["exchange"])),
                      "ok": ok, "code": code, "response": resp})

    leg = lambda sym, side="SELL", price=100.0, **kw: {"symbol": sym, "exchange": "NFO", "side": side, "segment": "OPTION", "price": price, "strike": 24050, "optionType": "CE" if sym.endswith("CE") else "PE", "expiry": "26NOV26", **kw}
    ce, pe, ce2 = "NIFTY26NOV2624050CE", "NIFTY26NOV2624050PE", "NIFTY26NOV2624100CE"
    d3 = (0, 1, 2)
    legs2 = [leg(ce, "SELL", 120.5), leg(pe, "SELL", 110.25)]
    base = {"NIFTY": series(d3, base=24000, step=3), ce: series(d3, base=120, oi=50000), pe: series(d3, base=110, oi=60000)}
    run("straddle-credit", base, legs2)
    run("debit-spread", {**base, ce2: series(d3, base=60, oi=40000)}, [leg(ce, "BUY", 120.5), leg(ce2, "SELL", 60.0)])
    run("flat-entry", base, [leg(ce, "BUY", 100), leg(pe, "SELL", 100)])
    run("days-1", base, legs2, days=1)
    run("days-2-ms-timestamps", {**base, ce: series(d3, base=120, ms_ts=True, oi=1), pe: series(d3, base=110, ms_ts=True, oi=1), "NIFTY": series(d3, base=24000, ms_ts=True)}, legs2, days=2)
    run("leg-missing-candles", {**base, pe: series(d3, base=110, skip={(0, 1), (0, 3), (1, 5)}, oi=2)}, legs2)
    run("underlying-missing-candles", {**base, "NIFTY": series(d3, base=24000, skip={(0, 2), (0, 4)})}, legs2)
    run("underlying-history-fails", {k: v for k, v in base.items() if k != "NIFTY"}, legs2)
    run("underlying-empty", {**base, "NIFTY": []}, legs2)
    run("underlying-no-timestamp", {**base, "NIFTY": [{"close": 1}, {"close": 2}]}, legs2)
    run("no-overlap", {**base, ce: series((5,), base=120), pe: series((6,), base=110)}, legs2)
    run("no-underlying-no-overlap", {ce: series(d3), pe: series(d3, skip={(d, k) for d in d3 for k in range(8)})}, legs2)
    run("leg-history-fails", {"NIFTY": base["NIFTY"], ce: "fail", pe: base[pe]}, legs2)
    run("leg-history-without-data", {"NIFTY": base["NIFTY"], ce: "no-data-key", pe: base[pe]}, legs2)
    run("duplicate-legs", base, [leg(ce, "SELL", 100), leg(ce, "SELL", 100), leg(pe, "BUY", 50)])
    run("filtered-legs", base, [leg(ce, "SELL", 100), leg(pe, "SELL", 100, active=False), leg("NIFTY26NOVFUT", "BUY", 24000, segment="FUTURE"), leg("X", "HOLD"), {"symbol": "Y", "side": "BUY"}, "junk", None, leg(pe, "buy", "bad")])
    run("string-price", base, [leg(ce, "SELL", "120.5"), leg(pe, "SELL", None)])
    clock = {"NIFTY": series(d3, n=24, base=24000, start_min=0, gap=3600), ce: series(d3, n=24, base=120, start_min=0, gap=3600, oi=500), pe: series(d3, n=24, base=110, start_min=0, gap=3600, oi=600)}
    run("round-the-clock-one-day", clock, legs2, days=1)
    run("round-the-clock-two-days", clock, legs2, days=2)
    run("explicit-window", base, legs2, start="2026-11-01", end="2026-11-10")
    run("explicit-window-open-end", base, legs2, start="2026-11-05")
    run("explicit-window-bad-date", base, legs2, start="2026-02-31")
    run("explicit-window-shape", base, legs2, start="11/05/2026")
    run("explicit-window-before-2000", base, legs2, start="1999-12-31")
    run("explicit-window-inverted", base, legs2, start="2026-11-10", end="2026-11-01")
    run("explicit-window-too-wide", base, legs2, start="2025-01-01", end="2026-11-10")
    run("no-legs", base, [])
    run("no-underlying-name", base, legs2, under="  ")
    run("no-reference", base, legs2, exch="NCDEX", reference=None)
    run("given-reference", {**base, "NIFTY-X": series(d3, base=24000)}, legs2, u_sym="nifty-x", u_exch="nse_index")
    run("banknifty", {"BANKNIFTY": series(d3, base=52000), ce: series(d3, base=500), pe: series(d3, base=480)}, legs2, under="BANKNIFTY")
    run("quote-fails", base, legs2, quote=("fail", 0))
    run("sensex-bse", {"SENSEX": series(d3, base=80000), ce: series(d3, base=500), pe: series(d3, base=480)}, [leg(ce, "SELL", 5, exchange="BFO"), leg(pe, "SELL", 5, exchange="BFO")], under="SENSEX", exch="BFO")
    return {"now": NOW.isoformat(), "cases": cases}


def record_straddle_chart():
    import services.straddle_chart_service as st
    import services.strategy_chart_service as sc

    st.datetime = Frozen
    sc.datetime = Frozen
    midnight = int(NOW.replace(hour=0, minute=0, second=0).timestamp())
    cases = []

    def spot_at(d, i, centre):
        return round(centre + (d - 1) * 40 + i * 13 - (i % 3) * 21, 2)

    def underlying(days, n=14, centre=24000.0, ms=False, skip=(), key="close"):
        rows = []
        for d in days:
            t0 = midnight - d * 86400 + (9 * 60 + 15) * 60 - 19800
            for i in range(n):
                if (d, i) in skip:
                    continue
                ts = t0 + i * 300
                rows.append({"timestamp": ts * 1000 if ms else ts, "open": 1, "high": 1, "low": 1, key: spot_at(d, i, centre), "volume": 1})
        return rows

    def option(days, strike, kind, n=14, centre=24000.0, ms=False, skip=()):
        rows = []
        for d in days:
            t0 = midnight - d * 86400 + (9 * 60 + 15) * 60 - 19800
            for i in range(n):
                if (d, i) in skip:
                    continue
                ts = t0 + i * 300
                spot = spot_at(d, i, centre)
                rows.append({"timestamp": ts * 1000 if ms else ts, "close": round(abs(spot - strike) * 0.35 + 55 + (3 if kind == "CE" else 5) + i * 0.1, 2)})
        return rows

    def run(name, strikes, days=3, expiry="26NOV26", interval="5m", under="NIFTY", exch="NSE_INDEX", u_hist="ok", opt_skip=None, ms=False, quote=("ok", 24057.5), centre=24000.0, drop_options=(), step=50):
        calls = []
        hist = {}
        d3 = tuple(range(3))
        quote_symbol = under
        if u_hist == "ok":
            hist[quote_symbol] = underlying(d3, centre=centre, ms=ms)
        elif u_hist == "no-close":
            hist[quote_symbol] = underlying(d3, centre=centre, key="px")
        else:
            hist[quote_symbol] = u_hist
        for st_ in strikes:
            for kind in ("CE", "PE"):
                sym = f"{under}{expiry.upper()}{int(st_)}{kind}"
                if sym in drop_options:
                    continue
                hist[sym] = option(d3, st_, kind, centre=centre, ms=ms, skip=(opt_skip or {}).get(sym, ()))

        def get_history(symbol, exchange, interval, start_date, end_date, api_key):
            calls.append({"symbol": symbol, "exchange": exchange, "interval": interval, "start": start_date, "end": end_date})
            v = hist.get(symbol, "missing")
            if v == "missing":
                return False, {"status": "error", "message": "no history"}, 404
            if isinstance(v, dict):
                return v["ok"], v["resp"], 200
            return True, {"data": json.loads(json.dumps(v))}, 200

        st.get_available_strikes = lambda base, expiry_, opt, exch_: list(strikes)
        st.get_history = get_history
        st.get_quotes = (lambda **kw: (True, {"data": {"ltp": quote[1]}}, 200)) if quote[0] == "ok" else (lambda **kw: (False, {}, 500))
        ok, resp, code = st.get_straddle_chart_data(under, exch, expiry, interval, "key", days)
        cases.append({"name": name, "underlying": under, "exchange": exch, "expiry": expiry, "interval": interval, "days": days, "strikes": list(strikes), "history": hist, "quote": list(quote),
                      "calls": sorted(calls, key=lambda c: (c["symbol"], c["exchange"])), "ok": ok, "code": code, "response": resp})

    strikes = [23800 + 50 * i for i in range(0, 12)]
    run("nifty", strikes)
    run("days-1", strikes, days=1)
    run("days-2-ms", strikes, days=2, ms=True)
    run("lower-case-expiry", strikes, expiry="26nov26")
    run("expired", strikes, expiry="10NOV26")
    run("expiry-in-2-days", strikes, expiry="13NOV26")
    run("option-gaps", strikes, opt_skip={f"NIFTY26NOV26{s}{k}": {(0, 3), (1, 5), (2, 7)} for s in strikes for k in ("CE", "PE")})
    run("some-options-missing", strikes, drop_options={f"NIFTY26NOV26{s}PE" for s in strikes[:6]})
    run("all-options-missing", strikes, drop_options={f"NIFTY26NOV26{s}{k}" for s in strikes for k in ("CE", "PE")})
    run("no-strikes", [])
    run("underlying-fails", strikes, u_hist={"ok": False, "resp": {"message": "token expired"}})
    run("underlying-fails-no-message", strikes, u_hist={"ok": False, "resp": {}})
    run("underlying-empty", strikes, u_hist=[])
    run("underlying-no-timestamp", strikes, u_hist=[{"close": 1}])
    run("underlying-no-close", strikes, u_hist="no-close")
    run("banknifty-wide-step", [51000 + 100 * i for i in range(10)], under="BANKNIFTY", centre=51500.0)
    run("sensex", [79000 + 100 * i for i in range(14)], under="SENSEX", exch="BSE_INDEX", centre=79500.0)
    run("quote-fails", strikes, quote=("fail", 0))
    run("fractional-strikes", [291.0 + 2.5 * i for i in range(8)], under="VEDL", exch="NFO", centre=295.0)
    return {"now": NOW.isoformat(), "cases": cases}


def record_custom_straddle():
    import services.custom_straddle_service as cs
    import services.straddle_chart_service as st
    import services.strategy_chart_service as sc

    st.datetime = Frozen
    sc.datetime = Frozen
    midnight = int(NOW.replace(hour=0, minute=0, second=0).timestamp())
    cases = []

    def spot_at(d, i, centre, swing):
        return round(centre + (d - 1) * 40 * swing + i * 13 * swing - (i % 3) * 21 * swing, 2)

    def underlying(days, n, centre, swing, skip=()):
        rows = []
        for d in days:
            t0 = midnight - d * 86400 + (9 * 60 + 15) * 60 - 19800
            for i in range(n):
                if (d, i) in skip:
                    continue
                rows.append({"timestamp": t0 + i * 300, "close": spot_at(d, i, centre, swing)})
        return rows

    def option(days, strike, kind, n, centre, swing, skip=(), zero=()):
        rows = []
        for d in days:
            t0 = midnight - d * 86400 + (9 * 60 + 15) * 60 - 19800
            for i in range(n):
                if (d, i) in skip:
                    continue
                spot = spot_at(d, i, centre, swing)
                close = 0.0 if (d, i) in zero else round(abs(spot - strike) * 0.35 + 55 + (3 if kind == "CE" else 5) + i * 0.1, 2)
                rows.append({"timestamp": t0 + i * 300, "close": close})
        return rows

    def run(name, strikes, days=1, adj=50, lot=65, lots=1, expiry="26NOV26", n=14, centre=24000.0, swing=1.0, opt_skip=None, opt_zero=None, u_skip=(), under="NIFTY", exch="NSE_INDEX", u_hist=None, quote=("ok", 24057.5), n_days=3):
        calls = []
        d_all = tuple(range(n_days))
        hist = {under: u_hist if u_hist is not None else underlying(d_all, n, centre, swing, u_skip)}
        for strike in strikes:
            for kind in ("CE", "PE"):
                sym = f"{under}{expiry.upper()}{int(strike)}{kind}"
                hist[sym] = option(d_all, strike, kind, n, centre, swing, (opt_skip or {}).get(sym, ()), (opt_zero or {}).get(sym, ()))

        def get_history(symbol, exchange, interval, start_date, end_date, api_key):
            calls.append({"symbol": symbol, "exchange": exchange, "interval": interval, "start": start_date, "end": end_date})
            v = hist.get(symbol, "missing")
            if v == "missing":
                return False, {"message": "no history"}, 404
            if isinstance(v, dict):
                return v["ok"], v["resp"], 200
            return True, {"data": json.loads(json.dumps(v))}, 200

        cs.get_available_strikes = lambda base, expiry_, opt, exch_: list(strikes)
        cs.get_history = get_history
        cs.get_quotes = (lambda **kw: (True, {"data": {"ltp": quote[1]}}, 200)) if quote[0] == "ok" else (lambda **kw: (False, {}, 500))
        ok, resp, code = cs.get_custom_straddle_simulation(under, exch, expiry, "5m", "key", days=days, adjustment_points=adj, lot_size=lot, lots=lots)
        cases.append({"name": name, "underlying": under, "exchange": exch, "expiry": expiry, "days": days, "adj": adj, "lot": lot, "lots": lots, "strikes": list(strikes), "history": hist,
                      "quote": list(quote), "calls": sorted(calls, key=lambda c: (c["symbol"], c["exchange"])), "ok": ok, "code": code, "response": resp})

    strikes = [23700 + 50 * i for i in range(0, 14)]
    run("one-day-50", strikes)
    run("one-day-100", strikes, adj=100)
    run("calm-market-no-adjustment", strikes, swing=0.2)
    run("wild-market", strikes, swing=3.0, adj=50)
    run("three-days", strikes, days=3, swing=1.5)
    run("two-days-lots", strikes, days=2, lot=75, lots=3, adj=75)
    run("gaps-skip-entry", strikes, opt_skip={f"NIFTY26NOV26{int(s)}CE": {(0, 0), (0, 1)} for s in strikes})
    run("gaps-block-adjustment", strikes, swing=2.0, opt_skip={f"NIFTY26NOV26{int(s)}PE": {(0, 4), (0, 5), (0, 6)} for s in strikes})
    run("missing-last-candle", strikes, swing=2.0, opt_skip={f"NIFTY26NOV26{int(s)}{k}": {(0, 13)} for s in strikes for k in ("CE", "PE")})
    run("zero-priced-option", strikes, opt_zero={f"NIFTY26NOV26{int(s)}CE": {(0, 6), (0, 7)} for s in strikes})
    run("no-options-at-all", [], expiry="26NOV26")
    run("options-missing", strikes[:3], swing=0.1)
    run("underlying-fails", strikes, u_hist={"ok": False, "resp": {"message": "token expired"}})
    run("underlying-fails-no-message", strikes, u_hist={"ok": False, "resp": {}})
    run("underlying-empty", strikes, u_hist=[])
    run("underlying-no-timestamp", strikes, u_hist=[{"close": 1}])
    run("underlying-no-close", strikes, u_hist=[{"timestamp": midnight, "px": 1}])
    run("banknifty", [51000 + 100 * i for i in range(10)], under="BANKNIFTY", centre=51500.0, adj=100, lot=30)
    run("quote-fails", strikes, quote=("fail", 0))
    run("expired", strikes, expiry="10NOV26")
    return {"now": NOW.isoformat(), "cases": cases}


def record_arbitrage():
    import services.arbitrage_service as ar

    ar.datetime = Frozen
    cases = []

    def fut(exchange, name, expiry, sym=None, lot=50, tick=0.05, **extra):
        sym = sym or f"{name}{expiry.replace('-', '')[:5]}FUT"
        return {"symbol": sym, "exchange": exchange, "name": name, "expiry": expiry, "lotsize": lot, "tick_size": tick, **extra}

    universe = {
        "NFO": [
            fut("NFO", "NIFTY", "26-NOV-26", lot=75), fut("NFO", "NIFTY", "31-DEC-26", lot=75), fut("NFO", "NIFTY", "28-JAN-27", lot=75), fut("NFO", "NIFTY", "25-FEB-27", lot=75),
            fut("NFO", "BANKNIFTY", "31-DEC-26", lot=30), fut("NFO", "BANKNIFTY", "26-NOV-26", lot=30), fut("NFO", "BANKNIFTY", "28-JAN-27", lot=30),
            fut("NFO", "RELIANCE", "26-NOV-26", lot=250), fut("NFO", "RELIANCE", "31-DEC-26", lot=250),
            fut("NFO", "SBIN", "26-NOV-26", lot=1500),
            fut("NFO", "TCS", "26-NOV-2026", sym="TCS26NOV26FUT", lot=175), fut("NFO", "TCS", "31-dec-2026", sym="TCS31DEC26FUT", lot=175), fut("NFO", "TCS", "bad-date", sym="TCSBADFUT", lot=175),
            fut("NFO", "INFY", "26-NOV-26"), fut("NFO", "INFY", "26-NOV-26", sym="INFY26NOVFUT", lot=400), fut("NFO", "INFY", "31-DEC-26"),
            {"symbol": "NIFTY26NOV2624000CE", "exchange": "NFO", "name": "NIFTY", "expiry": "26-NOV-26"},
            fut("NFO", None, "26-NOV-26", sym="NONAMEFUT"), fut("NFO", "NOEXP", "", sym="NOEXPFUT"), {"symbol": None, "exchange": "NFO", "name": "ZZ", "expiry": "26-NOV-26"},
            fut("NFO", "ITC", "26-NOV-26", sym="itc26novfut", lot=1600), fut("NFO", "ITC", "31-DEC-26", sym="itc31decfut", lot=1600),
        ],
        "MCX": [fut("MCX", "GOLD", "05-DEC-26", lot=100, tick=1.0), fut("MCX", "GOLD", "05-FEB-27", lot=100, tick=1.0), fut("MCX", "GOLD", "05-APR-27", lot=100, tick=1.0), fut("MCX", "CRUDEOIL", "19-NOV-26", lot=100, tick=1.0), fut("MCX", "CRUDEOIL", "21-DEC-26", lot=100, tick=1.0)],
        "BFO": [fut("BFO", "SENSEX", "26-NOV-26", lot=20), fut("BFO", "SENSEX", "31-DEC-26", lot=20)],
        "CDS": [fut("CDS", "USDINR", "27-NOV-26", lot=1000, tick=0.0025), fut("CDS", "USDINR", "29-DEC-26", lot=1000, tick=0.0025), fut("CDS", "USDINR", "27-JAN-27", lot=1000, tick=0.0025)],
    }

    def run(name, exchanges=None):
        ar.fno_search_symbols = lambda exchange, instrumenttype, limit: list(universe.get(exchange, []))
        ok, resp, code = (ar.get_arbitrage_universe() if exchanges is None else ar.get_arbitrage_universe(exchanges))
        cases.append({"name": name, "exchanges": exchanges, "universe": universe, "ok": ok, "code": code, "response": resp})

    run("default")
    run("nfo-only", ["NFO"])
    run("all-four", ["nfo", "MCX", "bfo", "cds"])
    run("padded-and-unsupported", [" nfo ", "xyz", ""])
    run("only-unsupported", ["XYZ"])
    run("empty-list", [])
    run("blank-entries", ["", "  "])
    run("duplicate-exchange", ["NFO", "NFO"])
    run("bfo-two-contracts", ["BFO"])
    return {"now": NOW.isoformat(), "cases": cases}


def record_strategy_pnl():
    import database.strategy_book_db as book
    import services.strategy_pnl_service as sp

    cases = []
    legs = [
        {"strategy": "alpha", "symbol": "NIFTY26NOV2624000CE", "exchange": "NFO", "product": "MIS", "quantity": -75, "average_price": 120.5, "realized_pnl": 1500.25, "today_realized_pnl": 300.0},
        {"strategy": "alpha", "symbol": "NIFTY26NOV2624000PE", "exchange": "NFO", "product": "MIS", "quantity": -75, "average_price": 98.0, "realized_pnl": -200.0, "today_realized_pnl": 0},
        {"strategy": "alpha", "symbol": "NIFTY26NOV2623500PE", "exchange": "NFO", "product": "MIS", "quantity": 0, "average_price": 0, "realized_pnl": 410.1234567, "today_realized_pnl": 410.1234567},
        {"strategy": "beta", "symbol": "SBIN", "exchange": "NSE", "product": "CNC", "quantity": 100, "average_price": 812.4, "realized_pnl": 0, "today_realized_pnl": 0},
        {"strategy": "beta", "symbol": "INFY", "exchange": "NSE", "product": "CNC", "quantity": 10, "average_price": 1500.0, "realized_pnl": 12.5, "today_realized_pnl": 0},
        {"strategy": None, "symbol": "TCS", "exchange": "NSE", "product": "MIS", "quantity": "5", "average_price": "3500.5", "realized_pnl": "bad", "today_realized_pnl": None},
        {"strategy": "", "symbol": "ITC", "exchange": "NSE", "product": "MIS", "quantity": 1e-12, "average_price": 400, "realized_pnl": 1, "today_realized_pnl": 1},
        {"symbol": "HDFC", "exchange": "NSE", "product": "MIS", "quantity": -3, "average_price": 1600},
        {"strategy": "gamma", "symbol": "ZEEL", "exchange": "NSE", "product": "MIS", "quantity": 0, "average_price": 0, "realized_pnl": -50, "today_realized_pnl": -50},
        {"strategy": "gamma", "symbol": "WIPRO", "exchange": "NSE", "product": "MIS", "quantity": 20, "average_price": 450.0, "realized_pnl": 0, "today_realized_pnl": 0},
    ]
    positions = [
        {"symbol": "NIFTY26NOV2624000CE", "exchange": "NFO", "product": "MIS", "ltp": 101.25},
        {"symbol": "NIFTY26NOV2624000PE", "exchange": "NFO", "product": "MIS", "last_price": 130.0},
        {"symbol": "SBIN", "exchange": "NSE", "product": "CNC", "ltp": 0},
        {"symbol": "INFY", "exchange": "NSE", "product": "CNC"},
        {"symbol": "TCS", "exchange": "NSE", "product": "MIS", "ltp": "3600.25"},
        {"symbol": "WIPRO", "exchange": "NSE", "product": "MIS", "ltp": 455.5},
        {"symbol": "WIPRO", "exchange": "NSE", "product": "MIS", "ltp": 460.0},
    ]

    def book_case(name, strategy=None, lg=legs, ps=positions):
        cases.append({"name": name, "kind": "book", "strategy": strategy, "legs": lg, "positions": ps, "response": sp.pnl_from_book(lg, ps, strategy=strategy)})

    book_case("all-strategies")
    book_case("alpha", "alpha")
    book_case("beta", "beta")
    book_case("untagged", "untagged")
    book_case("gamma-flat-leg-sorted-last", "gamma")
    book_case("unknown-strategy", "nothing")
    book_case("no-positions", None, legs, [])
    book_case("no-legs", None, [], positions)
    book_case("none-positions", None, legs, None)

    class Client:
        def __init__(self, reply):
            self.reply = reply

        def positionbook(self):
            return self.reply

    def get_case(name, strategy=None, reply=None, legs_result=None, unavailable=None):
        def get_strategy_legs(user_id=None, strategy=None):
            if unavailable:
                raise book.StrategyBookUnavailable(unavailable)
            return legs_result if legs_result is not None else legs

        book.get_strategy_legs = get_strategy_legs
        out = sp.get_strategy_pnl(Client(reply), strategy=strategy)
        cases.append({"name": name, "kind": "get", "strategy": strategy, "legs": legs_result if legs_result is not None else legs, "reply": reply, "unavailable": unavailable, "response": out})

    get_case("get-all", None, {"status": "success", "data": positions})
    get_case("get-one", "alpha", {"status": "success", "data": positions})
    get_case("get-empty-reply", "alpha", {})
    get_case("get-none-reply", "alpha", None)
    get_case("get-error-with-error", "alpha", {"status": "error", "error": "broker down", "message": "ignored"})
    get_case("get-error-with-message", None, {"status": "error", "message": "token expired"})
    get_case("get-error-bare", None, {"status": "error"})
    get_case("get-data-not-a-list", "alpha", {"status": "success", "data": {"oops": 1}})
    get_case("get-book-unavailable", None, {"status": "success", "data": positions}, unavailable="database locked")
    return {"now": NOW.isoformat(), "cases": cases}


def record_sector_rotation():
    import random
    import types
    from datetime import date, timedelta

    import pandas as pd

    import services.sector_rotation_service as sr
    from portfolio.data import DataError

    end = date(2026, 11, 10)

    def sessions(first, last, skip=()):
        d, out = first, []
        while d <= last:
            if d.weekday() < 5 and d not in skip:
                out.append(d)
            d += timedelta(days=1)
        return out

    def walk(days, seed, start=1000.0, drift=0.0003, vol=0.01, trend=0.0):
        rng = random.Random(seed)
        price, out = start, []
        for i, _ in enumerate(days):
            price *= 1 + rng.gauss(drift + trend * (1 if i > len(days) // 2 else -1), vol)
            out.append(round(price, 2))
        return out

    cases = []
    first = end - timedelta(days=800)

    def run(name, benchmark="NIFTY", timeframe="daily", source="db", instruments=None, data=None, errors=None, bench_error=False):
        loads = []
        data = data or {}
        errors = errors or {}

        def load_prices(symbols, exchanges, start, end_, source="db", allowed_exchanges=None, cache=True, **creds):
            sym = symbols[0]
            loads.append({"symbol": sym, "exchange": exchanges[0], "start": start, "end": end_, "source": source})
            if sym in errors:
                raise (DataError(errors[sym]) if errors[sym] != "boom" else RuntimeError("boom"))
            dates, closes = data[sym]
            frame = pd.DataFrame({sym: closes}, index=pd.DatetimeIndex(pd.to_datetime(dates)))
            return types.SimpleNamespace(closes=frame, source=source, warnings={}, start=None, end=None)

        sr.load_prices = load_prices
        sr._CLOSES_CACHE.clear()
        ok, resp, code = sr.compute_sector_rotation(benchmark, timeframe, source, instruments=instruments, end_date=end)
        cases.append({"name": name, "benchmark": benchmark, "timeframe": timeframe, "source": source, "instruments": instruments, "data": data, "errors": errors, "loads": loads, "ok": ok, "code": code, "response": resp})

    days = sessions(first, end)
    bsedays = sessions(first, end, skip={date(2026, 4, 3), date(2026, 9, 14)})
    names = ["NIFTYAUTO", "NIFTYIT", "NIFTYPHARMA", "FINNIFTY", "NIFTYMETAL", "NIFTYREALTY", "NIFTYFMCG", "NIFTYENERGY", "NIFTYMEDIA", "NIFTYPSE", "NIFTYPSUBANK", "NIFTYPVTBANK", "NIFTYINFRA", "NIFTYCOMMODITIES"]
    base = {"NIFTY": ([d.isoformat() for d in days], walk(days, 1, 20000.0)), "NIFTY500": ([d.isoformat() for d in days], walk(days, 2, 18000.0)),
            "SENSEX": ([d.isoformat() for d in bsedays], walk(bsedays, 3, 70000.0))}
    for i, n in enumerate(names):
        trend = (i - 7) * 0.0002
        base[n] = ([d.isoformat() for d in days], walk(days, 10 + i, 1000.0 + 100 * i, trend=trend))

    run("default-daily", data=base)
    run("default-weekly", timeframe="weekly", data=base)
    run("api-source", source="api", data=base)
    run("sensex-benchmark-holiday-mismatch", benchmark="SENSEX", timeframe="weekly", data=base)
    run("sensex-benchmark-daily", benchmark="sensex", data=base)
    run("custom-instruments", instruments=[{"symbol": "nifty it", "exchange": "nse_index"}] if False else [{"symbol": "niftyit", "exchange": "nse_index", "label": "Tech"}, {"symbol": "NIFTYPHARMA", "exchange": "NSE_INDEX"}, {"symbol": "NIFTYPHARMA", "exchange": "NSE_INDEX", "label": "dup"},
                                                                 {"symbol": "NIFTYAUTO", "exchange": "NSE_INDEX", "label": "  "}], data=base)
    run("benchmark-in-the-list", instruments=[{"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {"symbol": "NIFTYIT", "exchange": "NSE_INDEX"}], data=base)
    short = {**base, "NIFTYIT": ([d.isoformat() for d in days[-30:]], walk(days[-30:], 99))}
    run("short-history-and-missing", data=short, errors={"NIFTYMEDIA": "no data", "NIFTYPSE": "boom"})
    run("short-history-weekly", timeframe="weekly", data=short)
    run("everything-missing", data={"NIFTY": base["NIFTY"]}, errors={n: "no data" for n in names})
    run("only-short-history", instruments=[{"symbol": "NIFTYIT", "exchange": "NSE_INDEX"}], data=short)
    run("benchmark-missing-db", data={}, errors={"NIFTY": "no data"})
    run("benchmark-missing-api", source="api", data={}, errors={"NIFTY": "no data"})
    run("bad-benchmark", benchmark="DOW", data=base)
    run("blank-benchmark", benchmark="", data=base)
    run("bad-timeframe", timeframe="monthly", data=base)
    run("bad-source", source="file", data=base)
    run("too-many", instruments=[{"symbol": f"S{i}", "exchange": "NSE"} for i in range(41)], data=base)
    run("blank-symbol", instruments=[{"symbol": " ", "exchange": "NSE"}], data=base)
    run("bad-exchange", instruments=[{"symbol": "ES", "exchange": "NFO"}], data=base)
    run("no-exchange", instruments=[{"symbol": "ES"}], data=base)
    run("stocks-on-the-map", instruments=[{"symbol": "INFY", "exchange": "NSE"}, {"symbol": "TCS", "exchange": "NSE", "label": "Tata Consultancy"}],
        data={**base, "INFY": ([d.isoformat() for d in days], walk(days, 41, 1500.0)), "TCS": ([d.isoformat() for d in days], walk(days, 42, 3500.0))})
    boundaries = [[r, m] for r in (99.999, 100.0, 100.001) for m in (99.999, 100.0, 100.001)]
    quadrants = [[r, m, sr.quadrant(r, m)] for r, m in boundaries]
    return {"now": NOW.isoformat(), "end": end.isoformat(), "cases": cases, "options": sr.list_options()[1], "quadrants": quadrants}


def record_iv_chart():
    import services.iv_chart_service as ic
    import services.strategy_chart_service as sc
    from opengreeks import black76

    sc.datetime = Frozen
    midnight = int(NOW.replace(hour=0, minute=0, second=0).timestamp())
    cases = []

    def candle_ts(d, i, offset_min=0):
        return midnight - d * 86400 + (9 * 60 + 15 + offset_min) * 60 - 19800 + i * 300

    def expiry_epoch(code):
        from datetime import datetime as dt
        return dt.strptime(code, "%d%b%y").replace(hour=15, minute=30)

    def spot_at(d, i, centre):
        return round(centre + (d - 1) * 35 + i * 9 - (i % 4) * 17, 2)

    def underlying(days, centre, n=14, ms=False, skip=(), key="close", tshift=0):
        rows = []
        for d in days:
            for i in range(n):
                if (d, i) in skip:
                    continue
                ts = candle_ts(d, i, tshift)
                rows.append({"timestamp": ts * 1000 if ms else ts, key: spot_at(d, i, centre)})
        return rows

    def option(days, centre, strike, flag, expiry, n=14, skip=(), shift=0, zero=(), junk=(), tshift=0):
        shift = shift + tshift
        rows = []
        e = expiry_epoch(expiry)
        for d in days:
            for i in range(n):
                if (d, i) in skip:
                    continue
                ts = candle_ts(d, i, shift)
                spot = spot_at(d, i, centre)
                from datetime import datetime as dt, timedelta as td
                candle = dt.utcfromtimestamp(ts + 19800)
                years = max((e - candle).total_seconds() / 86400 / 365, 0.0001) if e > candle else 0.0
                sigma = 0.11 + 0.002 * i + 0.01 * d
                if (d, i) in zero:
                    close = 0.0
                elif (d, i) in junk:
                    close = round(abs(spot - strike) + 0.01, 2)  # at or under intrinsic once discounting is ignored
                elif years > 0:
                    close = round(black76.black(flag, spot, strike, years, 0.0, sigma), 2)
                else:
                    close = round(max(spot - strike, 0) if flag == "c" else max(strike - spot, 0), 2)
                rows.append({"timestamp": ts, "close": close})
        return rows

    def run(name, strikes, days=1, expiry="26NOV26", under="NIFTY", exch="NSE_INDEX", centre=24000.0, interval="5m", u_hist=None, ce_hist=None, pe_hist=None, quote=("ok", 24057.5), n_days=3, atm=None, **kw):
        calls = []
        d_all = tuple(range(n_days))
        atm_strike = atm if atm is not None else min(strikes, key=lambda s: abs(s - quote[1])) if strikes and quote[0] == "ok" else None
        hist = {under: u_hist if u_hist is not None else underlying(d_all, centre, **{k: v for k, v in kw.items() if k in ("n", "ms", "skip", "key", "tshift")})}
        if atm_strike is not None:
            hist[f"{under}{expiry.upper()}{int(atm_strike)}CE"] = ce_hist if ce_hist is not None else option(d_all, centre, atm_strike, "c", expiry, **{k: v for k, v in kw.items() if k in ("n", "shift", "zero", "junk", "tshift")})
            hist[f"{under}{expiry.upper()}{int(atm_strike)}PE"] = pe_hist if pe_hist is not None else option(d_all, centre, atm_strike, "p", expiry, **{k: v for k, v in kw.items() if k in ("n", "shift", "zero", "junk", "tshift")})

        def get_history(symbol, exchange, interval, start_date, end_date, api_key):
            calls.append({"symbol": symbol, "exchange": exchange, "interval": interval, "start": start_date, "end": end_date})
            v = hist.get(symbol, "missing")
            if v == "missing":
                return False, {"message": "no history"}, 404
            if isinstance(v, dict):
                return v["ok"], v["resp"], 200
            return True, {"data": json.loads(json.dumps(v))}, 200

        ic.get_available_strikes = lambda base, expiry_, opt, exch_: list(strikes)
        ic.get_history = get_history
        ic.get_quotes = (lambda **kw_: (True, {"data": {"ltp": quote[1]}}, 200)) if quote[0] == "ok" else (lambda **kw_: (False, {"message": quote[2] if len(quote) > 2 else None} if len(quote) > 2 else {}, 503))
        ok, resp, code = ic.get_iv_chart_data(under, exch, expiry, interval, "key", days)
        ok2, resp2, code2 = ic.get_default_symbols(under, exch, expiry, "key")
        cases.append({"name": name, "underlying": under, "exchange": exch, "expiry": expiry, "interval": interval, "days": days, "strikes": list(strikes), "history": hist, "quote": list(quote),
                      "calls": sorted(calls, key=lambda c: (c["symbol"], c["exchange"])), "ok": ok, "code": code, "response": resp, "defaults": {"ok": ok2, "code": code2, "response": resp2}})

    strikes = [23700 + 50 * i for i in range(0, 14)]
    run("one-day", strikes)
    run("three-days", strikes, days=3)
    run("two-days-ms", strikes, days=2, ms=True)
    run("expiry-tomorrow", strikes, expiry="12NOV26")
    run("expired", strikes, expiry="10NOV26")
    run("expiry-day-late-candles", strikes, expiry="11NOV26", n=20, tshift=330)
    run("zero-priced-candles", strikes, zero={(0, 3), (1, 5)})
    run("at-intrinsic-candles", strikes, junk={(0, 2), (0, 9)})
    run("option-offset-timestamps", strikes, shift=1)
    run("leg-history-missing", strikes, ce_hist={"ok": False, "resp": {"message": "x"}})
    run("both-legs-missing", strikes, ce_hist={"ok": False, "resp": {}}, pe_hist={"ok": True, "resp": {"data": []}})
    run("underlying-fails", strikes, u_hist={"ok": False, "resp": {"message": "token expired"}})
    run("underlying-fails-no-message", strikes, u_hist={"ok": False, "resp": {}})
    run("underlying-empty", strikes, u_hist=[])
    run("underlying-no-timestamp", strikes, u_hist=[{"close": 1}])
    run("quote-fails", strikes, quote=("fail", 0, "Broker busy"))
    run("quote-fails-silently", strikes, quote=("fail", 0))
    run("quote-zero", strikes, quote=("ok", 0))
    run("no-strikes", [])
    run("banknifty", [51000 + 100 * i for i in range(10)], under="BANKNIFTY", centre=51500.0, quote=("ok", 51520.0))
    run("sensex", [79000 + 100 * i for i in range(14)], under="SENSEX", exch="BSE_INDEX", centre=79500.0, quote=("ok", 79540.0))
    return {"now": NOW.isoformat(), "cases": cases}


def record_chain_greeks():
    import random

    from opengreeks import black76

    import services.option_chain_service as oc
    import services.option_greeks_service as g

    g.datetime = Frozen
    cases = []
    rng = random.Random(77)

    def ladder(forward, years, rate, step=50, count=14, vol=0.14, zero_every=0, junk=False):
        strikes = [24000 + step * i for i in range(-count, count + 1)]
        ce, pe = [], []
        for i, k in enumerate(strikes):
            sigma = vol + 0.002 * abs(i - count)
            ce.append(round(black76.black("c", forward, k, years, rate / 100, sigma), 2))
            pe.append(round(black76.black("p", forward, k, years, rate / 100, sigma), 2))
        ce = [0 if x != x else x for x in ce]
        pe = [0 if x != x else x for x in pe]  # a price the generator could not make (deep out of the money, very short dated) is no price
        if zero_every:
            for i in range(0, len(strikes), zero_every):
                ce[i] = 0
                pe[(i + 1) % len(strikes)] = None
        if junk:
            ce[0] = round(max(forward - strikes[0], 0), 2)          # exactly intrinsic
            pe[-1] = round(max(strikes[-1] - forward, 0) + 0.004, 2)  # a hair over intrinsic
            ce[3] = 1e6                                             # impossible
            pe[5] = 0.01
        return strikes, ce, pe

    def run(name, forward, years, rate, **kw):
        strikes, ce, pe = ladder(forward, years, rate or 0, **kw)
        out_ce, out_pe = g.calculate_chain_greeks(strikes, ce, pe, forward, years, rate)
        cases.append({"name": name, "strikes": strikes, "ce": ce, "pe": pe, "forward": forward, "years": years, "rate": rate, "out_ce": out_ce, "out_pe": out_pe})

    run("nifty-15-days", 24110.5, 15.2292 / 365, None)
    run("with-rate", 24110.5, 0.25, 6.5)
    run("short-dated", 24050.0, 1.2292 / 365, 0)
    run("very-short", 24050.0, 0.0001, None)
    run("gaps-and-zeros", 24110.5, 0.08, None, zero_every=4)
    run("junk-prices", 24110.5, 0.05, None, junk=True)
    run("wide-step", 52100.0, 0.04, None, step=200)
    run("expired", 24050.0, 0.0, None)
    run("no-forward", 0.0, 0.05, None)
    cases.append({"name": "empty", "strikes": [], "ce": [], "pe": [], "forward": 24000.0, "years": 0.05, "rate": None, "out_ce": [], "out_pe": []})

    chains = []
    for label, atm, rows in [
        ("priced", 24050, [{"strike": 24000, "ce": {"ltp": 120}, "pe": {"ltp": 55}}, {"strike": 24050, "ce": {"ltp": 95.5}, "pe": {"ltp": 80.25}}, {"strike": 24100, "ce": {"ltp": 70}, "pe": {"ltp": 110}}]),
        ("unpriced-atm", 24050, [{"strike": 24050, "ce": {"ltp": 0}, "pe": {"ltp": 80}}]),
        ("atm-missing", 24050, [{"strike": 24000, "ce": {"ltp": 120}, "pe": {"ltp": 55}}]),
        ("legs-missing", 24050, [{"strike": 24050, "ce": None, "pe": {"ltp": 80}}]),
    ]:
        chains.append({"name": label, "atm": atm, "ltp": 24060.5, "chain": rows, "forward": oc._forward_from_chain(rows, atm, 24060.5)})

    from datetime import datetime as dt

    expired = []
    for sym, exch, spot, price, rate in [("NIFTY10NOV2624000CE", "NFO", 24100.0, 150.0, None), ("NIFTY10NOV2624300PE", "NFO", 24100.0, 260.5, 7.0), ("NIFTY10NOV2624000PE", "NFO", 24100.0, 0.5, None)]:
        base, expiry, strike, kind = g.parse_option_symbol(sym, exch)
        expired.append({"symbol": sym, "exchange": exch, "base": base, "expiry": expiry.isoformat(), "strike": strike, "type": kind, "spot": spot, "price": price, "rate": rate,
                        "response": g._expired_option_greeks_response(sym, exch, base, expiry, strike, kind, spot, price, rate)})
    return {"now": NOW.isoformat(), "cases": cases, "forwards": chains, "expired": expired}


def record_strategy_chart():
    return _strategy_chart_cases("strategy_chart")


def record_multi_strike_oi():
    return _strategy_chart_cases("multi_strike_oi")


RECORDERS = {"chain_greeks": record_chain_greeks, "iv_chart": record_iv_chart, "sector_rotation": record_sector_rotation, "strategy_pnl": record_strategy_pnl, "arbitrage": record_arbitrage, "custom_straddle": record_custom_straddle, "straddle_chart": record_straddle_chart, "strategy_chart": record_strategy_chart, "multi_strike_oi": record_multi_strike_oi, "oi_profile": record_oi_profile, "gamma_density": record_gamma_density, "strike_profile": record_strike_profile, "vol_surface": record_vol_surface, "greeks": record_greeks, "gex": record_gex, "iv_smile": record_iv_smile, "oi": record_oi}

if __name__ == "__main__":
    out = {name: fn() for name, fn in RECORDERS.items()}
    (Path(__file__).with_name("tools.fixtures.json")).write_text(json.dumps(out, indent=1, default=str))
    for name, v in out.items():
        print(name, len(v["cases"]), "cases;", sum(1 for c in v["cases"] if c.get("ok", c.get("oi", {}).get("ok"))), "ok")
