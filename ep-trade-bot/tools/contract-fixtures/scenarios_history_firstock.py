"""Firstock history: POST /timePriceSeries with epoch-stamped candles. One-minute candles a day at a time; other intervals in chunks of days that grow with the interval; daily candles stamped 09:15 of their date in UTC terms."""

import scenarios_firstock as s
from scenarios_firstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V  # noqa: F401

BROKER = "firstock"
SYMBOLS = s.SYMBOLS + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX")]
HP = f"POST {V}/timePriceSeries"
T = 1793591100  # 2026-11-02 09:15 IST
DAY = 86400


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def c(epoch, o="800.00", h="802.00", l="799.00", cl="801.00", v="1000", **extra):
    d = {"epochTime": epoch, "open": o, "high": h, "low": l, "close": cl, "volume": v}
    d.update(extra)
    return d


def ok(*candles):
    return {"body": {"status": "success", "data": list(candles)}}


FAILED = {"body": {"status": "failed", "message": "No data"}}
INTRA = ok(c(T), c(T + 300, "801", "803", "800", "802", "2000"), c(T + 600, "802", "804", "801", "803", "3000"))
DAILY = ok(c(1793232000), c(1793318400, "801", "803", "800", "802", "2000"), c(1793577600, "802", "804", "801", "803", "3000"))
SCENARIOS = [
    hist("history_5m_in_one_call", {}, {HP: INTRA}),
    hist("history_3m", {"interval": "3m"}, {HP: INTRA}),
    hist("history_10m", {"interval": "10m"}, {HP: INTRA}),
    hist("history_15m", {"interval": "15m"}, {HP: INTRA}),
    hist("history_30m", {"interval": "30m"}, {HP: INTRA}),
    hist("history_1h", {"interval": "1h"}, {HP: INTRA}),
    hist("history_2h", {"interval": "2h"}, {HP: INTRA}),
    hist("history_4h", {"interval": "4h"}, {HP: INTRA}),
    hist("history_1m_is_a_call_a_day", {"interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-04"}, {HP: [INTRA, INTRA, ok(c(T + 2 * DAY))]}),
    hist("history_1m_skips_a_day_that_fails", {"interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-04"}, {HP: [INTRA, FAILED, ok(c(T + 2 * DAY))]}),
    hist("history_daily_in_one_call", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {HP: DAILY}),
    hist("history_daily_candles_are_stamped_at_nine_fifteen", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {HP: ok(c(1793232000 + 3600), c(1793318400 + 86399))}),
    hist("history_daily_over_thirty_days_goes_in_chunks", {"interval": "D", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: [ok(c(1788220800)), ok(c(1790812800)), ok(c(1793404800))]}),
    hist("history_5m_over_three_days_goes_in_chunks", {"start_date": "2026-11-02", "end_date": "2026-11-09"}, {HP: [ok(c(T)), ok(c(T + 3 * DAY)), ok(c(T + 6 * DAY))]}),
    hist("history_a_failed_chunk_is_left_out", {"start_date": "2026-11-02", "end_date": "2026-11-09"}, {HP: [ok(c(T)), FAILED, ok(c(T + 6 * DAY))]}),
    hist("history_every_chunk_failing_is_empty", {"start_date": "2026-11-02", "end_date": "2026-11-09"}, {HP: FAILED}),
    hist("history_overlapping_chunks_are_not_repeated", {"start_date": "2026-11-02", "end_date": "2026-11-09"}, {HP: [ok(c(T), c(T + 300)), ok(c(T + 300), c(T + 600)), ok(c(T + 900))]}),
    hist("history_one_call_that_fails_is_empty", {}, {HP: FAILED}),
    hist("history_one_call_with_a_http_error_is_empty", {}, {HP: {"status": 500, "body": {"x": 1}}}),
    hist("history_no_candles_is_empty", {}, {HP: ok()}),
    hist("history_unsorted_rows_are_sorted", {}, {HP: ok(c(T + 600), c(T), c(T + 300))}),
    hist("history_repeated_rows_in_one_call_are_kept", {}, {HP: ok(c(T), c(T))}),
    hist("history_a_volume_that_is_not_a_whole_number_skips_the_candle", {}, {HP: ok(c(T, v="12.5"), c(T + 300, v="1,000"), c(T + 600, v="7"))}),
    hist("history_a_missing_volume_is_zero", {}, {HP: ok({"epochTime": T, "open": "1", "high": "2", "low": "0.5", "close": "1.5"})}),
    hist("history_a_candle_with_no_time_is_skipped", {}, {HP: ok({"open": "1", "high": "2", "low": "0.5", "close": "1.5", "volume": "5"}, c(T + 300))}),
    hist("history_text_numbers_and_a_numeric_volume", {}, {HP: ok(c(T, "800.5", "802", "799", "801", 1000), c(T + 300, v=1500.9))}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {HP: DAILY}),
    hist("history_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "interval": "30m"}, {HP: INTRA}),
    hist("history_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "15m"}, {HP: INTRA}),
    hist("history_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {HP: INTRA}),
    hist("history_the_ampersand_in_a_name_is_not_escaped_here", {"symbol": "M&M"}, {HP: INTRA}),
    hist("history_a_rate_limit_is_tried_again", {}, {HP: [{"status": 429, "body": "Rate limit exceeded"}, INTRA]}),
    hist("history_interval_firstock_lacks", {"interval": "45m"}, {HP: INTRA}),
    hist("history_weekly_is_not_offered", {"interval": "W", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: DAILY}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {HP: INTRA}),
]


def patch_clients(shared):
    """The history call builds a client of its own (a long wait for the broker); every client it builds is the recorder's, and is left open for the next call."""
    import httpx

    class _Borrowed:
        def __enter__(self):
            return shared

        def __exit__(self, *exc):
            return False

    httpx.Client = lambda *args, **kwargs: _Borrowed()


def patch():
    import importlib

    data = importlib.import_module("broker.firstock.api.data")
    data.time.sleep = lambda seconds: None
