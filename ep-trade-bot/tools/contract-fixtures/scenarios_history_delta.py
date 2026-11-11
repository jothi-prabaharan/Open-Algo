"""History on Delta Exchange (public /v2/history/candles).

Dates are relative to the day of recording (the answer is capped at now); the recording instant
is written into the fixture file and the .NET test runs with its clock stopped there. A request
that ends today is covered by an integration test, because its "end" is the second it was made.
"""

from datetime import datetime, timedelta, timezone

BROKER = "deltaexchange"
AUTH_TOKEN = "delta-api-key"
ENV = {"BROKER_API_KEY": "delta-api-key", "BROKER_API_SECRET": "delta-api-secret", "TZ": "Asia/Kolkata"}

_IST = timezone(timedelta(hours=5, minutes=30))
_NOW = datetime.now(timezone.utc)
RECORDED_AT = _NOW.isoformat()
TODAY = _NOW.astimezone(_IST).date()


def day(back):
    return (TODAY - timedelta(days=back)).isoformat()


def at(back, hour=9, minute=15):
    d = TODAY - timedelta(days=back)
    return int(datetime(d.year, d.month, d.day, hour, minute, tzinfo=_IST).timestamp())




def patch():
    """Delta weighs public calls against a quota of 9000 units in 300 s; the recording would run it dry. EP Trade bot does not port the accounting."""
    import broker.deltaexchange.api.data as data

    data.consume = lambda *a, **k: None


SYMBOLS = [
    {"symbol": "BTCUSD", "brsymbol": "BTCUSD", "name": "Bitcoin perpetual", "exchange": "CRYPTO", "brexchange": "CRYPTO", "token": "27", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "PERPFUT", "tick_size": 0.5, "contract_value": 0.001},
]

H = "GET /v2/history/candles"


def ok(*rows):
    return {"body": {"success": True, "result": [list(r) if isinstance(r, tuple) else r for r in rows]}}


M5 = ok((at(9), 67000.0, 67100.5, 66950.0, 67050.25, 120), (at(9, 9, 20), 67050.25, 67200.0, 67000.0, 67150.0, 98), (at(9, 9, 25), 67150.0, 67180.0, 67100.0, 67120.0, 76))
DAILY = ok((at(12, 5, 30), 66000.0, 67500.0, 65800.5, 67000.0, 91000), (at(11, 5, 30), 67000.0, 68000.0, 66800.0, 67900.0, 83000))
WITH_OI = ok((at(9), 67000.0, 67100.5, 66950.0, 67050.25, 120, 5400), (at(9, 9, 20), 67050.25, 67200.0, 67000.0, 67150.0, 98, 5450))
NAMED = ok({"time": at(9), "open": 67000.0, "high": 67100.5, "low": 66950.0, "close": 67050.25, "volume": 120}, {"t": at(9, 9, 20), "o": 1.0, "h": 2.0, "l": 0.5, "c": 1.5, "v": 7})
TEXT = ok((str(at(9)), "67000.0", "67100.5", "66950.0", "67050.25", "120.0"))
FUTURE = ok((at(9), 67000.0, 67100.5, 66950.0, 67050.25, 120), (int(_NOW.timestamp()) + 100000, 1.0, 1.0, 1.0, 1.0, 1))
UNSORTED = ok((at(9, 9, 25), 67150.0, 67180.0, 67100.0, 67120.0, 76), (at(9), 67000.0, 67100.5, 66950.0, 67050.25, 120), (at(9, 9, 25), 1.0, 1.0, 1.0, 1.0, 1))
EMPTY = ok()
LIMITED = {"status": 429, "body": {"success": False, "error": {"code": "rate_limit_exceeded"}}}
SERVER = {"status": 500, "body": {"success": False, "error": {"code": "internal_error"}}}
REFUSED = {"body": {"success": False, "error": {"code": "invalid_symbol"}}}


def hist(name, request, broker, **extra):
    base = {"symbol": "BTCUSD", "exchange": "CRYPTO", "interval": "5m", "start_date": day(9), "end_date": day(9)}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


SCENARIOS = [
    hist("history_5m", {}, {H: M5}),
    hist("history_1h", {"interval": "1h"}, {H: M5}),
    hist("history_daily", {"interval": "D", "start_date": day(12), "end_date": day(11)}, {H: DAILY}),
    hist("history_weekly", {"interval": "W", "start_date": day(60), "end_date": day(9)}, {H: DAILY}),
    # 1m candles are asked a day at a time (the answer is capped near 4000 candles): three days, three requests.
    hist("history_1m_one_request_per_day", {"interval": "1m", "start_date": day(11), "end_date": day(9)}, {H: [M5, M5, M5]}),
    # 5m: 12 days a request, so 30 days are three requests.
    hist("history_5m_chunked_12_days", {"start_date": day(38), "end_date": day(9)}, {H: [M5, DAILY, M5]}),
    hist("history_daily_never_chunked", {"interval": "D", "start_date": day(400), "end_date": day(9)}, {H: DAILY}),
    hist("history_with_open_interest", {}, {H: WITH_OI}),
    hist("history_candles_as_named_fields", {}, {H: NAMED}),
    hist("history_numbers_as_text", {}, {H: TEXT}),
    hist("history_candle_after_now_dropped", {}, {H: FUTURE}),
    hist("history_unsorted_with_duplicate", {}, {H: UNSORTED}),
    hist("history_nothing_traded", {}, {H: EMPTY}),
    hist("history_end_before_start", {"start_date": day(8), "end_date": day(9)}, {}),
    # Both dates are still to come: nothing can have traded, so Delta is not asked.
    hist("history_dates_in_the_future", {"start_date": day(-2), "end_date": day(-3)}, {}),
    hist("history_rate_limited_then_answered", {}, {H: [LIMITED, M5]}),
    hist("history_rate_limited_every_time", {}, {H: LIMITED}),
    hist("history_server_error", {}, {H: SERVER}),
    hist("history_symbol_refused", {}, {H: REFUSED}),
    hist("history_interval_delta_lacks", {"interval": "2m"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "BTCUSD", "exchange": "CRYPTO", "interval": "5m", "start_date": day(9), "end_date": day(9)}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
]
