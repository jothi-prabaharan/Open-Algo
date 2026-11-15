"""IndMoney history: GET /market/historical/<interval> with the scrip code and the range as epoch milliseconds, in chunks of 7 days for minute candles, 14 for hourly ones and 365 for daily and longer. Candles are objects (ts, o, h, l, c, v) or lists (milliseconds, o, h, l, c, v). A range of one day makes no chunk at all."""

import scenarios_indmoney as s
from scenarios_indmoney import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, ok  # noqa: F401

BROKER = "indmoney"
SYMBOLS = list(s.SYMBOLS) + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", 51, "SENSEX", 1, "INDEX")]
H = "GET /market/historical/"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-03"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def candle(ts, o=800, h=802, low=799, c=801, v=1000):
    return {"ts": ts, "o": o, "h": h, "l": low, "c": c, "v": v}


def candles(code, *rows):
    return ok({code: {"candles": list(rows)}})


T0 = 1793594700  # 2026-11-02 10:15 IST
N = "NSE_3045"
SCENARIOS = [
    hist("history_one_minute", {}, {H + "1minute": candles(N, *[candle(T0 + 60 * i, 800 + i) for i in range(5)])}),
    hist("history_candles_as_lists", {}, {H + "1minute": candles(N, [T0 * 1000, 800, 802, 799, 801, 1000], [(T0 + 60) * 1000, 801, 803, 800, 802, 0])}),
    hist("history_candles_not_nested_under_the_scrip", {}, {H + "1minute": ok({"candles": [candle(T0)]})}),
    hist("history_candles_as_a_list_in_data", {}, {H + "1minute": ok([candle(T0), candle(T0 + 60)])}),
    hist("history_five_minutes", {"interval": "5m"}, {H + "5minute": candles(N, candle(T0))}),
    hist("history_two_minutes", {"interval": "2m"}, {H + "2minute": candles(N, candle(T0))}),
    hist("history_thirty_minutes", {"interval": "30m"}, {H + "30minute": candles(N, candle(T0))}),
    hist("history_one_hour", {"interval": "1h"}, {H + "60minute": candles(N, candle(T0))}),
    hist("history_four_hours", {"interval": "4h"}, {H + "240minute": candles(N, candle(T0))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-28", "end_date": "2026-11-03"}, {H + "1day": candles(N, candle(T0 - 86400 * 3), candle(T0))}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-08-01", "end_date": "2026-11-03"}, {H + "1week": candles(N, candle(T0 - 86400 * 7))}),
    hist("history_monthly", {"interval": "M", "start_date": "2026-01-01", "end_date": "2026-11-03"}, {H + "1month": candles(N, candle(T0 - 86400 * 30))}),
    hist("history_a_range_of_one_day_makes_no_request", {"start_date": "2026-11-02", "end_date": "2026-11-02"}, {H + "1minute": candles(N, candle(T0))}),
    hist("history_minute_candles_over_twenty_days_go_in_chunks_of_seven", {"start_date": "2026-10-01", "end_date": "2026-10-20"}, {H + "1minute": [candles(N, candle(T0 - 86400 * 30)), candles(N, candle(T0 - 86400 * 23)), candles(N, candle(T0 - 86400 * 16))]}),
    hist("history_hourly_candles_over_thirty_days_go_in_chunks_of_fourteen", {"interval": "1h", "start_date": "2026-10-01", "end_date": "2026-10-31"}, {H + "60minute": [candles(N, candle(T0 - 86400 * 30)), candles(N, candle(T0 - 86400 * 16)), candles(N, candle(T0 - 86400 * 2))]}),
    hist("history_daily_candles_over_two_years_go_in_chunks_of_a_year", {"interval": "D", "start_date": "2024-11-01", "end_date": "2026-11-01"}, {H + "1day": [candles(N, candle(T0 - 86400 * 600)), candles(N, candle(T0 - 86400 * 240)), candles(N, candle(T0 - 86400 * 1))]}),
    hist("history_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {H + "1minute": candles("NIDX_26000", candle(T0))}),
    hist("history_a_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {H + "1minute": candles("BIDX_51", candle(T0))}),
    hist("history_a_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {H + "1minute": candles("NFO_49500", candle(T0))}),
    hist("history_bse", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {H + "1minute": candles("BSE_500570", candle(T0))}),
    hist("history_mcx", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {H + "1minute": candles("MCX_80001", candle(T0))}),
    hist("history_rows_out_of_order_are_sorted", {}, {H + "1minute": candles(N, candle(T0 + 60, 801), candle(T0, 800))}),
    hist("history_duplicate_times_keep_the_first", {}, {H + "1minute": candles(N, candle(T0, 800), candle(T0, 900), candle(T0 + 60, 801))}),
    hist("history_numbers_as_text", {}, {H + "1minute": candles(N, {"ts": str(T0), "o": "800.5", "h": "802", "l": "799", "c": "801", "v": "1200"})}),
    hist("history_a_candle_that_cannot_be_read_is_skipped", {}, {H + "1minute": candles(N, candle(T0, "x"), candle(T0 + 60))}),
    hist("history_a_candle_of_neither_shape_is_skipped", {}, {H + "1minute": candles(N, "junk", [1, 2, 3], candle(T0))}),
    hist("history_a_volume_with_decimals_as_a_number_is_cut", {}, {H + "1minute": candles(N, candle(T0, v=1200.9))}),
    hist("history_a_null_volume_in_a_list_is_zero", {}, {H + "1minute": candles(N, [T0 * 1000, 800, 802, 799, 801, None])}),
    hist("history_no_candles", {}, {H + "1minute": ok({N: {"candles": []}})}),
    hist("history_null_candles", {}, {H + "1minute": ok({N: {"candles": None}})}),
    hist("history_a_failed_chunk_is_left_out", {"start_date": "2026-10-01", "end_date": "2026-10-20"}, {H + "1minute": [{"status": 500, "body": {"x": 1}}, candles(N, candle(T0 - 86400 * 23)), {"body": {"status": "error", "message": "no data"}}]}),
    hist("history_every_chunk_failed_is_no_candles", {}, {H + "1minute": {"status": 500, "body": {"x": 1}}}),
    hist("history_not_json_is_no_candles", {}, {H + "1minute": {"status": 200, "body": "<html>"}}),
    hist("history_an_api_error_is_no_candles", {}, {H + "1minute": {"body": {"status": "error", "message": "Invalid range", "code": "E9"}}}),
    hist("history_the_success_flag_alone_is_enough", {}, {H + "1minute": {"body": {"success": True, "data": {N: {"candles": [candle(T0)]}}}}}),
    hist("history_an_interval_indmoney_lacks", {"interval": "7m"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
]


def before_each(sc):
    import importlib

    importlib.import_module("broker.indmoney.api.rate_limiter").time.sleep = lambda *a, **k: None
