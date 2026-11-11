"""History on Upstox (v3 historical candles)."""

from urllib.parse import quote

BROKER = "upstox"
AUTH_TOKEN = "upstox-access-token"
ENV = {"BROKER_API_KEY": "upstox-api-key", "BROKER_API_SECRET": "upstox-secret", "TZ": "Asia/Kolkata"}

SBIN, FUT = "NSE_EQ|INE062A01020", "NSE_FO|35001"
SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE_EQ", "token": SBIN, "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY FUT 25 NOV 26", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FO", "token": FUT, "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
]


def key(unit, interval, to, frm, instrument=SBIN):
    return f"GET /v3/historical-candle/{quote(instrument)}/{unit}/{interval}/{to}/{frm}"


def candles(*rows):
    return {"body": {"status": "success", "data": {"candles": [list(r) for r in rows]}}}


M5 = candles(
    ("2026-11-02T09:25:00+05:30", 802.75, 804.1, 802.0, 803.9, 76500, 0),
    ("2026-11-02T09:20:00+05:30", 801.25, 803.0, 800.5, 802.75, 98000, 0),
    ("2026-11-02T09:15:00+05:30", 800.0, 802.5, 799.0, 801.25, 120000, 0),
)
DAILY = candles(("2026-10-30T00:00:00+05:30", 800.0, 806.0, 797.0, 805.45, 8300000, 0), ("2026-10-29T00:00:00+05:30", 790.0, 801.0, 788.5, 799.95, 9100000, 0))
FUT_CANDLES = candles(("2026-11-02T09:16:00+05:30", 24010.0, 24015.0, 24001.5, 24003.5, 380000, 12915000), ("2026-11-02T09:15:00+05:30", 24000.0, 24020.5, 23990.0, 24010.0, 450000, 12900000))
EMPTY = candles()
DUP = candles(
    ("2026-11-02T09:15:00+05:30", 800.0, 802.5, 799.0, 801.25, 120000, 0),
    ("2026-11-02T09:15:00+05:30", 1.0, 1.0, 1.0, 1.0, 1, 0),
)
RATE = {"status": 429, "body": {"status": "error", "errors": [{"errorCode": "UDAPI10005", "message": "Rate limit threshold exceeded"}]}}
EXPIRED = {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}
BAD = {"status": 400, "body": {"status": "error", "errors": [{"errorCode": "UDAPI1021", "message": "Invalid Instrument key"}]}}


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


D = ("2026-11-02", "2026-11-02")
SCENARIOS = [
    hist("history_5m_equity", {}, {key("minutes", 5, *D): M5}),
    hist("history_2m", {"interval": "2m"}, {key("minutes", 2, *D): M5}),
    hist("history_1h", {"interval": "1h"}, {key("hours", 1, *D): M5}),
    hist("history_daily_stamped_at_midnight_utc", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {key("days", 1, "2026-10-30", "2026-10-29"): DAILY}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-10-26", "end_date": "2026-11-02"}, {key("weeks", 1, "2026-11-02", "2026-10-26"): DAILY}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {key("minutes", 1, *D, instrument=FUT): FUT_CANDLES}),
    # 63 days of 5 minute candles: 30 days a request, so three requests.
    hist("history_chunked_30_days", {"start_date": "2026-09-01", "end_date": "2026-11-02"},
         {key("minutes", 5, "2026-09-30", "2026-09-01"): M5, key("minutes", 5, "2026-10-30", "2026-10-01"): DAILY, key("minutes", 5, "2026-11-02", "2026-10-31"): M5}),
    hist("history_nothing_traded", {}, {key("minutes", 5, *D): EMPTY}),
    hist("history_duplicate_timestamp", {}, {key("minutes", 5, *D): DUP}),
    hist("history_end_before_start", {"start_date": "2026-11-03", "end_date": "2026-11-02"}, {}),
    hist("history_rate_limited_then_answered", {}, {key("minutes", 5, *D): [RATE, M5]}),
    hist("history_session_expired", {}, {key("minutes", 5, *D): EXPIRED}),
    hist("history_chunk_refused", {}, {key("minutes", 5, *D): BAD}),
    hist("history_interval_upstox_lacks", {"interval": "20m"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
]
