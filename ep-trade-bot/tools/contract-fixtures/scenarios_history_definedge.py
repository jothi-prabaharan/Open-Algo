"""Definedge history: GET /history/{segment}/{token}/{minute|day}/{from}/{to} on the data host, CSV rows (a date and time, open, high, low, close, volume and sometimes open interest). Five, fifteen, thirty and sixty minutes are built from the minute rows, in bins that start at the session open. Chunks of days by interval; a chunk that fails is left out."""

from datetime import datetime as _datetime

import scenarios_definedge as s
from scenarios_definedge import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "definedge"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", 1, "SENSEX", 1, "INDEX")]
H = "GET /sds/history"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def key(segment, token, timeframe, start, end):
    return f"{H}/{segment}/{token}/{timeframe}/{start}/{end}"


def csv(*rows):
    return {"text": "\n".join(rows) + "\n"}


def minute_rows(day="02112026", first=(9, 15), count=7, price=800.0, seven=False):
    rows = []
    h, m = first
    for i in range(count):
        t = h * 60 + m + i
        o = price + i
        row = f"{day}{t // 60:02d}{t % 60:02d},{o},{o + 2},{o - 1},{o + 1},{1000 + i}"
        rows.append(row + (f",{5000 + i}" if seven else ""))
    return rows


def span(day_from, day_to, timeframe="minute", segment="NSE", token=3045, today=False):
    """The request key for a range of days (ddmmyyyy), the end at 23:59 or, for the current day, at the minute it is."""
    end = f"{day_to}1100" if today else f"{day_to}2359"
    if timeframe == "day":
        end = f"{day_to}0000"
    return key(segment, token, timeframe, f"{day_from}0000", end)


D1 = "02112026"
SCENARIOS = [
    hist("history_one_minute", {}, {span(D1, D1): csv(*minute_rows())}),
    hist("history_one_minute_with_open_interest", {}, {span(D1, D1): csv(*minute_rows(seven=True))}),
    hist("history_one_minute_range_of_two_days", {"end_date": "2026-11-03"}, {span(D1, "03112026"): csv(*minute_rows(), *minute_rows("03112026"))}),
    hist("history_one_minute_chunks_of_thirty_days", {"start_date": "2026-10-01", "end_date": "2026-11-02"},
         {span("01102026", "30102026"): csv(*minute_rows("01102026")), span("31102026", D1): csv(*minute_rows(D1))}),
    hist("history_five_minutes_are_built_from_minutes", {"interval": "5m"}, {span(D1, D1): csv(*minute_rows(count=12))}),
    hist("history_fifteen_minutes", {"interval": "15m"}, {span(D1, D1): csv(*minute_rows(count=40, seven=True))}),
    hist("history_thirty_minutes_start_at_the_quarter_past", {"interval": "30m"}, {span(D1, D1): csv(*minute_rows(count=70))}),
    hist("history_sixty_minutes", {"interval": "1h"}, {span(D1, D1): csv(*minute_rows(count=130))}),
    hist("history_commodity_bins_start_on_the_hour", {"interval": "30m", "symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {span(D1, D1, segment="MCX", token=80001): csv(*minute_rows(first=(9, 0), count=70))}),
    hist("history_a_bin_with_no_candles_is_left_out", {"interval": "5m"}, {span(D1, D1): csv(*minute_rows(count=3), *minute_rows(first=(9, 40), count=3))}),
    hist("history_five_minute_chunks_of_ninety_days", {"interval": "5m", "start_date": "2026-07-01", "end_date": "2026-11-02"},
         {span("01072026", "28092026"): csv(*minute_rows("01072026")), span("29092026", D1): csv(*minute_rows(D1))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {span("30102026", "03112026", "day"): csv("301020260000,800,802,799,801,1000", "021120260000,801,803,800,802,2000", "031120260000,802,804,801,803,3000")}),
    hist("history_daily_with_open_interest", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {span("30102026", "03112026", "day"): csv("301020260000,800,802,799,801,1000,77")}),
    hist("history_the_current_day_ends_at_the_minute_it_is", {"end_date": "2026-11-13", "start_date": "2026-11-13"}, {span("13112026", "13112026", today=True): csv(*minute_rows("13112026", count=5))}),
    hist("history_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {span(D1, D1, token=26000): csv(*minute_rows())}),
    hist("history_the_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {span(D1, D1, segment="BSE", token=1): csv(*minute_rows())}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {span(D1, D1, segment="BSE", token=500570): csv(*minute_rows())}),
    hist("history_a_derivative", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {span(D1, D1, segment="NFO", token=49500): csv(*minute_rows(seven=True))}),
    hist("history_an_empty_answer_is_no_candles", {}, {span(D1, D1): {"text": ""}}),
    hist("history_a_server_error_is_tried_three_times", {}, {span(D1, D1): [{"status": 500, "body": "down"}, {"status": 500, "body": "down"}, csv(*minute_rows())]}),
    hist("history_a_server_error_every_time_is_no_candles", {}, {span(D1, D1): {"status": 500, "body": "down"}}),
    hist("history_a_client_error_is_not_retried", {}, {span(D1, D1): {"status": 403, "body": "no"}}),
    hist("history_a_request_timeout_is_retried", {}, {span(D1, D1): [{"status": 408, "body": "slow"}, csv(*minute_rows())]}),
    hist("history_rate_limited_is_tried_again", {}, {span(D1, D1): [{"status": 429, "body": "slow"}, csv(*minute_rows())]}),
    hist("history_a_failed_chunk_is_left_out", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {span("01102026", "30102026"): {"status": 403, "body": "no"}, span("31102026", D1): csv(*minute_rows(D1))}),
    hist("history_rows_with_a_time_that_does_not_parse_are_dropped", {}, {span(D1, D1): csv("garbage,1,2,3,4,5", *minute_rows(count=2))}),
    hist("history_every_row_bad_is_no_candles", {}, {span(D1, D1): csv("garbage,1,2,3,4,5")}),
    hist("history_candles_outside_the_range_are_clipped", {}, {span(D1, D1): csv(*minute_rows("01112026", count=2), *minute_rows(count=2), *minute_rows("03112026", count=2))}),
    hist("history_duplicate_times_keep_the_first", {}, {span(D1, D1): csv("021120260915,800,802,799,801,1000", "021120260915,900,902,899,901,2000", "021120260916,801,803,800,802,1500")}),
    hist("history_rows_out_of_order_are_sorted", {}, {span(D1, D1): csv("021120260916,801,803,800,802,1500", "021120260915,800,802,799,801,1000")}),
    hist("history_prices_that_are_not_numbers_are_zero", {}, {span(D1, D1): csv("021120260915,x,802,799,801,1000", "021120260916,801,803,800,802,n/a")}),
    hist("history_open_interest_that_is_not_a_number_is_zero", {}, {span(D1, D1): csv("021120260915,800,802,799,801,1000,n/a")}),
    hist("history_an_interval_definedge_lacks", {"interval": "3m"}, {span(D1, D1): csv(*minute_rows())}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
]


def patch():
    import importlib

    import pandas as pd

    limiter = importlib.import_module("broker.definedge.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
    data = importlib.import_module("broker.definedge.api.data")
    data.time.sleep = lambda *a, **k: None
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Stamp(pd.Timestamp):
        @classmethod
        def now(cls, tz=None):
            return pd.Timestamp(fixed.astimezone(__import__("zoneinfo").ZoneInfo(tz)) if tz else fixed.replace(tzinfo=None))

    data.pd = type("pd_proxy", (), {"__getattr__": lambda self, n: getattr(pd, n) if n != "Timestamp" else _Stamp})()
