"""5paisa history: GET /V2/historical/{exchange}/{type}/{token}/{interval}, a hundred days a call for daily candles and thirty for the rest; a chunk that fails is left out."""

import scenarios_fivepaisa as s
from scenarios_fivepaisa import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401

BROKER = "fivepaisa"
SYMBOLS = s.SYMBOLS + [s._sym("NIFTYBEES", "NIFTYBEES_EQ", "NSE", 10576, "NIFTY BEES"), s._sym("SENSEX", "SENSEX", "BSE_INDEX", 999901, "SENSEX", 1, "INDEX")]
H = "GET /V2/historical"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def candle(stamp, o=800.0, h=802.0, l=799.0, c=801.0, v=1000):
    return [stamp, o, h, l, c, v]


def ok(*candles):
    return {"body": {"status": "success", "data": {"candles": list(candles)}}}


def path(exch="N", typ="C", token=3045, interval="5m"):
    return f"GET /V2/historical/{exch}/{typ}/{token}/{interval}"


INTRA = ok(candle("2026-11-02T09:15:00"), candle("2026-11-02T09:20:00", 801, 803, 800, 802, 2000), candle("2026-11-02T09:25:00", 802, 804, 801, 803, 3000))
DAILY = ok(candle("2026-10-29T00:00:00"), candle("2026-10-30T00:00:00", 801, 803, 800, 802, 2000), candle("2026-11-02T00:00:00", 802, 804, 801, 803, 3000))
SCENARIOS = [
    hist("history_5m_equity", {}, {path(): INTRA}),
    hist("history_1m", {"interval": "1m"}, {path(interval="1m"): INTRA}),
    hist("history_10m", {"interval": "10m"}, {path(interval="10m"): INTRA}),
    hist("history_15m", {"interval": "15m"}, {path(interval="15m"): INTRA}),
    hist("history_30m", {"interval": "30m"}, {path(interval="30m"): INTRA}),
    hist("history_1h_is_sixty_minutes", {"interval": "1h"}, {path(interval="60m"): INTRA}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {path(interval="1d"): DAILY}),
    hist("history_daily_drops_a_flat_or_empty_day", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {path(interval="1d"): ok(candle("2026-10-29T00:00:00", 800, 800, 800, 800, 5000), candle("2026-10-30T00:00:00", 801, 803, 800, 802, 0), candle("2026-10-31T00:00:00", 0, 0, 0, 0, 0), candle("2026-11-02T00:00:00"))}),
    hist("history_intraday_keeps_a_quiet_minute", {"interval": "1m"}, {path(interval="1m"): ok(candle("2026-11-02T09:15:00", 800, 800, 800, 800, 0), candle("2026-11-02T09:16:00", 0, 0, 0, 0, 0), candle("2026-11-02T09:17:00"))}),
    hist("history_candles_outside_the_range_are_dropped", {}, {path(): ok(candle("2026-11-01T15:25:00"), candle("2026-11-02T09:15:00"), candle("2026-11-03T09:15:00"))}),
    hist("history_a_short_candle_is_skipped", {}, {path(): ok([ "2026-11-02T09:15:00", 1, 2], candle("2026-11-02T09:20:00"))}),
    hist("history_a_candle_with_a_bad_time_is_skipped", {}, {path(): ok(candle("not a time"), candle("2026-11-02T09:20:00"))}),
    hist("history_text_numbers", {}, {path(): ok(["2026-11-02T09:15:00", "800.5", "802", "799", "801", "1000"])}),
    hist("history_unsorted_and_repeated_rows", {}, {path(): ok(candle("2026-11-02T09:25:00"), candle("2026-11-02T09:15:00"), candle("2026-11-02T09:20:00"), candle("2026-11-02T09:15:00", 1, 2, 0.5, 1.5, 5))}),
    hist("history_a_long_intraday_range_goes_in_thirty_day_chunks", {"interval": "15m", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {path(interval="15m"): [ok(candle("2026-09-01T09:15:00")), ok(candle("2026-10-01T09:15:00")), ok(candle("2026-10-31T09:15:00"))]}),
    hist("history_a_long_daily_range_goes_in_hundred_day_chunks", {"interval": "D", "start_date": "2026-01-01", "end_date": "2026-11-02"}, {path(interval="1d"): [ok(candle("2026-01-05T00:00:00")), ok(candle("2026-04-15T00:00:00")), ok(candle("2026-08-01T00:00:00"))]}),
    hist("history_a_failed_chunk_is_left_out", {"interval": "15m", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {path(interval="15m"): [ok(candle("2026-09-01T09:15:00")), {"status": 500, "body": {"x": 1}}, ok(candle("2026-10-31T09:15:00"))]}),
    hist("history_a_chunk_with_an_error_status_is_left_out", {}, {path(): {"body": {"status": "error", "message": "bad"}}}),
    hist("history_future_range_answered_with_the_latest_candle", {"start_date": "2026-12-01", "end_date": "2026-12-02"}, {path(): ok(candle("2026-11-02T09:15:00"))}),
    hist("history_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "1m"}, {path("M", "D", 80001, "1m"): INTRA}),
    hist("history_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {path("N", "D", 49500, "1m"): INTRA}),
    hist("history_currency", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS", "interval": "15m"}, {path("N", "U", 90001, "15m"): INTRA}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {path("B", "C", 500570): INTRA}),
    hist("history_index_keeps_zero_volume", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {path("N", "C", 999920000, "1d"): ok(candle("2026-10-29T00:00:00", 24000, 24100, 23900, 24050, 0), candle("2026-10-30T00:00:00", 0, 0, 0, 0, 0))}),
    hist("history_bse_index_intraday", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "interval": "30m"}, {path("B", "C", 999901, "30m"): ok(candle("2026-11-02T09:15:00", 79000, 79100, 78900, 79050, 0))}),
    hist("history_a_share_with_nifty_in_its_name", {"symbol": "NIFTYBEES", "exchange": "NSE", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {path("N", "C", 10576, "1d"): DAILY}),
    hist("history_nothing_at_all_is_empty", {}, {path(): ok()}),
    hist("history_interval_5paisa_lacks", {"interval": "3m"}, {path(): INTRA}),
    hist("history_weekly_is_not_offered", {"interval": "W", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {path(interval="1d"): DAILY}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {path(): INTRA}),
]


def patch():
    pass
