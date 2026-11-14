"""IIFL Capital history: one POST /marketdata/historicaldata for the whole range, the interval spelt out ("5 minutes") and the dates as dd-Mon-yyyy; candles are read from several row shapes."""

import scenarios_iiflcapital as s
from scenarios_iiflcapital import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "iiflcapital"
HEP = "POST /v1/marketdata/historicaldata"
T = 1793591100  # 2026-11-02 09:15 IST
LIMITED = {"status": 429, "body": {"status": "error", "message": "Too many requests"}}


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


def c(ts, o=800.0, h=802.0, l=799.0, close=801.0, v=1000, *oi):
    return [ts, o, h, l, close, v, *oi]


def ok(*rows):
    return {"body": {"status": "Ok", "result": list(rows)}}


LIST3 = ok(c(T), c(T + 300, 801, 803, 800, 802, 2000), c(T + 600, 802, 804, 801, 803, 3000))
SCENARIOS = [
    hist("history_5m_equity", {}, {HEP: LIST3}),
    hist("history_1m", {"interval": "1m"}, {HEP: LIST3}),
    hist("history_10m", {"interval": "10m"}, {HEP: LIST3}),
    hist("history_15m", {"interval": "15m"}, {HEP: LIST3}),
    hist("history_30m", {"interval": "30m"}, {HEP: LIST3}),
    hist("history_60m", {"interval": "60m"}, {HEP: LIST3}),
    hist("history_1h", {"interval": "1h"}, {HEP: LIST3}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {HEP: ok(c(1793212200), c(1793298600))}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {HEP: ok(c(1793212200))}),
    hist("history_monthly", {"interval": "M", "start_date": "2026-01-01", "end_date": "2026-11-02"}, {HEP: ok(c(1793212200))}),
    hist("history_unsorted_rows_are_sorted", {}, {HEP: ok(c(T + 600), c(T), c(T + 300))}),
    hist("history_milliseconds_are_seconds", {}, {HEP: ok(c(T * 1000), c((T + 300) * 1000))}),
    hist("history_text_timestamps_are_read", {}, {HEP: ok(c("2026-11-02T09:15:00"), c("2026-11-02 09:20:00"), c("1793591700"))}),
    hist("history_open_interest_in_the_seventh_column", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {HEP: ok(c(T, 24000, 24010, 23990, 24005, 100, 12000), c(T + 60, 24005, 24015, 24000, 24010, 200, 12500))}),
    hist("history_candles_nested_in_a_row", {}, {HEP: ok({"candles": [c(T), c(T + 300)]})}),
    hist("history_rows_as_objects", {}, {HEP: ok({"timestamp": T, "open": 800, "high": 802, "low": 799, "close": 801, "volume": 1000, "oi": 5}, {"time": T + 300, "o": 801, "h": 803, "l": 800, "c": 802, "v": 2000, "openInterest": 6})}),
    hist("history_rows_as_pipe_text", {}, {HEP: ok(f"{T}|800|802|799|801|1000|7,{T + 300}|801|803|800|802|2000")}),
    hist("history_a_short_list_row_is_a_zero_candle", {}, {HEP: ok(c(T), [T + 300, 1, 2])}),
    hist("history_text_numbers", {}, {HEP: ok([str(T), "800.5", "802", "799", "801", "1000"])}),
    hist("history_rows_in_a_data_container", {}, {HEP: {"body": {"status": "Ok", "data": {"candles": [c(T), c(T + 300)]}}}}),
    hist("history_no_candles_is_empty", {}, {HEP: ok()}),
    hist("history_refused_with_a_message", {}, {HEP: {"status": 400, "body": {"status": "error", "message": "Invalid instrument"}}}),
    hist("history_refused_with_nothing_useful", {}, {HEP: {"status": 500, "body": {"x": 1}}}),
    hist("history_expired_session", {}, {HEP: {"status": 401, "body": {"status": "error", "message": "Invalid session"}}}),
    hist("history_a_rate_limited_call_is_tried_again", {}, {HEP: [LIMITED, LIMITED, LIST3]}),
    hist("history_rate_limited_every_time", {}, {HEP: [LIMITED, LIMITED, LIMITED, LIMITED, LIST3]}),
    hist("history_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {HEP: LIST3}),
    hist("history_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "1m"}, {HEP: LIST3}),
    hist("history_interval_iifl_lacks", {"interval": "3m"}, {HEP: LIST3}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {HEP: LIST3}),
]


def patch():
    pass
