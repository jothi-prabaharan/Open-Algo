"""Rupeezy history: GET /v2/data/history in chunks (30 days intraday, 1000 days daily), epoch seconds, daily bars moved to midnight UTC of their India date."""

import scenarios_rupeezy as s
from scenarios_rupeezy import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, B, SYMBOLS, req  # noqa: F401

BROKER = "rupeezy"
HB = f"GET {B}/data/history"
T = 1793591100  # 2026-11-02 09:15 IST


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


def ok(times, o=None, h=None, l=None, c=None, v=None):
    n = len(times)
    return {"body": {"s": "ok", "t": times, "o": o or [800.0 + i for i in range(n)], "h": h or [802.0 + i for i in range(n)], "l": l or [799.0 + i for i in range(n)], "c": c or [801.0 + i for i in range(n)], "v": v if v is not None else [1000 * (i + 1) for i in range(n)]}}


M5 = ok([T, T + 300, T + 600])
UNSORTED = ok([T + 300, T, T + 600])
DUPLICATES = ok([T, T, T + 300], c=[1.0, 2.0, 3.0])
NO_DATA = {"body": {"s": "no_data"}}
ERROR = {"body": {"s": "error", "message": "Bad ticker"}}
ERROR_ERRMSG = {"body": {"s": "error", "errmsg": "Bad range"}}
ERROR_NOTHING = {"body": {"s": "error"}}
NULL_VOLUME = {"body": {"s": "ok", "t": [T, T + 300], "o": [1, 2], "h": [2, 3], "l": [0.5, 1], "c": [1.5, 2.5], "v": [None, 7]}}
DAILY_IST = ok([1793212200, 1793298600])      # midnight IST of 2026-10-29 and 10-30
DAILY_UTC = ok([1793232000, 1793318400])      # midnight UTC of the same days
WEEKLY = ok([1793212200])
HTTP_DOWN = {"status": 500, "body": {"status": "error", "message": "down"}}

SCENARIOS = [
    hist("history_5m_equity", {}, {HB: M5}),
    hist("history_is_sorted", {}, {HB: UNSORTED}),
    hist("history_one_bar_per_timestamp_the_first_kept", {}, {HB: DUPLICATES}),
    hist("history_null_volume_is_zero", {}, {HB: NULL_VOLUME}),
    hist("history_no_data_is_empty", {}, {HB: NO_DATA}),
    hist("history_error_with_a_message", {}, {HB: ERROR}),
    hist("history_error_with_errmsg", {}, {HB: ERROR_ERRMSG}),
    hist("history_error_with_nothing", {}, {HB: ERROR_NOTHING}),
    hist("history_http_error", {}, {HB: HTTP_DOWN}),
    hist("history_1m", {"interval": "1m"}, {HB: M5}),
    hist("history_45m", {"interval": "45m"}, {HB: M5}),
    hist("history_4h", {"interval": "4h"}, {HB: M5}),
    hist("history_futures", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {HB: M5}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {HB: M5}),
    hist("history_daily_from_ist_midnight", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {HB: DAILY_IST}),
    hist("history_daily_from_utc_midnight", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {HB: DAILY_UTC}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-10-26", "end_date": "2026-10-30"}, {HB: WEEKLY}),
    hist("history_monthly", {"interval": "M", "start_date": "2026-10-01", "end_date": "2026-10-30"}, {HB: WEEKLY}),
    hist("history_two_chunks_of_thirty_days", {"interval": "5m", "start_date": "2026-10-01", "end_date": "2026-11-15"}, {HB: [M5, ok([T + 900])]}),
    hist("history_a_chunk_fails_after_the_first_answered", {"interval": "5m", "start_date": "2026-10-01", "end_date": "2026-11-15"}, {HB: [M5, ERROR]}),
    hist("history_daily_chunks_of_a_thousand_days", {"interval": "D", "start_date": "2022-01-01", "end_date": "2026-10-30"}, {HB: [DAILY_IST, DAILY_UTC, WEEKLY, NO_DATA, NO_DATA]}),
    hist("history_interval_rupeezy_lacks", {"interval": "20m"}, {HB: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]
