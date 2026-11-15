"""mStock history: GET /instruments/historical with a JSON body (exchange, token, interval and the dates as yyyy-MM-dd HH:mm) in chunks of days by interval, a second apart; candles come as [time, open, high, low, close, volume]. Today's candles are POSTed to /instruments/intraday with a numeric exchange code. A range that ends today asks for the days before it from the first call and for today from the second."""

from datetime import datetime as _datetime

import scenarios_mstock as s
from scenarios_mstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V  # noqa: F401

BROKER = "mstock"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS
H = f"GET {V}/instruments/historical"
I = f"POST {V}/instruments/intraday"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def candles(*rows, status=True):
    return {"body": {"status": status, "data": {"candles": list(rows)}}}


def minute_rows(day="2026-11-02", count=5, first=(9, 15), zone="+05:30"):
    rows = []
    start = first[0] * 60 + first[1]
    for i in range(count):
        t = start + i
        rows.append([f"{day}T{t // 60:02d}:{t % 60:02d}:00{zone}", 800 + i, 802 + i, 799 + i, 801 + i, 1000 + i])
    return rows


def naive_rows(day="2026-11-13", count=3, first=(9, 15)):
    return [[f"{day} {(first[0] * 60 + first[1] + i) // 60:02d}:{(first[0] * 60 + first[1] + i) % 60:02d}", 800 + i, 802 + i, 799 + i, 801 + i, 1000 + i] for i in range(count)]


def daily_rows(*days, zone="+05:30"):
    return [[f"{d}T00:00:00{zone}", 800 + i, 802 + i, 799 + i, 801 + i, 1000 * (i + 1)] for i, d in enumerate(days)]


