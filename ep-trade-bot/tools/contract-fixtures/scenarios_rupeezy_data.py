"""Rupeezy quotes, depth and several quotes (GET /v2/data/quotes, mode full)."""

import scenarios_rupeezy as s
from scenarios_rupeezy import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, B, SYMBOLS, req  # noqa: F401

BROKER = "rupeezy"
QB = f"GET {B}/data/quotes"


def q(ltp=805.45, **extra):
    d = {"last_trade_price": ltp, "open_price": 802.45, "high_price": 809.45, "low_price": 800.45, "close_price": 803.95, "volume": 1234567, "open_interest": 1500,
         "last_trade_quantity": 25, "total_buy_quantity": 5000, "total_sell_quantity": 4000,
         "depth": {"buy": [{"price": 805.4 - 0.05 * i, "quantity": 100 * (i + 1), "orders": i + 1} for i in range(5)], "sell": [{"price": 805.5 + 0.05 * i, "quantity": 110 * (i + 1), "orders": 2 * (i + 1)} for i in range(5)]}}
    d.update(extra)
    return d


def reply(**quotes):
    return {"body": {"status": "success", "data": quotes}}


SBIN = reply(**{"NSE:SBIN": q()})
NO_DEPTH = reply(**{"NSE:SBIN": q(depth={})})
SHORT_DEPTH = reply(**{"NSE:SBIN": q(depth={"buy": [{"price": 805.4, "quantity": 100}, {}], "sell": []})})
TEXT_NUMBERS = reply(**{"NSE:SBIN": q(last_trade_price="805.45", volume="1234567", open_interest="", depth={"buy": [{"price": "805.4", "quantity": "100"}], "sell": [{"price": "x", "quantity": None}]})})
EMPTY = {"body": {"status": "success", "data": {}}}
REFUSED = {"status": 401, "body": {"status": "error", "message": "Invalid token"}}
NIFTY_FUT = reply(**{"NSE:NIFTY25NOV26FUT": q(24005.5)})
MULTI = reply(**{"NSE:SBIN": q(), "NSE:INFY": q(1499.5), "NSE:NIFTY25NOV26FUT": q(24005.5)})

SCENARIOS = [
    req("quotes_equity", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN}),
    req("quotes_futures", "/api/v1/quotes", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {QB: NIFTY_FUT}),
    req("quotes_index", "/api/v1/quotes", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {QB: reply(**{"NSE:NIFTY": q(24100.25)})}),
    req("quotes_without_depth", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_DEPTH}),
    req("quotes_short_depth", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT_DEPTH}),
    req("quotes_numbers_as_text", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: TEXT_NUMBERS}),
    req("quotes_no_quote_for_the_instrument", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: EMPTY}),
    req("quotes_refused", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED}),
    req("quotes_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}, {QB: SBIN}),
    req("depth_five_levels", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN}),
    req("depth_short_book", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT_DEPTH}),
    req("depth_no_book", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_DEPTH}),
    req("depth_refused", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED}),
    req("multiquotes", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}]}, {QB: MULTI}),
    req("multiquotes_one_instrument_missing_from_the_answer", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}]}, {QB: SBIN}),
    req("multiquotes_refused", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, {QB: REFUSED}),
    req("multiquotes_the_same_instrument_twice", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {QB: SBIN}),
]
