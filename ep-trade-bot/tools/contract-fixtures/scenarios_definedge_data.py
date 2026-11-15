"""Definedge quotes, depth and several quotes: GET /quotes/{exchange}/{token} on the trading host (an index is asked for under its cash exchange) and, for a derivative, its open interest from the last one-minute candle of the data host's history, read once a minute at most."""

from datetime import datetime as _datetime

import scenarios_definedge as s
from scenarios_definedge import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V, req  # noqa: F401

BROKER = "definedge"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
EXTRA = [s._sym("BANKNIFTY", "Nifty Bank", "NSE_INDEX", 26009, "BANKNIFTY", 1, "INDEX"), s._sym("SENSEX", "SENSEX", "BSE_INDEX", 1, "SENSEX", 1, "INDEX")]
MANY = [s._sym(f"STK{i:02d}", f"STK{i:02d}-EQ", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 24)]
FUTS = [s._sym(f"FUT{i}", f"FUT{i}", "NFO", 49500 + i, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1) for i in range(1, 10)]
SYMBOLS = s.SYMBOLS + EXTRA + MANY + FUTS
OI = "GET /sds/history"


def qk(exchange, token):
    return f"GET {V}/quotes/{exchange}/{token}"


def oi_key(segment, token):
    return f"{OI}/{segment}/{token}/minute/091120260915/131120261100"


def quote(ltp="805.45", **extra):
    d = {"status": "SUCCESS", "ltp": ltp, "day_open": "801.00", "day_high": "809.50", "day_low": "799.00", "volume": "1234567", "last_traded_qty": "25"}
    for i in range(1, 6):
        d[f"best_bid_price{i}"] = f"{805.4 - 0.05 * (i - 1):.2f}"
        d[f"best_bid_qty{i}"] = str(100 * i)
        d[f"best_ask_price{i}"] = f"{805.5 + 0.05 * (i - 1):.2f}"
        d[f"best_ask_qty{i}"] = str(110 * i)
    d.update(extra)
    return {"body": d}


def one(name, path, symbol, exchange, broker):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


