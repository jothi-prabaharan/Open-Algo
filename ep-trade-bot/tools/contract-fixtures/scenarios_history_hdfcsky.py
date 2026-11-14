"""HDFC Sky history: the chart service serves only one-minute and daily candles (31 and 2000 days a call); every other interval is built from those, binned from midnight UTC (weeks from Monday, months from the first)."""

from datetime import datetime as _datetime

import scenarios_hdfcsky as s
from scenarios_hdfcsky import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, req  # noqa: F401

BROKER = "hdfcsky"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
CB = "GET /oapi/charts-api/charts/v1/fetch-candle"
SYMBOLS = s.SYMBOLS + [
    s._sym("BANKNIFTY", "NIFTY BANK", "NSE_INDEX", 26009, "BANKNIFTY", 1, "INDEX"),
    s._sym("INDIAVIX", "INDIA VIX", "NSE_INDEX", 26017, "INDIAVIX", 1, "INDEX"),
    s._sym("FINNIFTY", "NIFTY FIN SERVICE", "NSE_INDEX", 26037, "FINNIFTY", 1, "INDEX"),
    s._sym("MIDCPNIFTY", "NIFTY MID SELECT", "NSE_INDEX", 26074, "MIDCPNIFTY", 1, "INDEX"),
    s._sym("NIFTYNXT50", "NIFTY NEXT 50", "NSE_INDEX", 26013, "NIFTYNXT50", 1, "INDEX"),
]


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


def reply(*rows, meta=None):
    return {"body": {"meta": meta or {"err_code": "success"}, "data": {"results": [list(r) for r in rows]}}}


def minute(hhmm, o=800.0, h=802.0, l=799.0, c=801.0, v=1000, oi=0, day="02-11-2026"):
    return [o, h, l, c, v, oi, f"{day} {hhmm}", 0]


def mins(start_minutes, count, day="02-11-2026", base=800.0):
    out = []
    for i in range(count):
        t = start_minutes + i
        out.append(minute(f"{t // 60:02d}:{t % 60:02d}", base + i, base + i + 2, base + i - 1, base + i + 1, 1000 + i, 0, day))
    return out


def daily(day, o=800.0, h=810.0, l=795.0, c=805.0, v=100000, oi=0):
    return [o, h, l, c, v, oi, day, v * 3]


M16 = reply(*mins(9 * 60 + 15, 16))
M70 = reply(*mins(9 * 60 + 15, 70))
D3 = reply(daily("29-10-2026", 800, 810, 795, 805, 100), daily("30-10-2026", 805, 815, 800, 810, 200), daily("02-11-2026", 810, 820, 805, 815, 300))
WEEKS = reply(daily("28-10-2026", 1, 2, 0.5, 1.5, 10, 1), daily("30-10-2026", 2, 4, 1, 3, 20, 2), daily("02-11-2026", 3, 5, 2, 4, 30, 3), daily("04-11-2026", 4, 8, 3, 7, 40, 4),
              daily("09-11-2026", 5, 9, 4, 8, 50, 5), daily("12-11-2026", 6, 7, 5, 6, 60, 6))
MONTHS = reply(daily("29-09-2026", 1, 2, 0.5, 1.5, 10), daily("30-09-2026", 2, 3, 1, 2.5, 20), daily("01-10-2026", 3, 4, 2, 3.5, 30), daily("30-10-2026", 4, 6, 3, 5, 40), daily("02-11-2026", 5, 7, 4, 6, 50))
EMPTY = reply()
HTTP_DOWN = {"status": 500, "body": {"error": "down"}}
LIMITED = {"status": 429, "body": {"error": "merchantKeyRateLimit - too many requests"}}
CHART_REFUSED = {"status": 400, "body": {"meta": {"displayMessage": "Allowed Date range exceeded"}}}
CHART_ERR = reply(meta={"err_code": "invalid", "displayMessage": "No data for the symbol"})
CHART_ERR_NOTHING = reply(meta={"err_code": "invalid"})

