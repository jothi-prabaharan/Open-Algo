"""History on Shoonya: TPSeries for intraday candles, EODChartData for daily ones, both in chunks, with the form body Noren's chart endpoints take."""

import json
from datetime import datetime as _datetime

import scenarios_shoonya as s
from scenarios_shoonya import AUTH_TOKEN, ENV as _ENV, READ_POSTS, SEED_API_KEY_ROW, BASE, Q, quote  # noqa: F401

BROKER = "shoonya"
ENV = {**_ENV, "TZ": "Asia/Kolkata"}
RECORDED_AT = "2026-11-13T11:00:00"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
    {"symbol": "OTHER", "brsymbol": "OTHER-EQ", "name": "OTHER", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]
READ_POSTS = READ_POSTS + [f"{BASE}/TPSeries", f"{BASE}/EODChartData"]
TP, EOD = f"POST {BASE}/TPSeries", f"POST {BASE}/EODChartData"

T = 1793591100  # 2026-11-02 09:15 IST


def bar(ts, o, h, l, c, v, oi=None, **extra):
    d = {"stat": "Ok", "time": _datetime.fromtimestamp(ts + 19800).strftime("%d-%m-%Y %H:%M:%S"), "ssboe": str(ts), "into": str(o), "inth": str(h), "intl": str(l), "intc": str(c), "intv": str(v), "v": "1000"}
    if oi is not None:
        d["oi"] = str(oi)
    d.update(extra)
    return d


def day(date, o, h, l, c, v):
    return json.dumps({"stat": "Ok", "time": date, "ssboe": None, "into": str(o), "inth": str(h), "intl": str(l), "intc": str(c), "intv": str(v)}).replace('"ssboe": null, ', "")


M5 = {"body": [bar(T + 600, 802.75, 804.1, 802.0, 803.9, 76500), bar(T + 300, 801.25, 803.0, 800.5, 802.75, 98000), bar(T, 800.0, 802.5, 799.0, 801.25, 120000)]}
FUT = {"body": [bar(T + 60, 24010.0, 24015.0, 24001.5, 24003.5, 380000, 12915000), bar(T, 24000.0, 24020.5, 23990.0, 24010.0, 450000, 12900000)]}
DAILY = {"body": [day("30-OCT-2026", 800.0, 806.0, 797.0, 805.45, 8300000), day("29-OCT-2026", 790.0, 801.0, 788.5, 799.95, 9100000)]}
NO_DATA = {"body": {"stat": "Not_Ok", "emsg": "Error Occurred : 5 \"no data\""}}
EXPIRED = {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}
OTHER_ERR = {"body": {"stat": "Not_Ok", "emsg": "Invalid Input : Bad token"}}
BAD_BARS = {"body": [
    bar(T, 0, 0, 0, 0, 5),
    bar(T + 300, 802.0, 801.0, 803.0, 802.5, 100),               # high below low: widened
    bar(T + 600, 800.0, 805.0, 799.0, 806.0, -50),               # close above high, negative volume
    {"stat": "Ok", "time": "02-11-2026 09:35:00", "into": "810", "inth": "811", "intl": "809", "intc": "810.5", "intv": "10"},   # no ssboe: the time text is read as IST
    {"stat": "Ok", "time": "garbled", "into": "810", "inth": "811", "intl": "809", "intc": "810.5", "intv": "10"},
    "not json",
    7,
    bar(T + 900, "x", 1, 1, 1, 1),
]}
DUP = {"body": [bar(T, 800.0, 802.5, 799.0, 801.25, 120000), bar(T, 1.0, 1.0, 1.0, 1.0, 1)]}


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


SCENARIOS = [
    hist("history_5m_equity_newest_first_is_sorted", {}, {TP: M5}),
    hist("history_1m", {"interval": "1m"}, {TP: M5}),
    hist("history_1h", {"interval": "1h"}, {TP: M5}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {TP: FUT}),
    hist("history_index_intraday_uses_the_cash_segment", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {TP: M5}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY}),
    hist("history_daily_index_uses_its_chart_name", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY}),
    hist("history_daily_index_without_a_chart_name", {"symbol": "OTHER", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY}),
    hist("history_daily_with_todays_candle_from_a_quote", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: quote()}),
    hist("history_daily_today_quote_refused", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: {"body": {"stat": "Not_Ok", "emsg": "no data"}}}),
    hist("history_daily_already_holds_today", {"interval": "D", "start_date": "2026-11-12", "end_date": "2026-11-13"}, {EOD: {"body": [day("13-NOV-2026", 1, 2, 0.5, 1.5, 10)]}, Q: quote()}),
    # 1m windows are 5 days: 12 days is three requests.
    hist("history_chunked", {"interval": "1m", "start_date": "2026-10-20", "end_date": "2026-11-01"}, {TP: [M5, NO_DATA, FUT]}),
    hist("history_chunked_5m_in_twenty_day_windows", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {TP: [M5, NO_DATA, M5, FUT]}),
    hist("history_daily_in_two_year_windows", {"interval": "D", "start_date": "2023-01-01", "end_date": "2026-01-01"}, {EOD: [DAILY, DAILY]}),
    hist("history_one_chunk_has_no_data", {"interval": "1m", "start_date": "2026-10-20", "end_date": "2026-11-01"}, {TP: [M5, NO_DATA, NO_DATA]}),
    hist("history_one_chunk_fails_others_answer", {"interval": "1m", "start_date": "2026-10-20", "end_date": "2026-11-01"}, {TP: [M5, OTHER_ERR, FUT]}),
    hist("history_every_chunk_fails", {"interval": "1m", "start_date": "2026-10-20", "end_date": "2026-11-01"}, {TP: OTHER_ERR}),
    hist("history_session_expired", {}, {TP: EXPIRED}),
    hist("history_no_data", {}, {TP: NO_DATA}),
    hist("history_chunk_answers_with_something_else", {}, {TP: {"body": "oops"}}),
    hist("history_daily_nothing", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: {"body": []}}),
    hist("history_zero_and_broken_bars_are_repaired", {}, {TP: BAD_BARS}),
    hist("history_duplicate_timestamp_keeps_the_last", {}, {TP: DUP}),
    hist("history_interval_shoonya_lacks", {"interval": "20m"}, {TP: M5}),
    hist("history_end_before_start", {"start_date": "2026-11-03", "end_date": "2026-11-02"}, {TP: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]


def patch():
    # The daily candle for today is built from "now"; the recording pins the clock the plugin reads.
    import importlib

    module = importlib.import_module("broker.shoonya.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed if tz is None else fixed.astimezone(tz)

    module.datetime = _Frozen
