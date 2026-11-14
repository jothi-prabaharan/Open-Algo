"""Kotak Neo history: GET /market-data/1.0/historical/details?neosymbol=segment|token&fromdate&todate&interval, positional candles, in chunks of days that depend on the interval. An index also tries its names. An empty range is a 400 fault read as no candles. A 429 is retried with a growing wait."""

from datetime import datetime as _datetime

import scenarios_kotak as s
from scenarios_kotak import AUTH_TOKEN, ENV, ENCODE_PIPE, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "kotak"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
HP = "GET /market-data/1.0/historical/details"
SYMBOLS = s.SYMBOLS + [
    s._sym("NIFTY", "Nifty 50", "NSE_INDEX", "nse_cm", 26000, "NIFTY", 1, "INDEX"),
    s._sym("MIDCPNIFTY", "Nifty Mid Select", "NSE_INDEX", "nse_cm", 26074, "MIDCPNIFTY", 1, "INDEX"),
    s._sym("INDIAVIX", "India VIX", "NSE_INDEX", "nse_cm", 26017, "INDIAVIX", 1, "INDEX"),
]


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def c(stamp="2026-11-02T09:15:00+0530", o="800.00", h="802.00", l="799.00", cl="801.00", v="1000", oi="0"):
    return [stamp, o, h, l, cl, v, oi]


def ok(*candles):
    return {"body": {"status": "success", "data": {"candles": list(candles)}}}


NO_DATA = {"status": 400, "body": {"fault": {"message": "No data found"}}}
NOT_YET = {"status": 400, "body": {"fault": {"message": "Data not available. Market has not yet opened"}}}
BAD = {"status": 400, "body": {"fault": {"message": "Invalid neosymbol"}}}
LIMITED = {"status": 429, "body": {"message": "too many request received"}}
INTRA = ok(c(), c("2026-11-02T09:20:00+0530", "801", "803", "800", "802", "2000"), c("2026-11-02T09:25:00+0530", "802", "804", "801", "803", "3000"))
DAILY = ok(c("2026-10-30T00:00:00+0530"), c("2026-11-02T00:00:00+0530", "801", "803", "800", "802", "2000"), c("2026-11-03T09:15:00+0530", "802", "804", "801", "803", "3000"))

