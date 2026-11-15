"""Groww history: minute to 4-hour candles come from GET /v1/historical/candles (a groww symbol such as NSE-SBIN or NSE-NIFTY-25Nov26-FUT, the candle interval, a start and end time), cut into requests of the longest range Groww allows for the interval; daily and weekly come from
GET /v1/historical/candle/range (the trading symbol and the interval in minutes), stamped at midnight UTC of their IST date. 30-minute, 1-hour and 4-hour candles are built from 15-minute ones aligned to the 09:15 open; a candle wholly in the pre-open session, or with no open, is left out."""

import scenarios_groww as s
from scenarios_groww import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401

BROKER = "groww"
READ_POSTS = []
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [
    s._sym("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "NIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "SENSEX", 1, "INDEX"),
    s._sym("NIFTY25NOV2624000.5CE", "NIFTY26NOV24000.5CE", "NFO", "NSE", 49601, "NIFTY", 75, "CE", "25-NOV-26", 24000.5, 0.05),
    s._sym("NIFTY25NOV2624000PE", "NIFTY26NOV24000PE", "NFO", "NSE", 49602, "NIFTY", 75, "PE", "25-NOV-26", 24000.0, 0.05),
    s._sym("BADFNO", "BADFNO", "NFO", "NSE", 49603, "BADFNO", 75, "FUT", "25-NOV-26", -1.0, 0.05),
    s._sym("NOEXPIRYFUT25NOV26FUT", "NOEXPIRYFUT26NOVFUT", "NFO", "NSE", 49604, "NOEXPIRYFUT", 75, "FUT", "", -1.0, 0.05),
]
C = "GET /v1/historical/candles"
R = "GET /v1/historical/candle/range"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


def candles(*rows):
    return s.ok({"candles": list(rows), "closing_price": None, "start_time": "x", "end_time": "y", "interval_in_minutes": 1})


def row(stamp, o=800.0, h=802.0, low=799.0, c=801.0, v=1000, *rest):
    return [stamp, o, h, low, c, v, *rest]


def session(day="2026-11-02", first=(9, 15), count=7, step=1, price=800.0):
    out = []
    t = first[0] * 60 + first[1]
    for i in range(count):
        m = t + i * step
        o = price + i
        out.append(row(f"{day}T{m // 60:02d}:{m % 60:02d}:00", o, o + 2, o - 1, o + 1, 1000 + i))
    return out


def eod(*days):
    base = 1793577600  # 2026-11-02 00:00 IST in seconds? kept as is: any instant of the day works
    return [[base + 86400 * i, 800 + i, 802 + i, 799 + i, 801 + i, 1000 * (i + 1)] for i, _ in enumerate(days)]


