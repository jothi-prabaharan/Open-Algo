"""History on Fyers (/data/history).

Dates are relative to the day of recording, because Fyers history clamps its window to today and
to the last 30 days for seconds candles. The recording instant is written into the fixture file,
and the .NET test runs with its clock stopped there.
"""

from datetime import datetime, timedelta, timezone

BROKER = "fyers"
AUTH_TOKEN = "fyers-access-token"
ENV = {"BROKER_API_KEY": "FYAPPID-100", "BROKER_API_SECRET": "fyers-secret", "TZ": "Asia/Kolkata"}

_IST = timezone(timedelta(hours=5, minutes=30))
_NOW = datetime.now(timezone.utc)
RECORDED_AT = _NOW.isoformat()
TODAY = _NOW.astimezone(_IST).date()


def day(back):
    return (TODAY - timedelta(days=back)).isoformat()


def at(back, hour=9, minute=15):
    d = TODAY - timedelta(days=back)
    return int(datetime(d.year, d.month, d.day, hour, minute, tzinfo=_IST).timestamp())


SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "NSE:SBIN-EQ", "name": "SBIN", "exchange": "NSE", "brexchange": "NSE", "token": "10100000003045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NSE:NIFTY26NOVFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "101126112535001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "NSE:NIFTY50-INDEX", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "101000000026000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

H = "GET /data/history"


def ok(*rows):
    return {"body": {"s": "ok", "code": 200, "message": "", "candles": [list(r) for r in rows]}}


M5 = ok((at(9), 800.0, 802.5, 799.0, 801.25, 120000), (at(9, 9, 20), 801.25, 803.0, 800.5, 802.75, 98000), (at(9, 9, 25), 802.75, 804.1, 802.0, 803.9, 76500))
DAILY = ok((at(12, 5, 30), 790.0, 801.0, 788.5, 799.95, 9100000), (at(11, 5, 30), 800.0, 806.0, 797.0, 805.45, 8300000))
FUT_OI = ok((at(9), 24000.0, 24020.5, 23990.0, 24010.0, 450000, 12900000), (at(9, 9, 16), 24010.0, 24015.0, 24001.5, 24003.5, 380000, 12915000))
FUT_NO_OI = ok((at(9), 24000.0, 24020.5, 23990.0, 24010.0, 450000))
EMPTY = ok()
UNSORTED = ok((at(9, 9, 25), 802.75, 804.1, 802.0, 803.9, 76500), (at(9), 800.0, 802.5, 799.0, 801.25, 120000), (at(9, 9, 25), 1.0, 1.0, 1.0, 1.0, 1))
FAILED = {"body": {"s": "error", "code": -300, "message": "Please provide a valid symbol"}}
LIMITED = {"status": 429, "body": {"s": "error", "message": "Too many requests"}}
EXPIRED = {"status": 401, "body": {"s": "error", "code": -16, "message": "Could not authenticate the user"}}


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": day(9), "end_date": day(9)}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


SCENARIOS = [
    hist("history_5m_equity", {}, {H: M5}),
    hist("history_1h", {"interval": "1h"}, {H: M5}),
    hist("history_5_seconds", {"interval": "5s"}, {H: M5}),
    hist("history_daily", {"interval": "D", "start_date": day(12), "end_date": day(11)}, {H: DAILY}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {H: FUT_OI}),
    hist("history_futures_without_open_interest_column", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {H: FUT_NO_OI}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {H: M5}),
    # 130 days of 5 minute candles: 60 days a request, so three requests.
    hist("history_chunked_60_days", {"start_date": day(130), "end_date": day(9)}, {H: [M5, DAILY, M5]}),
    # 100 days of daily candles: 300 days a request, so one.
    hist("history_daily_chunk_300_days", {"interval": "D", "start_date": day(100), "end_date": day(9)}, {H: DAILY}),
    # Candles of a few seconds exist for 30 days only: the start moves up to the earliest day.
    hist("history_seconds_start_clamped_to_30_days", {"interval": "5s", "start_date": day(60), "end_date": day(9)}, {H: M5}),
    hist("history_end_date_in_the_future_ends_today", {"start_date": day(1), "end_date": day(-5)}, {H: M5}),
    hist("history_nothing_traded", {}, {H: EMPTY}),
    hist("history_unsorted_with_duplicate", {}, {H: UNSORTED}),
    hist("history_start_after_end", {"start_date": day(8), "end_date": day(9)}, {}),
    hist("history_refused_then_answered", {}, {H: [FAILED, M5]}),
    hist("history_rate_limited_then_answered", {}, {H: [LIMITED, M5]}),
    # Five 429s in a row need both retry layers: three rate-limit retries, then a refused-call retry.
    hist("history_rate_limited_five_times_then_answered", {}, {H: [LIMITED] * 5 + [M5]}),
    hist("history_chunk_refused_every_time", {}, {H: FAILED}),
    hist("history_session_expired", {}, {H: EXPIRED}),
    hist("history_weekly_not_offered", {"interval": "W"}, {}),
    hist("history_interval_fyers_lacks", {"interval": "3h"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": day(9), "end_date": day(9)}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
]