OI_ROW = {"text": "13112026,1059,24000,24010,23990,24005,100,4200\n13112026,1100,24005,24020,24000,24015,200,4300.7\n"}
OI_CSV = {"status": 200, "body": "x"}
FUT_Q = qk("NFO", 49500)
FUT = quote("24005.5")
SCENARIOS = [
    req("auth_invalid_key", "/api/v1/quotes", {"apikey": "wrong-key", "symbol": "SBIN", "exchange": "NSE"}),
    one("quotes_equity", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): quote()}),
    one("quotes_bse", "/api/v1/quotes", "TATAMOTORS", "BSE", {qk("BSE", 500570): quote("500.5")}),
    one("quotes_index", "/api/v1/quotes", "NIFTY", "NSE_INDEX", {qk("NSE", 26000): quote("24100.25")}),
    one("quotes_bse_index", "/api/v1/quotes", "SENSEX", "BSE_INDEX", {qk("BSE", 1): quote("80000.5")}),
    one("quotes_text_numbers", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): quote(volume="16131960", day_open="801")}),
    one("quotes_no_volume", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): quote(volume=None)}),
    one("quotes_no_open_uses_the_last_price", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): {"body": {"status": "SUCCESS", "ltp": "805.45"}}}),
    one("quotes_status_not_success", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): {"body": {"status": "ERROR", "message": "bad"}}}),
    one("quotes_http_error", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): {"status": 500, "body": "oops"}}),
    one("quotes_rate_limited_is_tried_again", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): [{"status": 429, "body": {"message": "slow down"}}, quote()]}),
    one("quotes_rate_limited_every_time", "/api/v1/quotes", "SBIN", "NSE", {qk("NSE", 3045): {"status": 429, "body": {"message": "slow down"}}}),
    one("quotes_future_with_open_interest", "/api/v1/quotes", "FUT1", "NFO", {qk("NFO", 49501): FUT, oi_key("NFO", 49501): OI_ROW}),
    one("quotes_future_without_history_has_no_open_interest", "/api/v1/quotes", "FUT2", "NFO", {qk("NFO", 49502): FUT, oi_key("NFO", 49502): {"status": 500, "body": "down"}}),
    one("quotes_future_with_an_empty_history", "/api/v1/quotes", "FUT3", "NFO", {qk("NFO", 49503): FUT, oi_key("NFO", 49503): {"text": ""}}),
    one("quotes_future_with_a_short_row", "/api/v1/quotes", "FUT4", "NFO", {qk("NFO", 49504): FUT, oi_key("NFO", 49504): {"text": "13112026,1100,1,2,3,4"}}),
    one("quotes_mcx_future", "/api/v1/quotes", "GOLDM25NOV26FUT", "MCX", {qk("MCX", 80001): quote("75100"), oi_key("MCX", 80001): OI_ROW}),
    one("quotes_unknown_symbol", "/api/v1/quotes", "NOPE", "NSE", {}),
    one("depth_equity", "/api/v1/depth", "SBIN", "NSE", {qk("NSE", 3045): quote()}),
    one("depth_missing_levels_are_zero", "/api/v1/depth", "SBIN", "NSE", {qk("NSE", 3045): quote(best_bid_price3=None, best_bid_qty3=None, best_ask_price5="0", best_ask_qty5="0")}),
    one("depth_index", "/api/v1/depth", "NIFTY", "NSE_INDEX", {qk("NSE", 26000): quote("24100.25")}),
    one("depth_future_with_open_interest", "/api/v1/depth", "FUT5", "NFO", {qk("NFO", 49505): FUT, oi_key("NFO", 49505): OI_ROW}),
    one("depth_no_last_quantity", "/api/v1/depth", "SBIN", "NSE", {qk("NSE", 3045): quote(last_traded_qty=None)}),
    one("depth_status_not_success", "/api/v1/depth", "SBIN", "NSE", {qk("NSE", 3045): {"body": {"status": "ERROR"}}}),
    one("depth_http_error", "/api/v1/depth", "SBIN", "NSE", {qk("NSE", 3045): {"status": 500, "body": "oops"}}),
    one("depth_unknown_symbol", "/api/v1/depth", "NOPE", "NSE", {}),
    req("multiquotes_equities", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {qk("NSE", 3045): quote(), qk("NSE", 1594): quote("1500.5")}),
    req("multiquotes_different_exchanges", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("FUT8", "NFO"), ("TATAMOTORS", "BSE")), {qk("NSE", 3045): quote(), qk("NFO", 49508): FUT, qk("BSE", 500570): quote("500.5"), oi_key("NFO", 49508): OI_ROW}),
    req("multiquotes_indices_are_asked_as_the_cash_exchange", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")), {qk("NSE", 26000): quote("24100.25"), qk("BSE", 1): quote("80000.5")}),
    req("multiquotes_a_failed_quote_is_an_error_entry", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {qk("NSE", 3045): quote(), qk("NSE", 1594): {"status": 500, "body": "oops"}}),
    req("multiquotes_a_quote_with_another_status_is_an_error_entry", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {qk("NSE", 3045): quote(), qk("NSE", 1594): {"body": {"status": "ERROR"}}}),
    req("multiquotes_unknown_symbols_among_known", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE")), {qk("NSE", 3045): quote()}),
    req("multiquotes_future_open_interest_comes_from_history", "/api/v1/multiquotes", legs(("FUT6", "NFO"),), {qk("NFO", 49506): FUT, oi_key("NFO", 49506): OI_ROW}),
    req("multiquotes_future_open_interest_fails_to_zero", "/api/v1/multiquotes", legs(("FUT7", "NFO"),), {qk("NFO", 49507): FUT, oi_key("NFO", 49507): {"status": 500, "body": "down"}}),
    req("multiquotes_more_than_twenty_go_in_batches", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 24)]), {qk("NSE", 9000 + i): quote(str(100 + i)) for i in range(1, 24)}),
]


def patch():
    import importlib

    limiter = importlib.import_module("broker.definedge.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
    data = importlib.import_module("broker.definedge.api.data")
    data.USE_ASYNC = False
    data.time.sleep = lambda *a, **k: None
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed.replace(tzinfo=None) if tz is None else fixed.astimezone(tz)

    data.datetime = _Frozen
    data._oi_cache.clear()
