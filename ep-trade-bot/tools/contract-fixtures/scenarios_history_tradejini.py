"""Tradejini history: one GET of chart interval data with the symbol id, the interval and epoch seconds (the first day from 09:15 India time, the last day to 23:59:59); the answer is an envelope whose d.bars are the candles, as objects or as lists. There is no chunking, and a failure of any kind is read as no candles."""

import scenarios_tradejini as s
from scenarios_tradejini import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "tradejini"
SYMBOLS = s.SYMBOLS
H = "GET /v2/api/mkt-data/chart/interval-data"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def bars(*rows, status="ok"):
    return {"body": {"s": status, "d": {"bars": list(rows)}}}


def bar(t, o=800, h=802, low=799, c=801, v=1000):
    return {"time": t, "open": o, "high": h, "low": low, "close": c, "volume": v}


T0 = 1793594700  # 2026-11-02 09:15 IST
SCENARIOS = [
    hist("history_one_minute", {}, {H: bars(*[bar(T0 + 60 * i, 800 + i) for i in range(5)])}),
    hist("history_five_minutes", {"interval": "5m"}, {H: bars(*[bar(T0 + 300 * i) for i in range(3)])}),
    hist("history_fifteen_minutes", {"interval": "15m"}, {H: bars(bar(T0))}),
    hist("history_thirty_minutes", {"interval": "30m"}, {H: bars(bar(T0))}),
    hist("history_an_interval_with_no_translation_is_sent_as_it_is", {"interval": "1h"}, {H: bars(bar(T0))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {H: bars(bar(T0 - 86400 * 3), bar(T0), bar(T0 + 86400))}),
    hist("history_over_several_days_is_one_call", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {H: bars(bar(T0 - 86400 * 30), bar(T0))}),
    hist("history_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {H: bars(bar(T0))}),
    hist("history_a_derivative", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {H: bars(bar(T0))}),
    hist("history_a_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {H: bars(bar(T0))}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {H: bars(bar(T0))}),
    hist("history_a_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {H: bars(bar(T0))}),
    hist("history_times_in_milliseconds_are_cut_to_seconds", {}, {H: bars(bar(T0 * 1000), bar(T0 * 1000 + 60000))}),
    hist("history_rows_out_of_order_are_sorted", {}, {H: bars(bar(T0 + 60, 801), bar(T0, 800))}),
    hist("history_duplicate_times_are_kept", {}, {H: bars(bar(T0, 800), bar(T0, 900), bar(T0 + 60, 801))}),
    hist("history_rows_as_lists", {}, {H: bars([T0, 800, 802, 799, 801, 1000], [T0 + 60, 801, 803, 800, 802])}),
    hist("history_rows_as_lists_with_five_cells_have_no_volume", {}, {H: bars([T0, 800, 802, 799, 801])}),
    hist("history_a_list_row_with_too_few_cells_is_skipped", {}, {H: bars([T0, 800, 802], bar(T0 + 60))}),
    hist("history_a_row_that_is_not_an_object_or_list_is_skipped", {}, {H: bars("junk", bar(T0))}),
    hist("history_a_row_missing_fields_reads_them_as_zero", {}, {H: bars({"time": T0})}),
    hist("history_a_row_with_a_price_that_is_not_a_number_is_skipped", {}, {H: bars(bar(T0, "x"), bar(T0 + 60))}),
    hist("history_numbers_as_text", {}, {H: bars({"time": str(T0), "open": "800.5", "high": "802", "low": "799", "close": "801", "volume": "1200"})}),
    hist("history_a_volume_with_decimals_is_cut", {}, {H: bars(bar(T0, v=1200.9))}),
    hist("history_a_null_volume_is_zero", {}, {H: bars(bar(T0, v=None))}),
    hist("history_no_data", {}, {H: {"body": {"s": "no-data"}}}),
    hist("history_no_bars", {}, {H: bars()}),
    hist("history_bars_that_are_null", {}, {H: {"body": {"s": "ok", "d": {"bars": None}}}}),
    hist("history_d_that_is_null", {}, {H: {"body": {"s": "ok", "d": None}}}),
    hist("history_error_with_a_message", {}, {H: {"body": {"s": "error", "msg": "Invalid symbol"}}}),
    hist("history_http_error", {}, {H: {"status": 500, "body": {"s": "error", "msg": "down"}}}),
    hist("history_http_401", {}, {H: {"status": 401, "body": {"s": "error"}}}),
    hist("history_not_json", {}, {H: {"status": 200, "body": "<html>"}}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
]
