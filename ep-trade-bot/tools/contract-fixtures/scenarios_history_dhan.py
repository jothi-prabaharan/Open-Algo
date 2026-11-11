"""History on Dhan (/v2/charts/historical and /v2/charts/intraday).

Dates are weekdays counted back from the day of recording, because Dhan history moves a window to
trading days and builds today's daily candle from the quote. The recording instant is written into
the fixture file, and the .NET test runs with its clock stopped there. The market calendar is
initialised (the bars after an index's close are dropped against it) and the server is set to
India's time zone, as OpenAlgo's deployments run.
"""

from datetime import datetime, timedelta, timezone

BROKER = "dhan"
CLIENT_ID = "1100000001"
AUTH_TOKEN = "dhan-access-token"
ENV = {"BROKER_API_KEY": f"{CLIENT_ID}:::dhan-app-key", "BROKER_API_SECRET": "dhan-app-secret", "TZ": "Asia/Kolkata"}
READ_POSTS = ["/v2/marketfeed/quote", "/v2/charts/intraday", "/v2/charts/historical"]
INIT = ["database.market_calendar_db:ensure_market_calendar_tables_exists"]

_IST = timezone(timedelta(hours=5, minutes=30))
_NOW = datetime.now(timezone.utc)
RECORDED_AT = _NOW.isoformat()
TODAY = _NOW.astimezone(_IST).date()


def wd(n):
    """The n-th weekday before today."""
    d = TODAY
    while n:
        d -= timedelta(days=1)
        if d.weekday() < 5:
            n -= 1
    return d


def iso(d):
    return d.isoformat()


def at(d, hour=9, minute=15):
    return int(datetime(d.year, d.month, d.day, hour, minute, tzinfo=_IST).timestamp())


def utc_midnight(d):
    """Dhan stamps a daily candle at 00:00 IST of its date."""
    return int(datetime(d.year, d.month, d.day, tzinfo=_IST).timestamp())


SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY-Nov2026-FUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FNO", "token": "35001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "13", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]


def candles(*bars):
    """Dhan answers one array per field."""
    return {"body": {
        "open": [b[1] for b in bars], "high": [b[2] for b in bars], "low": [b[3] for b in bars], "close": [b[4] for b in bars],
        "volume": [b[5] for b in bars], "timestamp": [b[0] for b in bars], "open_interest": [b[6] if len(b) > 6 else 0 for b in bars],
    }}


D1 = wd(3)
M5 = candles((at(D1), 800.0, 802.5, 799.0, 801.25, 120000), (at(D1, 9, 20), 801.25, 803.0, 800.5, 802.75, 98000), (at(D1, 9, 25), 802.75, 804.1, 802.0, 803.9, 76500))
DAILY = candles((utc_midnight(wd(5)), 790.0, 801.0, 788.5, 799.95, 9100000), (utc_midnight(wd(4)), 800.0, 806.0, 797.0, 805.45, 8300000))
FUT = candles((at(D1), 24000.0, 24020.5, 23990.0, 24010.0, 450000, 12900000), (at(D1, 9, 16), 24010.0, 24015.0, 24001.5, 24003.5, 380000, 12915000))
INDEX = candles((at(D1), 24000.0, 24020.5, 23990.0, 24010.0, 0), (at(D1, 15, 15), 24010.0, 24015.0, 24001.5, 24003.5, 0), (at(D1, 15, 45), 24003.5, 24003.5, 24003.5, 24003.5, 0))
ZEROS = candles((at(D1), 0, 0, 0, 801.25, 0))
EMPTY = {"body": {"open": [], "high": [], "low": [], "close": [], "volume": [], "timestamp": [], "open_interest": []}}
UNSORTED = candles((at(D1, 9, 25), 802.75, 804.1, 802.0, 803.9, 76500), (at(D1), 800.0, 802.5, 799.0, 801.25, 120000), (at(D1, 9, 25), 1.0, 1.0, 1.0, 1.0, 1))
LIMITED = {"body": {"status": "failed", "remarks": {"error_code": "DH-904"}, "data": {"805": "Too many requests"}}}
NOT_SUBSCRIBED = {"body": {"status": "failed", "remarks": {"error_code": "DH-902"}, "data": {"806": "Data APIs not subscribed"}}}
EXPIRED = {"status": 401, "body": {"errorType": "Invalid_Authentication", "errorCode": "DH-901", "errorMessage": "Client ID or user generated access token is invalid or expired."}}

