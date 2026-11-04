"""History and intervals recorded on Zerodha (Kite's historical candles)."""

BROKER = "zerodha"

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "779521::::3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY26NOVFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "13238786::::51714", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUT", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "NIFTY 50", "name": "NIFTY 50", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "256265::::1001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]


def candles(*rows):
    return {"body": {"status": "success", "data": {"candles": [list(r) for r in rows]}}}


M5 = candles(
    ("2026-11-02T09:15:00+0530", 800.0, 802.5, 799.0, 801.25, 120000, 0),
    ("2026-11-02T09:20:00+0530", 801.25, 803.0, 800.5, 802.75, 98000, 0),
    ("2026-11-02T09:25:00+0530", 802.75, 804.1, 802.0, 803.9, 76500, 0),
)
DAILY = candles(
    ("2026-10-29T00:00:00+0530", 790.0, 801.0, 788.5, 799.95, 9100000, 0),
    ("2026-10-30T00:00:00+0530", 800.0, 806.0, 797.0, 805.45, 8300000, 0),
)
FUT = candles(
    ("2026-11-02T09:15:00+0530", 24000.0, 24020.5, 23990.0, 24010.0, 450000, 12900000),
    ("2026-11-02T09:16:00+0530", 24010.0, 24015.0, 24001.5, 24003.5, 380000, 12915000),
)
EMPTY = candles()
UNSORTED = candles(
    ("2026-11-02T09:25:00+0530", 802.75, 804.1, 802.0, 803.9, 76500, 0),
    ("2026-11-02T09:15:00+0530", 800.0, 802.5, 799.0, 801.25, 120000, 0),
    ("2026-11-02T09:25:00+0530", 1.0, 1.0, 1.0, 1.0, 1, 0),
)
REFUSED = {"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}
SBIN = "GET /instruments/historical/779521/"


def hist(name, request, broker=None, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker or {SBIN + "5minute": M5}, **extra}


SCENARIOS = [
    hist("history_5m_equity", {}),
    hist("history_1m", {"interval": "1m"}, {SBIN + "minute": M5}),
    hist("history_1h_alias", {"interval": "1h"}, {SBIN + "60minute": M5}),
    hist("history_daily_shifted_to_ist", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {SBIN + "day": DAILY}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {"GET /instruments/historical/13238786/minute": FUT}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {"GET /instruments/historical/256265/5minute": M5}),
    # 130 days of 5 minute candles: Kite takes 60 days a request, so three requests.
    hist("history_chunked_60_days", {"start_date": "2026-06-01", "end_date": "2026-10-08"}, {SBIN + "5minute": [M5, DAILY, M5]}),
    hist("history_nothing_traded", {}, {SBIN + "5minute": EMPTY}),
    hist("history_unsorted_with_duplicate", {}, {SBIN + "5minute": UNSORTED}),
    hist("history_end_before_start", {"start_date": "2026-11-03", "end_date": "2026-11-02"}),
    hist("history_one_day_range_of_several", {"start_date": "2026-11-02", "end_date": "2026-11-04"}),
    hist("history_unsupported_interval", {"interval": "2m"}),
    hist("history_unknown_symbol", {"symbol": "NOSUCH"}, {}),
    hist("history_refused", {}, {SBIN + "5minute": REFUSED}),
    hist("history_source_other", {"source": "x"}, {}),
    hist("history_bad_interval", {"interval": "7m"}, {}),
    hist("history_bad_date", {"start_date": "2026-13-01"}, {}),
    hist("history_missing_symbol", {"symbol": None}, {}),
    hist("history_bad_exchange", {"exchange": "XYZ"}, {}),
    {"name": "history_invalid_apikey", "path": "/api/v1/history", "auth": "none", "request": {"apikey": "not-a-key", "symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}, "broker": {}},
    {"name": "history_missing_apikey", "path": "/api/v1/history", "auth": "none", "request": {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}, "broker": {}},
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
    {"name": "intervals_invalid_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {"apikey": "not-a-key"}},
    {"name": "intervals_missing_apikey", "path": "/api/v1/intervals", "auth": "none", "request": {}},
]
