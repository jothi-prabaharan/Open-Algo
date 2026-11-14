"""Samco history: daily candles from /history/candleData (indexCandleData for an index), minute candles from /intraday/candleData; a daily request that ends today stops at yesterday."""

import types
from datetime import datetime as _datetime

import scenarios_samco as s
from scenarios_samco import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401

BROKER = "samco"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
DH, DI, IH, II = "GET /history/candleData", "GET /intraday/candleData", "GET /history/indexCandleData", "GET /intraday/indexCandleData"
RATE = {"status": 429, "body": {"status": "Failure", "statusMessage": "Too many requests"}}
DOWN = {"status": 500, "body": {"status": "Failure", "statusMessage": "Internal error"}}
SYMBOLS = s.SYMBOLS + [s._sym("BANKNIFTY", "NIFTY BANK", "NSE_INDEX", 26009, "BANKNIFTY", 1, "INDEX"), s._sym("SENSEX", "SENSEX", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX")]


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def day(date, o="800.00", h="802.00", l="799.00", c="801.00", v="1,000", **extra):
    d = {"date": date, "open": o, "high": h, "low": l, "close": c, "volume": v}
    d.update(extra)
    return d


def bar(stamp, o="800.00", h="802.00", l="799.00", c="801.00", v="1000", **extra):
    d = {"dateTime": stamp, "open": o, "high": h, "low": l, "close": c, "volume": v}
    d.update(extra)
    return d


def ok(key, *rows):
    return {"body": {"status": "Success", key: list(rows)}}


INTRA = ok("intradayCandleData", bar("2026-11-02 09:15:00"), bar("2026-11-02 09:20:00", "801", "803", "800", "802", "2000"), bar("2026-11-02 09:25:00", "802", "804", "801", "803", "3000"))
DAILY = ok("historicalCandleData", day("2026-10-29"), day("2026-10-30", "801", "803", "800", "802", "2,000"), day("2026-11-02", "802", "804", "801", "803", "3,000"))
SCENARIOS = [
    hist("history_5m_equity", {}, {DI: INTRA}),
    hist("history_1m_has_no_interval_parameter", {"interval": "1m"}, {DI: INTRA}),
    hist("history_10m", {"interval": "10m"}, {DI: INTRA}),
    hist("history_15m", {"interval": "15m"}, {DI: INTRA}),
    hist("history_30m", {"interval": "30m"}, {DI: INTRA}),
    hist("history_1h_is_sixty_minutes", {"interval": "1h"}, {DI: INTRA}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {DH: DAILY}),
    hist("history_daily_ending_today_stops_at_yesterday", {"interval": "D", "start_date": "2026-11-09", "end_date": "2026-11-13"}, {DH: DAILY}),
    hist("history_daily_for_today_only_keeps_today", {"interval": "D", "start_date": "2026-11-13", "end_date": "2026-11-13"}, {DH: DAILY}),
    hist("history_intraday_ending_today_is_not_cut", {"start_date": "2026-11-12", "end_date": "2026-11-13"}, {DI: INTRA}),
    hist("history_unsorted_and_repeated_rows", {}, {DI: ok("intradayCandleData", bar("2026-11-02 09:25:00"), bar("2026-11-02 09:15:00"), bar("2026-11-02 09:20:00"), bar("2026-11-02 09:15:00", "1", "2", "0.5", "1.5", "5"))}),
    hist("history_text_numbers_with_commas", {}, {DI: ok("intradayCandleData", bar("2026-11-02 09:15:00", "1,800.50", "1,802.00", "1,799.00", "1,801.25", "1,234,567"))}),
    hist("history_numbers_that_are_not_numbers_are_zero", {}, {DI: ok("intradayCandleData", bar("2026-11-02 09:15:00", "x", None, "", "--", "n/a"))}),
    hist("history_open_interest_is_kept", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {DI: ok("intradayCandleData", bar("2026-11-02 09:15:00", oi=12000), bar("2026-11-02 09:16:00", oi=12500))}),
    hist("history_daily_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {DH: ok("historicalCandleData", day("2026-10-29", oi=1000), day("2026-10-30", oi=1100))}),
    hist("history_commodity_sends_its_exchange", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "1m"}, {DI: INTRA}),
    hist("history_commodity_daily", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {DH: DAILY}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {DI: INTRA}),
    hist("history_currency", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS", "interval": "15m"}, {DI: INTRA}),
    hist("history_index_intraday_under_its_observed_key", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {II: ok("intradayCandleData", bar("2026-11-02 09:15:00"), bar("2026-11-02 09:20:00"))}),
    hist("history_index_intraday_under_the_documented_key", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {II: ok("indexIntraDayCandleData", bar("2026-11-02 09:15:00"))}),
    hist("history_index_daily", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {IH: ok("indexCandleData", day("2026-10-29"), day("2026-10-30"))}),
    hist("history_index_daily_under_the_generic_key", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {IH: ok("historicalCandleData", day("2026-10-29"))}),
    hist("history_bank_index_name_is_encoded", {"symbol": "BANKNIFTY", "exchange": "NSE_INDEX", "interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {IH: ok("indexCandleData", day("2026-10-29"))}),
    hist("history_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "interval": "30m"}, {II: ok("intradayCandleData", bar("2026-11-02 09:15:00"))}),
    hist("history_no_candles_is_empty", {}, {DI: ok("intradayCandleData")}),
    hist("history_daily_no_candles_is_empty", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {DH: ok("historicalCandleData")}),
    hist("history_refused_is_empty", {}, {DI: {"body": {"status": "Failure", "statusMessage": "Invalid symbol"}}}),
    hist("history_session_refused", {}, {DI: {"status": 403, "body": {"status": "Failure", "statusMessage": "Forbidden"}}}),
    hist("history_rate_limited_is_tried_again", {}, {DI: [RATE, RATE, INTRA]}),
    hist("history_rate_limited_every_time", {}, {DI: [RATE, RATE, RATE, RATE, INTRA]}),
    hist("history_server_error_is_tried_again", {}, {DI: [DOWN, INTRA]}),
    hist("history_server_error_every_time", {}, {DI: [DOWN, DOWN, DOWN, DOWN, INTRA]}),
    hist("history_interval_samco_lacks", {"interval": "3m"}, {DI: INTRA}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {DI: INTRA}),
]


def patch():
    import importlib

    import pandas as pandas

    data = importlib.import_module("broker.samco.api.data")
    data.time.sleep = lambda seconds: None
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Pandas:
        Timestamp = types.SimpleNamespace(now=staticmethod(lambda: pandas.Timestamp(fixed.replace(tzinfo=None))))

        def __getattr__(self, name):
            return getattr(pandas, name)

    data.pd = _Pandas()
