"""History on Angel One (SmartAPI getCandleData and getOIData)."""

BROKER = "angel"
AUTH_TOKEN = "angel-jwt-token"
ENV = {"BROKER_API_KEY": "angel-private-key", "BROKER_API_SECRET": "angel-secret", "TZ": "Asia/Kolkata"}
READ_POSTS = [
    "/rest/secure/angelbroking/historical/v1/getCandleData",
    "/rest/secure/angelbroking/historical/v1/getOIData",
]

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN-EQ", "name": "SBIN", "exchange": "NSE", "brexchange": "NSE", "token": "3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY25NOV26FUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "35001", "expiry": "25NOV2026", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.05},
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "99926000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "AMXIDX", "tick_size": 0.0},
]

OK = {"status": True, "message": "SUCCESS", "errorcode": ""}


def candles(*rows):
    return {"body": {**OK, "data": [list(r) for r in rows]}}


CANDLE = "POST /rest/secure/angelbroking/historical/v1/getCandleData"
OI = "POST /rest/secure/angelbroking/historical/v1/getOIData"

M5 = candles(
    ("2026-11-02T09:15:00+05:30", 800.0, 802.5, 799.0, 801.25, 120000),
    ("2026-11-02T09:20:00+05:30", 801.25, 803.0, 800.5, 802.75, 98000),
    ("2026-11-02T09:25:00+05:30", 802.75, 804.1, 802.0, 803.9, 76500),
)
DAILY = candles(
    ("2026-10-29T00:00:00+05:30", 790.0, 801.0, 788.5, 799.95, 9100000),
    ("2026-10-30T00:00:00+05:30", 800.0, 806.0, 797.0, 805.45, 8300000),
)
FUT = candles(
    ("2026-11-02T09:15:00+05:30", 24000.0, 24020.5, 23990.0, 24010.0, 450000),
    ("2026-11-02T09:16:00+05:30", 24010.0, 24015.0, 24001.5, 24003.5, 380000),
)
FUT_OI = {"body": {**OK, "data": [{"time": "2026-11-02T09:15:00+05:30", "oi": 12900000}, {"time": "2026-11-02T09:16:00+05:30", "oi": 12915000}]}}
NAIVE = candles(("2026-11-02T09:15:00", 800.0, 802.5, 799.0, 801.25, 120000))
STRINGS = candles(("2026-11-02T09:15:00+05:30", "800.0", "802.5", "799.0", "801.25", "120000"))
EMPTY = candles()
UNSORTED = candles(
    ("2026-11-02T09:25:00+05:30", 802.75, 804.1, 802.0, 803.9, 76500),
    ("2026-11-02T09:15:00+05:30", 800.0, 802.5, 799.0, 801.25, 120000),
    ("2026-11-02T09:25:00+05:30", 1.0, 1.0, 1.0, 1.0, 1),
)
RATE = {"status": 403, "body": {"message": "Access denied because of exceeding access rate", "errorCode": "AB1004", "status": False}}
REFUSED = {"body": {"status": False, "message": "Invalid Token", "errorcode": "AG8001", "data": ""}}
INTERNAL = {"body": {"status": False, "message": "Something went wrong, please try again after sometime.", "errorcode": "AB1004", "data": ""}}


def hist(name, request, broker=None, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": {CANDLE: M5} if broker is None else broker, **extra}


SCENARIOS = [
    hist("history_5m_equity", {}),
    hist("history_1h", {"interval": "1h"}),
    hist("history_daily_shifted_to_ist", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {CANDLE: DAILY}),
    hist("history_time_without_offset_is_india_time", {}, {CANDLE: NAIVE}),
    hist("history_prices_sent_as_text", {}, {CANDLE: STRINGS}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {CANDLE: FUT, OI: FUT_OI}),
    hist("history_futures_open_interest_refused", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {CANDLE: FUT, OI: INTERNAL}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {CANDLE: M5}),
    # 70 days of one minute candles: SmartAPI takes 30 days a request, so three requests.
    hist("history_chunked_30_days", {"interval": "1m", "start_date": "2026-08-25", "end_date": "2026-11-02"}, {CANDLE: [M5, DAILY, M5]}),
    hist("history_nothing_traded", {}, {CANDLE: EMPTY}),
    hist("history_unsorted_with_duplicate", {}, {CANDLE: UNSORTED}),
    hist("history_end_before_start", {"start_date": "2026-11-03", "end_date": "2026-11-02"}, {}),
    hist("history_rate_limited_then_answered", {}, {CANDLE: [RATE, M5]}),
    hist("history_session_expired", {}, {CANDLE: REFUSED}),
    hist("history_chunk_refused", {}, {CANDLE: INTERNAL}),
    hist("history_unsupported_interval", {"interval": "2m"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
]
