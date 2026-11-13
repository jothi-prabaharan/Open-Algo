"""History on TradeSmart: one TPSeries call for intraday candles, one EODChartData call for daily ones, with the pre-open bar dropped and bad rows skipped."""

import json
from datetime import datetime as _datetime

import scenarios_tradesmart as s
import scenarios_history_shoonya as _h
from scenarios_history_shoonya import RECORDED_AT, T, bar, hist, NO_DATA, EXPIRED, M5, FUT  # noqa: F401
from scenarios_tradesmart import AUTH_TOKEN, ENV as _ENV, SEED_API_KEY_ROW, BASE, quote  # noqa: F401

BROKER = "tradesmart"
ENV = {**_ENV, "TZ": "Asia/Kolkata"}
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [
    {"symbol": "SENSEX25NOV26FUT", "brsymbol": "SENSEX25NOV26F", "name": "SENSEX", "exchange": "BFO", "brexchange": "BFO", "token": "60001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 20, "instrumenttype": "FUTIDX", "tick_size": 0.05},
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]
READ_POSTS = s.READ_POSTS + [f"{BASE}/TPSeries", f"{BASE}/EODChartData"]
TP, EOD, Q = f"POST {BASE}/TPSeries", f"POST {BASE}/EODChartData", f"POST {BASE}/GetQuotes"


def dbar(ts, o, h, l, c, v, **extra):
    d = {"stat": "Ok", "time": "x", "ssboe": str(ts), "into": str(o), "inth": str(h), "intl": str(l), "intc": str(c), "intv": str(v)}
    d.update(extra)
    return json.dumps(d)


DAILY = {"body": [dbar(1793232000, 790.0, 801.0, 788.5, 799.95, 9100000), dbar(1793318400, 800.0, 806.0, 797.0, 805.45, 8300000)]}
DAILY_WIDE = {"body": [dbar(1793232000, 790.0, 795.0, 788.5, 799.95, 9100000), dbar(1793318400, 800.0, 806.0, 802.0, 801.0, -8300000)]}
DAILY_BAD = {"body": [dbar(1793232000, 790.0, 801.0, 788.5, 799.95, 9100000), {"stat": "Ok", "into": "800", "inth": "806", "intl": "797", "intc": "805", "intv": "5"},
                      dbar(1793318400, 0, 0, 0, 0, 0), dbar(1793404800, None, 806, 797, 805, 5), dbar(1793491200, "", 806, 797, 805, 5)]}
PRE_OPEN = {"body": [bar(T - 60, 800.0, 800.0, 800.0, 800.0, 0), bar(T, 800.0, 802.5, 799.0, 801.25, 120000), bar(T + 300, 801.25, 803.0, 800.5, 802.75, 98000)]}
NEG = {"body": [bar(T, 800.0, 802.5, 799.0, 801.25, 120000), bar(T + 300, 801.25, 803.0, 800.5, 802.75, -13357992)]}
BAD = {"body": [bar(T, 0, 0, 0, 0, 5), bar(T + 300, 802.0, 801.0, 803.0, 802.5, 100), {"stat": "Ok", "time": "garbled", "into": "810", "inth": "811", "intl": "809", "intc": "810.5", "intv": "10"},
                bar(T + 600, None, 1, 1, 1, 1), bar(T + 900, 810, 811, 809, 810.5, None), bar(T + 1200, 810, 811, 809, 810.5, 10, oi="x"), bar(T + 1500, "x", 1, 1, 1, 1)]}
DUP = _h.DUP
MCX_OPEN = {"body": [bar(T - 15300, 6100.0, 6110.0, 6095.0, 6105.0, 40), bar(T - 15000, 6105.0, 6112.0, 6100.0, 6108.0, 30)]}
TEXT_ROW = {"body": [bar(T, 800.0, 802.5, 799.0, 801.25, 120000), "not json"]}
FRACTION = {"body": [bar(T, 800.0, 802.5, 799.0, 801.25, "120000.9", oi="1500.7")]}
TODAY_BAR = {"body": [dbar(1794528000, 800.0, 806.0, 797.0, 805.0, 5)]}
HTTP_DOWN = {"status": 500, "body": "<html>down</html>"}
ONLY_STAT_OK = {"body": {"stat": "Ok"}}

SCENARIOS = [
    hist("history_5m_equity_newest_first_is_sorted", {}, {TP: M5}),
    hist("history_3m", {"interval": "3m"}, {TP: M5}),
    hist("history_2h", {"interval": "2h"}, {TP: M5}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {TP: FUT}),
    hist("history_index_intraday_uses_the_cash_segment", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {TP: M5}),
    hist("history_pre_open_bar_is_dropped", {}, {TP: PRE_OPEN}),
    hist("history_pre_open_bar_on_futures_is_dropped", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {TP: PRE_OPEN}),
    hist("history_commodity_bars_before_0915_are_kept", {"symbol": "CRUDEOIL", "exchange": "MCX"}, {TP: MCX_OPEN}),
    hist("history_pre_open_bar_on_bse_futures_is_dropped", {"symbol": "SENSEX25NOV26FUT", "exchange": "BFO", "interval": "1m"}, {TP: PRE_OPEN}),
    hist("history_fractional_volume_and_open_interest_are_cut", {}, {TP: FRACTION}),
    hist("history_negative_volume_is_zeroed", {}, {TP: NEG}),
    hist("history_zero_and_unreadable_bars_are_left_out", {}, {TP: BAD}),
    hist("history_a_row_that_is_not_json_text", {}, {TP: TEXT_ROW}),
    hist("history_duplicate_timestamps_are_kept", {}, {TP: DUP}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY}),
    hist("history_daily_range_is_widened_and_volume_kept", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY_WIDE}),
    hist("history_daily_bad_rows_are_skipped", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-11-02"}, {EOD: DAILY_BAD}),
    hist("history_daily_with_todays_candle_from_a_quote", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: quote()}),
    hist("history_daily_today_quote_refused", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: {"body": {"stat": "Not_Ok", "emsg": "no data"}}}),
    hist("history_daily_chart_fails_so_the_quote_is_all_there_is", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: HTTP_DOWN, Q: quote()}),
    hist("history_daily_chart_already_has_todays_bar", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: TODAY_BAR, Q: quote()}),
    hist("history_daily_range_after_today_adds_nothing", {"interval": "D", "start_date": "2026-11-14", "end_date": "2026-11-20"}, {EOD: {"body": []}, Q: quote()}),
    hist("history_daily_chart_fails_and_today_is_out_of_range", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: HTTP_DOWN, Q: quote()}),
    hist("history_daily_chart_answers_with_an_error", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: NO_DATA}),
    hist("history_daily_chart_answers_with_an_error_today_in_range", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: NO_DATA, Q: quote()}),
    hist("history_intraday_answers_with_an_error", {}, {TP: NO_DATA}),
    hist("history_intraday_answers_with_a_bare_ok", {}, {TP: ONLY_STAT_OK}),
    hist("history_session_expired", {}, {TP: EXPIRED}),
    hist("history_not_chunked_one_call_for_a_long_range", {"interval": "1m", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {TP: M5}),
    hist("history_interval_tradesmart_lacks", {"interval": "4h"}, {TP: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]


def patch():
    import importlib

    module = importlib.import_module("broker.tradesmart.api.data")
    module.USE_ASYNC = False
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed if tz is None else fixed.astimezone(tz)

    module.datetime = _Frozen