SCENARIOS = [
    hist("history_5m_in_one_call", {}, {HP: INTRA}),
    hist("history_1m", {"interval": "1m"}, {HP: INTRA}),
    hist("history_3m", {"interval": "3m"}, {HP: INTRA}),
    hist("history_10m", {"interval": "10m"}, {HP: INTRA}),
    hist("history_15m", {"interval": "15m"}, {HP: INTRA}),
    hist("history_30m", {"interval": "30m"}, {HP: INTRA}),
    hist("history_1h", {"interval": "1h"}, {HP: INTRA}),
    hist("history_60m_alias", {"interval": "60m"}, {HP: INTRA}),
    hist("history_daily_is_stamped_at_midnight_of_its_date", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, {HP: DAILY}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-10-01", "end_date": "2026-11-03"}, {HP: DAILY}),
    hist("history_a_long_range_goes_in_chunks", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: [INTRA, ok(c("2026-10-05T09:15:00+0530")), ok(c("2026-11-02T09:30:00+0530"))]}),
    hist("history_daily_chunks_of_a_hundred_and_eighty_days", {"interval": "D", "start_date": "2026-01-01", "end_date": "2026-11-02"}, {HP: [DAILY, ok(c("2026-10-30T00:00:00+0530"))]}),
    hist("history_ten_minute_chunks_of_sixty_days", {"interval": "10m", "start_date": "2026-08-01", "end_date": "2026-11-02"}, {HP: [INTRA, ok(c("2026-10-05T09:15:00+0530"))]}),
    hist("history_chunks_that_overlap_keep_the_first", {"start_date": "2026-09-01", "end_date": "2026-10-05"}, {HP: [ok(c(), c("2026-11-02T09:20:00+0530", "1", "2", "0.5", "1.5", "5")), ok(c("2026-11-02T09:20:00+0530", "9", "9", "9", "9", "9"), c("2026-10-05T09:15:00+0530"))]}),
    hist("history_candles_out_of_order_are_sorted", {}, {HP: ok(c("2026-11-02T09:25:00+0530"), c(), c("2026-11-02T09:20:00+0530"))}),
    hist("history_an_open_outside_its_wick_widens_it", {}, {HP: ok(c(o="798.00"), c("2026-11-02T09:20:00+0530", "803", "802", "799", "804"))}),
    hist("history_a_negative_volume_is_zero", {}, {HP: ok(c(v="-2800171"))}),
    hist("history_a_candle_with_no_price_is_dropped", {}, {HP: ok(c(o="x"), c("2026-11-02T09:20:00+0530"))}),
    hist("history_a_short_row_is_padded", {}, {HP: ok(["2026-11-02T09:15:00+0530", "800", "802", "799", "801"])}),
    hist("history_a_row_that_is_too_short_is_skipped", {}, {HP: ok(["2026-11-02T09:15:00+0530", "800", "802", "799"], c("2026-11-02T09:20:00+0530"))}),
    hist("history_a_row_that_is_not_a_list_is_skipped", {}, {HP: ok({"t": 1}, c())}),
    hist("history_numbers_as_text_and_float_volume", {}, {HP: ok(c(v="16131960.9", oi="12.7"))}),
    hist("history_volume_that_is_not_a_number_is_zero", {}, {HP: ok(c(v="n/a", oi=None))}),
    hist("history_a_time_with_a_colon_in_the_offset", {}, {HP: ok(c("2026-11-02T09:15:00+05:30"))}),
    hist("history_a_time_in_utc", {}, {HP: ok(c("2026-11-02T03:45:00Z"))}),
    hist("history_no_data_is_empty", {}, {HP: NO_DATA}),
    hist("history_market_not_yet_open_is_empty", {}, {HP: NOT_YET}),
    hist("history_the_last_chunk_having_no_data_keeps_the_rest", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: [INTRA, NO_DATA, NO_DATA]}),
    hist("history_an_error_fault_fails_the_pull", {}, {HP: BAD}),
    hist("history_a_failed_chunk_fails_the_pull", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: [INTRA, BAD, INTRA]}),
    hist("history_a_server_error_fails_the_pull", {}, {HP: {"status": 500, "body": {"emsg": "down"}}}),
    hist("history_an_answer_that_is_not_json", {}, {HP: {"status": 200, "body": "oops"}}),
    hist("history_an_answer_that_is_a_list", {}, {HP: {"body": [1, 2]}}),
    hist("history_a_status_that_is_not_success", {}, {HP: {"body": {"status": "error", "emsg": "bad"}}}),
    hist("history_no_candles_key", {}, {HP: {"body": {"status": "success", "data": {}}}}),
    hist("history_null_data", {}, {HP: {"body": {"status": "success", "data": None}}}),
    hist("history_rate_limited_is_tried_again", {}, {HP: [LIMITED, INTRA]}),
    hist("history_rate_limited_four_times_then_served", {}, {HP: [LIMITED, LIMITED, LIMITED, LIMITED, INTRA]}),
    hist("history_rate_limited_every_time", {}, {HP: LIMITED}),
    hist("history_index_asks_its_token_first", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {HP: INTRA}),
    hist("history_index_tries_the_next_name", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {HP: [BAD, INTRA]}),
    hist("history_index_tries_the_upper_case_name", {"symbol": "INDIAVIX", "exchange": "NSE_INDEX"}, {HP: [BAD, BAD, INTRA]}),
    hist("history_index_tries_every_spelling", {"symbol": "MIDCPNIFTY", "exchange": "NSE_INDEX"}, {HP: [BAD, BAD, BAD, BAD, BAD, BAD, BAD, BAD, BAD]}),
    hist("history_an_empty_answer_tries_the_next_name", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {HP: [ok(), INTRA]}),
    hist("history_stays_on_the_name_that_answered", {"symbol": "NIFTY", "exchange": "NSE_INDEX", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {HP: [BAD, INTRA, INTRA, INTRA]}),
    hist("history_a_future_only_range_is_empty", {"start_date": "2026-11-20", "end_date": "2026-11-21"}, {HP: ok()}),
    hist("history_before_the_horizon_is_empty", {"start_date": "2020-01-01", "end_date": "2021-01-01"}, {HP: INTRA}),
    hist("history_a_start_before_the_horizon_is_clamped", {"interval": "D", "start_date": "2020-01-01", "end_date": "2021-11-20"}, {HP: DAILY}),
    hist("history_start_after_end", {"start_date": "2026-11-03", "end_date": "2026-11-02"}, {HP: INTRA}),
    hist("history_interval_kotak_lacks", {"interval": "2m"}, {HP: INTRA}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {HP: INTRA}),
    hist("history_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {HP: INTRA}),
    hist("history_bse_derivative", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {HP: INTRA}),
    hist("history_mcx_is_quote_only", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {HP: INTRA}),
    hist("history_currency_is_quote_only", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, {HP: INTRA}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {HP: INTRA}),
]


def patch():
    import importlib

    import pandas as pd

    data = importlib.import_module("broker.kotak.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)
    data._history_pace = lambda: None
    data.time.sleep = lambda *a, **k: None
    real_now = pd.Timestamp.now

    class _Stamp(pd.Timestamp):
        @classmethod
        def now(cls, tz=None):
            return pd.Timestamp(fixed.astimezone(__import__("zoneinfo").ZoneInfo(tz)) if tz else fixed.replace(tzinfo=None))

    data.pd = type("pd_proxy", (), {"__getattr__": lambda self, n: getattr(pd, n) if n != "Timestamp" else _Stamp})()
