"""Sahi history: GET /v1/marketdata/candles, newest page first and paged backwards while has_more; prices in paise; intraday stamped in epoch seconds,
daily candles at 00:00 UTC of their India date. The clock is stopped at 2026-11-13 11:00 India time."""

from datetime import datetime as _datetime

import scenarios_sahi as s
import scenarios_sahi_data as d
from scenarios_sahi import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401
from scenarios_sahi_data import RECORDED_AT, SYMBOLS, NOW_MS  # noqa: F401

BROKER = "sahi"
FREEZE_CLOCK = False
CB = "GET /v1/marketdata/candles"
T = 1793591100000  # 2026-11-02 09:15 IST in ms
M = 60000
TODAY_T = d.TODAY


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


def c(ts, o=80000, h=80200, l=79900, close=80100, v=1000, *oi):
    return [ts, o, h, l, close, v, *oi]


def page(candles, more=False):
    return {"body": {"candles": candles, "has_more": more}}


M5 = page([c(T), c(T + 5 * M, 80100, 80300, 80000, 80250, 2000), c(T + 10 * M, 80250, 80400, 80150, 80300, 3000)])
UNSORTED = page([c(T + 5 * M), c(T), c(T + 10 * M)])
DUPLICATES = page([c(T, close=1), c(T, close=2), c(T + 5 * M, close=3)])
WITH_OI = page([c(T, 80000, 80200, 79900, 80100, 1000, 1500), c(T + 5 * M, 80100, 80300, 80000, 80250, 2000, 1600)])
EMPTY = page([])
NO_KEY = {"body": {"x": 1}}
FRACTIONAL = page([c(T, 80000, 80200, 79900, 80100, 1000.9, 1500.7)])
DAILY = page([c(1794368700000), c(1794455100000, 79900, 80500, 79500, 80395, 9000)])
DAILY_AT_MIDNIGHT = page([c(1794335400000), c(1794421800000)])  # 00:00 IST of 11 and 12 November
TODAY = page([c(d.TODAY, 80245, 80945, 80045, 80500, 5)])

SCENARIOS = [
    hist("history_5m_equity", {}, {CB: M5}),
    hist("history_is_sorted", {}, {CB: UNSORTED}),
    hist("history_one_bar_per_timestamp_the_last_kept", {}, {CB: DUPLICATES}),
    hist("history_no_candles_is_empty", {}, {CB: EMPTY}),
    hist("history_answer_without_candles_is_empty", {}, {CB: NO_KEY}),
    hist("history_futures_ask_for_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {CB: WITH_OI}),
    hist("history_options_ask_for_open_interest", {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, {CB: WITH_OI}),
    hist("history_equity_without_open_interest_column_has_zero", {}, {CB: M5}),
    hist("history_fractional_volume_and_open_interest_are_truncated", {}, {CB: FRACTIONAL}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {CB: M5}),
    hist("history_1m", {"interval": "1m"}, {CB: M5}),
    hist("history_3m", {"interval": "3m"}, {CB: M5}),
    hist("history_15m", {"interval": "15m"}, {CB: M5}),
    hist("history_30m", {"interval": "30m"}, {CB: M5}),
    hist("history_1h", {"interval": "1h"}, {CB: M5}),
    hist("history_daily_is_stamped_at_midnight_utc_of_the_india_date", {"interval": "D", "start_date": "2026-11-11", "end_date": "2026-11-12"}, {CB: DAILY}),
    hist("history_daily_candle_stamped_at_midnight_india_time", {"interval": "D", "start_date": "2026-11-11", "end_date": "2026-11-12"}, {CB: DAILY_AT_MIDNIGHT}),
    hist("history_range_ending_today_asks_for_the_partial_candle", {"interval": "D", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {CB: TODAY}),
    hist("history_range_ending_in_the_future_stops_at_now", {"interval": "5m", "start_date": "2026-11-13", "end_date": "2026-11-20"}, {CB: M5}),
    hist("history_pages_backwards_while_there_is_more", {"start_date": "2026-10-01", "end_date": "2026-11-02"},
         {CB: [page([c(T + 300 * M), c(T + 305 * M)], True), page([c(T), c(T + 5 * M)], True), page([c(T - 300 * M)], False)]}),
    hist("history_stops_when_a_page_comes_back_empty", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {CB: [page([c(T)], True), page([], True)]}),
    hist("history_pages_are_capped", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {CB: [page([c(T - M * i)], True) for i in range(200)]}),
    hist("history_puts_ask_for_open_interest", {"symbol": "NIFTY25NOV2624000PE", "exchange": "NFO"}, {CB: WITH_OI}),
    hist("history_partial_candle_is_asked_for_on_the_first_page_only", {"start_date": "2026-10-01", "end_date": "2026-11-13"}, {CB: [page([c(TODAY_T)], True), page([c(TODAY_T - 600 * M)], False)]}),
    hist("history_a_timestamp_on_two_pages_keeps_the_later_page_row", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {CB: [page([c(T, close=1)], True), page([c(T, close=2)], False)]}),
    hist("history_session_expired", {}, {CB: d.REFUSED_401}),
    hist("history_key_without_market_data", {}, {CB: d.REFUSED_403}),
    hist("history_refused_with_a_message", {}, {CB: d.REFUSED_MSG}),
    hist("history_refused_without_a_message", {}, {CB: d.REFUSED_BARE}),
    hist("history_a_later_page_fails", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {CB: [page([c(T)], True), d.REFUSED_MSG]}),
    hist("history_interval_sahi_lacks", {"interval": "4h"}, {CB: M5}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {CB: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]


def patch():
    d.patch()