INTRADAY = "POST /v2/charts/intraday"
HISTORICAL = "POST /v2/charts/historical"

_sat = TODAY - timedelta(days=(TODAY.weekday() - 5) % 7 or 7)
SATURDAY, SUNDAY = _sat, _sat + timedelta(days=1)


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": iso(D1), "end_date": iso(D1)}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


WEEK = {"start_date": iso(wd(8)), "end_date": iso(wd(4))}
LONG = {"start_date": iso(D1 - timedelta(days=200)), "end_date": iso(D1)}
SCENARIOS = [
    hist("history_5m_one_day", {}, {INTRADAY: M5}),
    hist("history_1m", {"interval": "1m"}, {INTRADAY: M5}),
    hist("history_15m", {"interval": "15m"}, {INTRADAY: M5}),
    hist("history_25m", {"interval": "25m"}, {INTRADAY: M5}),
    hist("history_1h", {"interval": "1h"}, {INTRADAY: M5}),
    hist("history_daily", {"interval": "D", "start_date": iso(wd(5)), "end_date": iso(wd(4))}, {HISTORICAL: DAILY}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {INTRADAY: FUT}),
    hist("history_index_bar_after_close_dropped", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "15m"}, {INTRADAY: INDEX}),
    hist("history_zero_prices", {}, {INTRADAY: ZEROS}),
    hist("history_unsorted_with_duplicate", {}, {INTRADAY: UNSORTED}),
    hist("history_nothing_traded", {}, {INTRADAY: EMPTY}),
    hist("history_week_one_request", {**WEEK}, {INTRADAY: M5}),
    hist("history_start_on_saturday_moves_to_monday", {"start_date": iso(SATURDAY - timedelta(days=7)), "end_date": iso(SATURDAY - timedelta(days=1))}, {INTRADAY: M5}),
    hist("history_end_on_sunday_moves_to_friday", {"start_date": iso(SATURDAY - timedelta(days=6)), "end_date": iso(SUNDAY)}, {INTRADAY: M5}),
    hist("history_weekend_only_intraday", {"start_date": iso(SATURDAY), "end_date": iso(SUNDAY)}, {}),
    hist("history_weekend_only_daily", {"interval": "D", "start_date": iso(SATURDAY), "end_date": iso(SUNDAY)}, {HISTORICAL: EMPTY}),
    # 200 days of 5 minute candles: 90 days a request, so three requests.
    hist("history_chunked_90_days", {**LONG}, {INTRADAY: [M5, M5, M5]}),
    hist("history_empty_chunk_tried_again", {**WEEK}, {INTRADAY: [EMPTY, M5]}),
    hist("history_hole_between_chunks", {**LONG}, {INTRADAY: [M5, EMPTY, EMPTY, EMPTY, M5]}),
    hist("history_chunk_refused_three_times", {**WEEK}, {INTRADAY: NOT_SUBSCRIBED}),
    hist("history_rate_limit_then_answered", {}, {INTRADAY: [LIMITED, M5]}),
    hist("history_one_day_refused", {}, {INTRADAY: NOT_SUBSCRIBED}),
    hist("history_token_expired", {}, {INTRADAY: EXPIRED}),
    hist("history_unsupported_interval", {"interval": "2m"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": iso(D1), "end_date": iso(D1)}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
]
