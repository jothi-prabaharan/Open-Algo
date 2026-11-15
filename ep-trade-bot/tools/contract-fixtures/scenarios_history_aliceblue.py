"""AliceBlue history: one POST to the chart service with millisecond bounds in IST (a day starts at a quarter past nine, or at midnight for daily; the end is the last second of the day, capped to now). Minute candles are stamped at the end of their minute; 3, 5, 10, 15, 30 and 60 minutes are built from the minutes in bins that start on the clock. A daily request asks to the following midnight."""

import scenarios_aliceblue as s
from scenarios_aliceblue import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "aliceblue"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", 1, "SENSEX", 1, "INDEX"), s._sym("USDINR28NOV26FUT", "USDINR28NOV26FUT", "BCD", 91001, "USDINR", 1000, "FUT", "28-NOV-26", -1.0, 0.0025)]
H = "POST /open-api/od/ChartAPIService/api/chart/history"


def hist(name, request, reply, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": {H: reply}, "ignore": [], **extra}


def ok(*rows):
    return {"body": {"stat": "Ok", "result": list(rows)}}


def candle(time, o=800.0, h=802.0, low=799.0, c=801.0, v=1000):
    return {"time": time, "open": o, "high": h, "low": low, "close": c, "volume": v}


def minutes(day="2026-11-02", first=(9, 15), count=7, price=800.0):
    rows = []
    start = first[0] * 60 + first[1]
    for i in range(count):
        t = start + i
        o = price + i
        rows.append(candle(f"{day} {t // 60:02d}:{t % 60:02d}:59", o, o + 2, o - 1, o + 1, 1000 + i))
    return rows


def daily(*days):
    return [candle(f"{d} 00:00:00", 800 + i, 802 + i, 799 + i, 801 + i, 1000 * (i + 1)) for i, d in enumerate(days)]


SCENARIOS = [
    hist("history_one_minute", {}, ok(*minutes())),
    hist("history_one_minute_range_of_two_days", {"end_date": "2026-11-03"}, ok(*minutes(), *minutes("2026-11-03"))),
    hist("history_three_minutes", {"interval": "3m"}, ok(*minutes(count=10))),
    hist("history_five_minutes", {"interval": "5m"}, ok(*minutes(count=12))),
    hist("history_ten_minutes", {"interval": "10m"}, ok(*minutes(count=25))),
    hist("history_fifteen_minutes", {"interval": "15m"}, ok(*minutes(count=40))),
    hist("history_thirty_minutes_start_on_the_half_hour", {"interval": "30m"}, ok(*minutes(count=70))),
    hist("history_sixty_minutes_start_on_the_hour", {"interval": "1h"}, ok(*minutes(count=130))),
    hist("history_resampled_over_two_days", {"interval": "15m", "end_date": "2026-11-03"}, ok(*minutes(count=20), *minutes("2026-11-03", count=20))),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-30", "end_date": "2026-11-03"}, ok(*daily("2026-10-30", "2026-11-02", "2026-11-03"))),
    hist("history_daily_ending_today_asks_to_the_next_midnight", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, ok(*daily("2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13"))),
    hist("history_daily_for_one_day", {"interval": "D", "start_date": "2026-11-02", "end_date": "2026-11-02"}, ok(*daily("2026-11-02"))),
    hist("history_the_current_day_ends_at_now", {"start_date": "2026-11-13", "end_date": "2026-11-13"}, ok(*minutes("2026-11-13", count=5))),
    hist("history_an_end_in_the_future_is_capped_to_now", {"start_date": "2026-11-12", "end_date": "2026-11-20"}, ok(*minutes("2026-11-12", count=3))),
    hist("history_a_start_in_the_future_is_no_candles", {"start_date": "2026-11-20", "end_date": "2026-11-21"}, ok(*minutes("2026-11-20", count=3))),
    hist("history_an_index", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, ok(*minutes())),
    hist("history_the_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, ok(*minutes())),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, ok(*minutes())),
    hist("history_a_derivative", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, ok(*minutes())),
    hist("history_a_commodity", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, ok(*minutes())),
    hist("history_token_with_a_decimal_point", {"symbol": "ODDTOKEN"}, ok(*minutes())),
    hist("history_bcd_is_not_served", {"symbol": "USDINR28NOV26FUT", "exchange": "BCD"}, ok(*minutes())),
    hist("history_no_data_available", {}, {"body": {"stat": "Not_Ok", "emsg": "No data available"}}),
    hist("history_not_ok_in_other_case", {}, {"body": {"stat": "not ok", "emsg": "Session expired"}}),
    hist("history_answer_without_a_result", {}, {"body": {"stat": "Ok"}}),
    hist("history_empty_result", {}, ok()),
    hist("history_http_error_is_no_candles", {}, {"status": 500, "body": {"x": 1}}),
    hist("history_not_json_is_no_candles", {}, {"status": 200, "body": "<html>"}),
    hist("history_missing_columns_is_no_candles", {}, {"body": {"stat": "Ok", "result": [{"time": "2026-11-02 09:15:59", "open": 1, "high": 2, "low": 0.5, "close": 1.5}]}}),
    hist("history_a_time_that_does_not_parse_is_no_candles", {}, ok(candle("garbage"), *minutes(count=2))),
    hist("history_a_price_that_is_not_a_number_is_no_candles", {}, ok(candle("2026-11-02 09:15:59", o="x"))),
    hist("history_prices_as_text_are_numbers", {}, ok(candle("2026-11-02 09:15:59", o="800.5", h="802", low="799", c="801", v="1200"))),
    hist("history_duplicate_times_keep_the_first", {}, ok(candle("2026-11-02 09:15:59", 800), candle("2026-11-02 09:15:30", 900), candle("2026-11-02 09:16:59", 801))),
    hist("history_rows_out_of_order_are_sorted", {}, ok(candle("2026-11-02 09:16:59", 801), candle("2026-11-02 09:15:59", 800))),
    hist("history_seconds_are_floored_to_the_minute", {}, ok(candle("2026-11-02 09:15:00", 800), candle("2026-11-02 09:16:30", 801), candle("2026-11-02 09:17:59", 802))),
    hist("history_daily_time_of_day_is_dropped", {"interval": "D", "start_date": "2026-11-02", "end_date": "2026-11-03"}, ok(candle("2026-11-02 05:30:00"), candle("2026-11-03 18:30:00", 801))),
    hist("history_a_gap_in_the_minutes_makes_no_empty_candle", {"interval": "5m"}, ok(*minutes(count=3), *minutes(first=(9, 45), count=3))),
    hist("history_candles_outside_the_range_are_kept_as_sent", {}, ok(*minutes("2026-11-01", count=2), *minutes(count=2), *minutes("2026-11-03", count=2))),
    hist("history_an_interval_aliceblue_lacks", {"interval": "2m"}, ok(*minutes())),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, ok()),
]


def patch():
    import importlib
    import time as _time
    from datetime import datetime as _datetime

    limiter = importlib.import_module("broker.aliceblue.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
    limiter.reset()
    data = importlib.import_module("broker.aliceblue.api.data")
    data.time.sleep = lambda *a, **k: None
    fixed = _datetime.fromisoformat(RECORDED_AT).timestamp()
    _time.time = lambda: fixed


def before_each(sc):
    import importlib

    importlib.import_module("broker.aliceblue.api.rate_limiter").reset()
