"""History on Zebu: one TPSeries call for intraday candles, one EODChartData call for daily ones."""

import json
from datetime import datetime as _datetime

import scenarios_history_shoonya as _h
from scenarios_history_shoonya import RECORDED_AT, SYMBOLS, TP, EOD, T, bar, hist, Q, quote, NO_DATA, EXPIRED  # noqa: F401
from scenarios_shoonya import AUTH_TOKEN, SEED_API_KEY_ROW  # noqa: F401
from scenarios_history_shoonya import READ_POSTS  # noqa: F401

BROKER = "zebu"
ENV = {"BROKER_API_KEY": "FA12345:::zebu-client-id", "BROKER_API_SECRET": "zebu-secret", "TZ": "Asia/Kolkata"}
FREEZE_CLOCK = False


def dbar(ts, o, h, l, c, v):
    return json.dumps({"stat": "Ok", "time": "x", "ssboe": str(ts), "into": str(o), "inth": str(h), "intl": str(l), "intc": str(c), "intv": str(v)})


M5 = _h.M5
DAILY = {"body": [dbar(1793232000, 790.0, 801.0, 788.5, 799.95, 9100000), dbar(1793318400, 800.0, 806.0, 797.0, 805.45, 8300000)]}
DAILY_ZERO = {"body": [dbar(1793232000, 0, 0, 0, 0, 0)]}
TODAY = 1794508200  # midnight IST on the recorded day
BAD = {"body": [bar(T, 0, 0, 0, 0, 5), bar(T + 300, 802.0, 801.0, 803.0, 802.5, 100), bar(T + 600, 800.0, 805.0, 799.0, 806.0, -50),
                {"stat": "Ok", "time": "garbled", "into": "810", "inth": "811", "intl": "809", "intc": "810.5", "intv": "10"}, bar(T + 900, "x", 1, 1, 1, 1)]}
DUP = _h.DUP

SCENARIOS = [
    hist("history_5m_equity_newest_first_is_sorted", {}, {TP: M5}),
    hist("history_1h", {"interval": "1h"}, {TP: M5}),
    hist("history_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {TP: _h.FUT}),
    hist("history_index_intraday_uses_the_cash_segment", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {TP: M5}),
    hist("history_daily", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY}),
    hist("history_daily_keeps_a_bar_with_no_price", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: DAILY_ZERO}),
    hist("history_daily_with_todays_candle_from_a_quote", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: quote()}),
    hist("history_daily_today_quote_refused", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: DAILY, Q: {"body": {"stat": "Not_Ok", "emsg": "no data"}}}),
    hist("history_daily_chart_fails_so_the_quote_is_all_there_is", {"interval": "D", "start_date": "2026-11-10", "end_date": "2026-11-13"}, {EOD: {"status": 500, "body": "<html>down</html>"}, Q: quote()}),
    hist("history_daily_chart_answers_with_an_error", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {EOD: NO_DATA}),
    hist("history_intraday_answers_with_an_error", {}, {TP: NO_DATA}),
    hist("history_zero_and_unreadable_bars_are_left_out", {}, {TP: BAD}),
    hist("history_duplicate_timestamps_are_kept", {}, {TP: DUP}),
    hist("history_not_chunked_one_call_for_a_long_range", {"interval": "1m", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {TP: M5}),
    hist("history_interval_zebu_lacks", {"interval": "20m"}, {TP: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]


def patch():
    import importlib

    module = importlib.import_module("broker.zebu.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed if tz is None else fixed.astimezone(tz)

    module.datetime = _Frozen
