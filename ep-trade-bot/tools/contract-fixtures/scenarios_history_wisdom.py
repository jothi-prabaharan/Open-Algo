"""Wisdom Capital (XTS) history: /apimarketdata/instruments/ohlc, in chunks of up to 7 days."""

from scenarios_wisdom import AUTH_TOKEN, ENV, SYMBOLS as _SYMBOLS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "wisdom"
ENV = {**ENV, "TZ": "Asia/Kolkata"}
SYMBOLS = _SYMBOLS + [
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]
OHLC = "GET /apimarketdata/instruments/ohlc"

# Seconds as XTS sends them: IST wall clock read as if it were UTC (09:15 IST on 2026-11-02 is 09:15 on the epoch clock).
T = 1793610900  # 2026-11-02 09:15:00 on that clock


def reply(*rows):
    data = ",".join("|".join(str(x) for x in r) for r in rows)
    return {"body": {"type": "success", "code": "s-instrument-0001", "description": "Get instrument ohlc successfully!", "result": {"dataReponse": data, "ExchangeSegment": "NSECM"}}}


M5 = reply((T, 800.0, 802.5, 799.0, 801.25, 120000, 0), (T + 300, 801.25, 803.0, 800.5, 802.75, 98000, 0), (T + 600, 802.75, 804.1, 802.0, 803.9, 76500, 0))
M5_OFFSET = reply((T + 120, 800.0, 802.5, 799.0, 801.25, 120000, 0), (T + 421, 801.25, 803.0, 800.5, 802.75, 98000, 0))
DAILY = reply((T - 33300, 800.0, 806.0, 797.0, 805.45, 8300000, 0), (T - 33300 + 86400, 790.0, 801.0, 788.5, 799.95, 9100000, 0))
DUP = reply((T, 800.0, 802.5, 799.0, 801.25, 120000, 0), (T, 1.0, 1.0, 1.0, 1.0, 1, 0))
SHORT_AND_BAD = reply((T, 800.0, 802.5, 799.0, 801.25, 120000, 0), (T + 300, 1, 2, 3, 4), (T + 600, "x", 1, 1, 1, 1, 0), (T + 900, 802.0, 803.0, 801.0, 802.5, 500, 0), (T + 1200, 1, 2, 3, 4, "5.0", 0))
EMPTY = {"body": {"type": "success", "result": {"dataReponse": ""}}}
REFUSED = {"status": 400, "body": {"type": "error", "code": "e-marketdata-0006", "description": "Invalid Token", "result": {}}}


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


SCENARIOS = [
    hist("history_5m_equity", {}, {OHLC: M5}),
    hist("history_candle_start_floored", {}, {OHLC: M5_OFFSET}),
    hist("history_1m", {"interval": "1m"}, {OHLC: M5}),
    hist("history_15m", {"interval": "15m"}, {OHLC: M5}),
    hist("history_daily_stamped_mid_morning", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {OHLC: reply((T - 86400 + 3600, 800.0, 806.0, 797.0, 805.45, 8300000, 0), (T + 7200, 790.0, 801.0, 788.5, 799.95, 9100000, 0))}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {OHLC: DAILY}),
    hist("history_futures", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {OHLC: M5}),
    hist("history_mcx", {"symbol": "CRUDEOIL", "exchange": "MCX"}, {OHLC: M5}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {OHLC: M5}),
    # 15 days: chunks of seven days (the start plus six), so three requests.
    hist("history_chunked_in_weeks", {"start_date": "2026-10-19", "end_date": "2026-11-02"}, {OHLC: [M5, M5_OFFSET, DUP]}),
    hist("history_duplicate_timestamp", {}, {OHLC: DUP}),
    hist("history_short_and_unreadable_rows", {}, {OHLC: SHORT_AND_BAD}),
    hist("history_nothing_traded", {}, {OHLC: EMPTY}),
    hist("history_refused", {}, {OHLC: REFUSED}),
    hist("history_one_chunk_refused", {"start_date": "2026-10-26", "end_date": "2026-11-02"}, {OHLC: [M5, REFUSED]}),
    hist("history_unsupported_interval", {"interval": "4h"}, {OHLC: M5}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {OHLC: M5}),
    hist("history_end_before_start", {"start_date": "2026-11-03", "end_date": "2026-11-02"}, {OHLC: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]