SCENARIOS = [
    hist("history_1m_equity", {"interval": "1m"}, {CB: M16}),
    hist("history_3m", {"interval": "3m"}, {CB: M16}),
    hist("history_5m", {"interval": "5m"}, {CB: M16}),
    hist("history_10m", {"interval": "10m"}, {CB: M16}),
    hist("history_15m", {"interval": "15m"}, {CB: M16}),
    hist("history_30m", {"interval": "30m"}, {CB: M70}),
    hist("history_1h_bins_start_on_the_utc_hour", {"interval": "1h"}, {CB: M70}),
    hist("history_unsorted_rows_are_sorted", {"interval": "1m"}, {CB: reply(*reversed(mins(9 * 60 + 15, 5)))}),
    hist("history_a_gap_leaves_the_bins_without_candles_out", {"interval": "5m"}, {CB: reply(*mins(9 * 60 + 15, 3), *mins(9 * 60 + 45, 3))}),
    hist("history_null_volume_and_open_interest_are_zero", {"interval": "1m"}, {CB: reply(minute("09:15", v=None, oi=None), minute("09:16", v=7, oi=9))}),
    hist("history_rows_with_eight_columns_are_trimmed", {"interval": "1m"}, {CB: reply(*mins(9 * 60 + 15, 2))}),
    hist("history_a_short_row_is_dropped", {"interval": "1m"}, {CB: reply(minute("09:15"), [1, 2, 3], minute("09:17"))}),
    hist("history_a_row_with_no_readable_time_is_dropped", {"interval": "1m"}, {CB: reply(minute("09:15"), [1, 2, 3, 4, 5, 6, "not a time", 0], minute("09:17"))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {CB: D3}),
    hist("history_daily_is_stamped_at_india_midnight", {"interval": "D", "symbol": "INFY", "start_date": "2026-11-02", "end_date": "2026-11-02"}, {CB: reply(daily("02-11-2026"))}),
    hist("history_weekly_bins_start_on_monday", {"interval": "W", "start_date": "2026-10-28", "end_date": "2026-11-12"}, {CB: WEEKS}),
    hist("history_monthly_bins_start_on_the_first", {"interval": "M", "start_date": "2026-09-29", "end_date": "2026-11-02"}, {CB: MONTHS}),
    hist("history_no_candles_is_empty", {"interval": "1m"}, {CB: EMPTY}),
    hist("history_the_start_after_the_end_is_empty", {"interval": "1m", "start_date": "2026-11-05", "end_date": "2026-11-02"}, {CB: M16}),
    hist("history_to_today_the_end_is_the_current_minute", {"interval": "1m", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {CB: reply(*mins(9 * 60 + 15, 3, "13-11-2026"))}),
    hist("history_fifty_days_of_minutes_go_in_two_calls", {"interval": "1m", "start_date": "2026-09-01", "end_date": "2026-10-20"}, {CB: [reply(*mins(9 * 60 + 15, 2, "01-09-2026")), reply(*mins(9 * 60 + 15, 2, "02-10-2026"))]}),
    hist("history_a_chunk_that_fails_is_tried_again", {"interval": "1m"}, {CB: [HTTP_DOWN, M16]}),
    hist("history_a_chunk_that_always_fails_leaves_a_gap", {"interval": "1m", "start_date": "2026-09-01", "end_date": "2026-10-20"}, {CB: [reply(*mins(9 * 60 + 15, 2, "01-09-2026")), HTTP_DOWN, HTTP_DOWN, HTTP_DOWN, HTTP_DOWN]}),
    hist("history_every_chunk_failing_is_an_error", {"interval": "1m"}, {CB: [HTTP_DOWN, HTTP_DOWN, HTTP_DOWN, HTTP_DOWN]}),
    hist("history_a_chart_refusal_with_a_message", {"interval": "1m"}, {CB: CHART_REFUSED}),
    hist("history_a_chart_error_code_with_a_message", {"interval": "1m"}, {CB: CHART_ERR}),
    hist("history_a_chart_error_code_without_a_message", {"interval": "1m"}, {CB: CHART_ERR_NOTHING}),
    hist("history_a_rate_limited_call_is_retried", {"interval": "1m"}, {CB: [LIMITED, LIMITED, M16]}),
    hist("history_rate_limited_every_time", {"interval": "1m"}, {CB: [LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED, LIMITED]}),
    hist("history_futures", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {CB: M16}),
    hist("history_option", {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "interval": "1m"}, {CB: M16}),
    hist("history_stock_future", {"symbol": "RELIANCE25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {CB: M16}),
    hist("history_bse_derivative", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO", "interval": "1m"}, {CB: M16}),
    hist("history_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "1m"}, {CB: M16}),
    hist("history_currency", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS", "interval": "1m"}, {CB: M16}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE", "interval": "1m"}, {CB: M16}),
    hist("history_index_the_compact_name_answers", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "1m"}, {CB: M16}),
    hist("history_index_the_broker_name_answers_when_the_compact_one_is_empty", {"symbol": "BANKNIFTY", "exchange": "NSE_INDEX", "interval": "1m"}, {CB: [EMPTY, M16]}),
    hist("history_index_neither_name_answers", {"symbol": "INDIAVIX", "exchange": "NSE_INDEX", "interval": "1m"}, {CB: [EMPTY, EMPTY]}),
    hist("history_bse_index_has_one_name", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "interval": "1m"}, {CB: M16}),
    hist("history_index_the_first_name_fails_and_the_second_is_tried_in_the_same_attempt", {"symbol": "FINNIFTY", "exchange": "NSE_INDEX", "interval": "1m"}, {CB: [HTTP_DOWN, EMPTY, M16]}),
    hist("history_index_a_probe_in_a_second_chunk_uses_the_name_that_worked", {"symbol": "MIDCPNIFTY", "exchange": "NSE_INDEX", "interval": "1m", "start_date": "2026-09-01", "end_date": "2026-10-20"}, {CB: [EMPTY, reply(*mins(9 * 60 + 15, 2, "01-09-2026")), reply(*mins(9 * 60 + 15, 2, "02-10-2026"))]}),
    hist("history_interval_hdfc_sky_lacks", {"interval": "2m"}, {CB: M16}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {CB: M16}),
    hist("history_daily_index", {"symbol": "NIFTYNXT50", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {CB: D3}),
]


def patch():
    import importlib
    import types

    import pandas as pandas

    data = importlib.import_module("broker.hdfcsky.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed.replace(tzinfo=None) if tz is None else fixed.astimezone(tz)

    data.datetime = _Frozen

    class _Pandas:
        Timestamp = types.SimpleNamespace(now=staticmethod(lambda: pandas.Timestamp(fixed.replace(tzinfo=None))))

        def __getattr__(self, name):
            return getattr(pandas, name)

    data.pd = _Pandas()
    data.BrokerData._collect_feed_snapshot = lambda self, instruments, is_complete: {}