SCENARIOS = [
    hist("history_one_minute", {}, {H: candles(*minute_rows())}),
    hist("history_one_minute_with_a_zone_of_plus_five", {}, {H: candles(*minute_rows(zone="+05"))}),
    hist("history_one_minute_with_no_zone_is_india_time", {}, {H: candles(*[[r[0].replace("T", " ").replace("+05:30", ""), *r[1:]] for r in minute_rows()])}),
    hist("history_one_minute_in_utc", {}, {H: candles(*minute_rows(zone="Z"))}),
    hist("history_one_minute_over_two_days_is_one_chunk", {"end_date": "2026-11-03"}, {H: candles(*minute_rows(), *minute_rows("2026-11-03"))}),
    hist("history_one_minute_over_five_days_goes_in_chunks_of_two", {"start_date": "2026-11-02", "end_date": "2026-11-06"}, {H: [candles(*minute_rows()), candles(*minute_rows("2026-11-04")), candles(*minute_rows("2026-11-06"))]}),
    hist("history_five_minutes", {"interval": "5m"}, {H: candles(*minute_rows(count=3))}),
    hist("history_five_minutes_over_a_month_goes_in_chunks_of_thirteen", {"interval": "5m", "start_date": "2026-10-01", "end_date": "2026-10-30"}, {H: [candles(*minute_rows("2026-10-01")), candles(*minute_rows("2026-10-14")), candles(*minute_rows("2026-10-27"))]}),
    hist("history_ten_minutes", {"interval": "10m"}, {H: candles(*minute_rows(count=2))}),
    hist("history_fifteen_minutes", {"interval": "15m"}, {H: candles(*minute_rows(count=2))}),
    hist("history_thirty_minutes", {"interval": "30m"}, {H: candles(*minute_rows(count=2))}),
    hist("history_one_hour", {"interval": "1h"}, {H: candles(*minute_rows(count=2))}),
    hist("history_three_minutes", {"interval": "3m"}, {H: candles(*minute_rows(count=2))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {H: candles(*daily_rows("2026-10-30", "2026-11-02", "2026-11-03"))}),
    hist("history_daily_candles_stamped_at_midnight_utc", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {H: candles(*daily_rows("2026-10-30", "2026-11-02", zone="Z"))}),
    hist("history_daily_candles_with_no_zone", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {H: candles(["2026-10-30 00:00", 1, 2, 0.5, 1.5, 10], ["2026-11-02 09:15", 2, 3, 1, 2.5, 20])}),
    hist("history_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {H: candles(*minute_rows(count=2))}),
    hist("history_a_derivative", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {H: candles(*minute_rows(count=2))}),
    hist("history_a_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {H: candles(*minute_rows(count=2))}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {H: candles(*minute_rows(count=2))}),
    hist("history_a_failed_chunk_is_left_out", {"start_date": "2026-11-02", "end_date": "2026-11-06"}, {H: [{"body": {"status": False, "message": "No data"}}, candles(*minute_rows("2026-11-04")), {"status": 500, "body": {"x": 1}}]}),
    hist("history_every_chunk_failed_is_no_candles", {"start_date": "2026-11-02", "end_date": "2026-11-04"}, {H: {"body": {"status": False, "message": "No data"}}}),
    hist("history_an_empty_answer_is_skipped", {}, {H: {"body": {}}}),
    hist("history_a_chunk_that_is_not_json_is_skipped", {}, {H: {"status": 200, "body": "<html>"}}),
    hist("history_a_chunk_with_no_candles", {}, {H: candles()}),
    hist("history_duplicate_times_keep_the_first", {}, {H: candles(["2026-11-02T09:15:00+05:30", 800, 802, 799, 801, 1000], ["2026-11-02T09:15:00+05:30", 900, 902, 899, 901, 2000], ["2026-11-02T09:16:00+05:30", 801, 803, 800, 802, 1500])}),
    hist("history_rows_out_of_order_are_sorted", {}, {H: candles(["2026-11-02T09:16:00+05:30", 801, 803, 800, 802, 1500], ["2026-11-02T09:15:00+05:30", 800, 802, 799, 801, 1000])}),
    hist("history_numbers_as_text", {}, {H: candles(["2026-11-02T09:15:00+05:30", "800.5", "802", "799", "801", "1200"])}),
    hist("history_a_price_that_is_not_a_number_is_an_error", {}, {H: candles(["2026-11-02T09:15:00+05:30", "x", 802, 799, 801, 1000])}),
    hist("history_a_time_that_does_not_parse_is_an_error", {}, {H: candles(["garbage", 800, 802, 799, 801, 1000])}),
    hist("history_a_row_with_too_few_cells_is_an_error", {}, {H: candles(["2026-11-02T09:15:00+05:30", 800, 802])}),
    hist("history_a_row_with_too_many_cells_is_an_error", {}, {H: candles(["2026-11-02T09:15:00+05:30", 800, 802, 799, 801, 1000, 5])}),
    hist("history_today_only_uses_the_intraday_call", {"start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows())}),
    hist("history_today_only_for_five_minutes", {"interval": "5m", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_a_derivative", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_the_bse", {"symbol": "TATAMOTORS", "exchange": "BSE", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_the_mcx", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_currency", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_for_the_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles(*naive_rows(count=2))}),
    hist("history_today_only_with_no_candles", {"start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: candles()}),
    hist("history_today_only_that_fails_is_an_error", {"start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: {"body": {"status": False, "message": "Market closed"}}}),
    hist("history_today_only_with_an_http_error_is_an_error", {"start_date": "2026-11-13", "end_date": "2026-11-13"}, {I: {"status": 500, "body": {"x": 1}}}),
    hist("history_a_range_ending_today_joins_the_days_before_and_today", {"start_date": "2026-11-11", "end_date": "2026-11-13"}, {H: candles(*minute_rows("2026-11-11", count=2), *minute_rows("2026-11-12", count=2)), I: candles(*naive_rows(count=2))}),
    hist("history_a_range_ending_today_with_a_failed_intraday_keeps_the_days_before", {"start_date": "2026-11-11", "end_date": "2026-11-13"}, {H: candles(*minute_rows("2026-11-11", count=2)), I: {"body": {"status": False}}}),
    hist("history_a_range_ending_today_with_nothing_from_either", {"start_date": "2026-11-11", "end_date": "2026-11-13"}, {H: candles(), I: candles()}),
    hist("history_a_range_ending_today_with_only_intraday", {"start_date": "2026-11-11", "end_date": "2026-11-13"}, {H: candles(), I: candles(*naive_rows(count=2))}),
    hist("history_a_range_ending_today_with_only_the_days_before", {"start_date": "2026-11-11", "end_date": "2026-11-13"}, {H: candles(*minute_rows("2026-11-12", count=2)), I: candles()}),
    hist("history_a_range_ending_today_where_a_candle_arrives_twice", {"start_date": "2026-11-12", "end_date": "2026-11-13"}, {H: candles(["2026-11-13T09:15:00+05:30", 1, 2, 0.5, 1.5, 10]), I: candles(["2026-11-13 09:15", 9, 9, 9, 9, 99])}),
    hist("history_a_range_after_today", {"start_date": "2026-11-20", "end_date": "2026-11-21"}, {H: candles()}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
]


def patch():
    import importlib

    import pandas as pd

    data = importlib.import_module("broker.mstock.api.data")
    data.time.sleep = lambda *a, **k: None
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Stamp(pd.Timestamp):
        @classmethod
        def now(cls, tz=None):
            return pd.Timestamp(fixed.astimezone(__import__("zoneinfo").ZoneInfo(tz)) if tz else fixed.replace(tzinfo=None))

    data.pd = type("pd_proxy", (), {"__getattr__": lambda self, n: getattr(pd, n) if n != "Timestamp" else _Stamp})()