SCENARIOS = [
    hist("history_one_minute", {}, {C: candles(*session())}),
    hist("history_one_minute_has_a_pre_open_candle_left_out", {}, {C: candles(row("2026-11-02T09:00:00", None, None, None, None, 500), row("2026-11-02T09:14:00", None, None, None, None, 300), *session())}),
    hist("history_a_candle_that_reaches_into_the_session_is_kept", {"interval": "15m"}, {C: candles(row("2026-11-02T09:00:00", 800.0, 805.0, 795.0, 802.0, 5000), *session(step=15, count=5))}),
    hist("history_a_candle_with_no_open_is_left_out", {}, {C: candles(row("2026-11-02T09:15:00", None, 802.0, 799.0, 801.0, 1000), *session(first=(9, 16)))}),
    hist("history_two_minutes", {"interval": "2m"}, {C: candles(*session(step=2))}),
    hist("history_three_minutes", {"interval": "3m"}, {C: candles(*session(step=3))}),
    hist("history_five_minutes", {"interval": "5m"}, {C: candles(*session(step=5))}),
    hist("history_ten_minutes", {"interval": "10m"}, {C: candles(*session(step=10))}),
    hist("history_fifteen_minutes", {"interval": "15m"}, {C: candles(*session(step=15, count=8))}),
    hist("history_thirty_minutes_are_built_from_fifteen", {"interval": "30m"}, {C: candles(*session(step=15, count=8))}),
    hist("history_one_hour_is_built_from_fifteen", {"interval": "1h"}, {C: candles(*session(step=15, count=12))}),
    hist("history_four_hours_is_built_from_fifteen", {"interval": "4h"}, {C: candles(*session(step=15, count=24))}),
    hist("history_built_candles_of_two_days", {"interval": "1h", "end_date": "2026-11-03"}, {C: candles(*session(step=15, count=8), *session("2026-11-03", step=15, count=8, price=810.0))}),
    hist("history_built_candles_with_volume_missing_on_some", {"interval": "30m"}, {C: candles(row("2026-11-02T09:15:00", 800, 802, 799, 801, None), row("2026-11-02T09:30:00", 801, 803, 800, 802, 50), row("2026-11-02T09:45:00", 802, 804, 801, 803, None))}),
    hist("history_built_candles_with_a_high_missing", {"interval": "30m"}, {C: candles(row("2026-11-02T09:15:00", 800, None, 799, 801, 10), row("2026-11-02T09:30:00", 801, 803, None, 802, 50))}),
    hist("history_daily", {"interval": "D", "end_date": "2026-11-04"}, {R: candles(*eod("a", "b", "c"))}),
    hist("history_daily_in_milliseconds", {"interval": "D"}, {R: candles([1793577600000, 800, 802, 799, 801, 1000])}),
    hist("history_weekly", {"interval": "W", "end_date": "2026-11-09"}, {R: candles(*eod("a", "b"))}),
    hist("history_daily_with_no_volume", {"interval": "D"}, {R: candles([1793577600, 800, 802, 799, 801, None])}),
    hist("history_daily_with_no_open_is_kept", {"interval": "D"}, {R: candles([1793577600, None, 802, 799, 801, 10])}),
    hist("history_daily_split_into_chunks", {"interval": "D", "start_date": "2022-01-01", "end_date": "2026-11-01"}, {R: [candles([1640995200, 800, 802, 799, 801, 10]), candles([1672531200, 810, 812, 809, 811, 20]), candles([1704067200, 820, 822, 819, 821, 30]), candles([1735689600, 830, 832, 829, 831, 40]), candles([1767225600, 840, 842, 839, 841, 50])]}),
    hist("history_one_minute_over_thirty_days_is_two_requests", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {C: [candles(*session("2026-10-01")), candles(*session("2026-11-02"))]}),
    hist("history_candles_that_repeat_a_time_are_kept_once", {}, {C: candles(*session(count=3), *session(count=3))}),
    hist("history_candles_out_of_order_are_sorted", {}, {C: candles(*reversed(session(count=4)))}),
    hist("history_no_candles", {}, {C: candles()}),
    hist("history_no_payload", {}, {C: {"body": {"status": "SUCCESS"}}}),
    hist("history_an_index_has_no_volume", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {C: candles(*[r[:5] + [None] for r in session()])}),
    hist("history_a_candle_with_five_fields", {}, {C: candles(*[r[:5] for r in session(count=3)])}),
    hist("history_a_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {C: candles(*session())}),
    hist("history_a_bse_index_daily", {"symbol": "SENSEX", "exchange": "BSE_INDEX", "interval": "D"}, {R: candles([1793577600, 80000, 80500, 79900, 80100, None])}),
    hist("history_a_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {C: candles(*session(price=24000.0))}),
    hist("history_a_bse_future", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {C: candles(*session(price=80000.0))}),
    hist("history_an_option", {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, {C: candles(*session(price=100.0))}),
    hist("history_an_option_with_a_fractional_strike", {"symbol": "NIFTY25NOV2624000.5CE", "exchange": "NFO"}, {C: candles(*session(price=100.0))}),
    hist("history_a_put", {"symbol": "NIFTY25NOV2624000PE", "exchange": "NFO"}, {C: candles(*session(price=100.0))}),
    hist("history_a_future_daily", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "D"}, {R: candles([1793577600, 24000, 24100, 23900, 24050, 500])}),
    hist("history_a_derivative_not_in_openalgo_format", {"symbol": "BADFNO", "exchange": "NFO"}, {}),
    hist("history_a_derivative_with_no_expiry", {"symbol": "NOEXPIRYFUT25NOV26FUT", "exchange": "NFO"}, {}),
    hist("history_an_interval_groww_does_not_have", {"interval": "1s"}, {}),
    hist("history_monthly_is_refused", {"interval": "M"}, {}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
    hist("history_refused_with_a_reason", {}, {C: s.fail(400, "Invalid groww_symbol", "GA070")}),
    hist("history_refused_with_no_reason", {}, {C: {"status": 400, "body": {}}}),
    hist("history_server_error_with_no_json", {}, {C: {"status": 500, "body": "oops"}}),
    hist("history_failure_status_in_a_200", {}, {C: {"body": {"status": "FAILURE", "error": {"message": "Not allowed"}}}}),
    hist("history_throttled_then_answered", {}, {C: [s.THROTTLED, candles(*session())]}),
    hist("history_throttled_every_time", {}, {C: s.THROTTLED}),
    hist("history_second_chunk_refused", {"start_date": "2026-10-01", "end_date": "2026-11-02"}, {C: [candles(*session("2026-10-01")), s.fail(400, "Bad range", "GA071")]}),
    hist("history_answer_is_not_json", {}, {C: s.NOT_JSON}),
]


def before_each(sc):
    s.before_each(sc)
