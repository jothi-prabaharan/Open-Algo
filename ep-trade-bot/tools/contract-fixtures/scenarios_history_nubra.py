"""Nubra history: POST /charts/timeseries with the broker symbol, the segment and instrument type (STOCK, INDEX, FUT, OPT), the fields and the range as ISO times, in chunks of days that depend on the interval (7 for seconds, 30 for minutes up to 2, 60, 90 for half hours and hours, 365 daily,
1000 weekly, 1500 monthly), a second apart, never past the present. The answer holds a series for each field, each point {ts in nanoseconds, v in paise}; the candle at a time is the fields found at it, and the volume is added only to a time that has a price."""

import scenarios_nubra as s
from scenarios_nubra import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "nubra"
SYMBOLS = list(s.SYMBOLS) + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "SENSEX", 1, "INDEX")]
H = "POST /charts/timeseries"
NS = 1_000_000_000


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, "ignore": [], **extra}


T0 = 1793594700  # 2026-11-02 04:45 UTC, 10:15 IST


def series(br="SBIN", n=3, step=60, base=80000, volume=True, t0=T0, **extra):
    def points(shift):
        return [{"ts": (t0 + step * i) * NS, "v": base + shift + i * 10} for i in range(n)]

    data = {"open": points(0), "high": points(20), "low": points(-20), "close": points(5)}
    if volume:
        data["tick_volume"] = [{"ts": (t0 + step * i) * NS, "v": 1000 + i} for i in range(n)]
    data.update(extra)
    return {"body": {"message": "charts", "result": [{"exchange": "NSE", "type": "STOCK", "values": [{br: data}]}]}}


EMPTY = {"body": {"message": "charts", "result": []}}
SCENARIOS = [
    hist("history_one_minute", {}, {H: series()}),
    hist("history_two_minutes", {"interval": "2m"}, {H: series()}),
    hist("history_three_minutes", {"interval": "3m"}, {H: series()}),
    hist("history_five_minutes", {"interval": "5m"}, {H: series(step=300)}),
    hist("history_fifteen_minutes", {"interval": "15m"}, {H: series(step=900)}),
    hist("history_thirty_minutes", {"interval": "30m"}, {H: series(step=1800)}),
    hist("history_one_hour", {"interval": "1h"}, {H: series(step=3600)}),
    hist("history_one_second", {"interval": "1s"}, {H: series(step=1)}),
    hist("history_daily_is_cut_to_the_day", {"interval": "D", "start_date": "2026-10-28", "end_date": "2026-11-02"}, {H: series(step=86400, t0=T0 - 86400 * 3)}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-09-01", "end_date": "2026-11-02"}, {H: series(step=604800, t0=T0 - 604800 * 4)}),
    hist("history_monthly", {"interval": "M", "start_date": "2026-01-01", "end_date": "2026-11-02"}, {H: series(step=2592000, t0=T0 - 2592000 * 4)}),
    hist("history_an_interval_nubra_does_not_have", {"interval": "10m"}, {H: series()}),
    hist("history_an_index_has_no_volume_field", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {H: series("NIFTY", volume=False)}),
    hist("history_a_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {H: series("SENSEX", volume=False)}),
    hist("history_a_future", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {H: series("NIFTY26NOVFUT")}),
    hist("history_an_option", {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, {H: series("NIFTY26NOV24000CE")}),
    hist("history_a_bse_future", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {H: series("SENSEX26NOVFUT")}),
    hist("history_a_commodity_future", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {H: series("GOLDM26NOVFUT")}),
    hist("history_bse_equity", {"symbol": "TATAMOTORS", "exchange": "BSE"}, {H: series("TATAMOTORS")}),
    hist("history_the_volume_comes_from_cumulative_volume_when_there_is_no_tick_volume", {}, {H: series(volume=False, cumulative_volume=[{"ts": T0 * NS, "v": 5000}])}),
    hist("history_a_volume_for_a_time_with_no_price_is_dropped", {}, {H: series(tick_volume=[{"ts": (T0 + 999) * NS, "v": 7}])}),
    hist("history_a_series_missing_a_field_reads_it_as_zero", {}, {H: {"body": {"message": "charts", "result": [{"values": [{"SBIN": {"open": [{"ts": T0 * NS, "v": 80000}], "close": [{"ts": T0 * NS, "v": 80100}]}}]}]}}}),
    hist("history_a_point_with_no_value_is_zero", {}, {H: {"body": {"message": "charts", "result": [{"values": [{"SBIN": {"open": [{"ts": T0 * NS}], "high": [], "low": [], "close": []}}]}]}}}),
    hist("history_the_symbol_is_found_in_a_later_entry", {}, {H: {"body": {"message": "charts", "result": [{"values": [{"OTHER": {"open": []}}, series()["body"]["result"][0]["values"][0]]}]}}}),
    hist("history_another_symbol_only", {}, {H: series("OTHER")}),
    hist("history_no_result", {}, {H: EMPTY}),
    hist("history_a_message_that_is_not_charts", {}, {H: {"body": {"message": "something else", "result": []}}}),
    hist("history_rejected_with_an_error", {}, {H: {"status": 400, "body": {"error": "Invalid range"}}}),
    hist("history_session_expired", {}, {H: {"status": 440, "body": {}}}),
    hist("history_http_error_with_no_body", {}, {H: {"status": 500, "body": ""}}),
    hist("history_not_json", {}, {H: {"status": 200, "body": "<html>"}}),
    hist("history_throttled_then_read", {}, {H: [{"status": 429, "body": {}}, series()]}),
    hist("history_over_two_chunks", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {H: [series(t0=T0 - 86400 * 40), series()]}),
    hist("history_over_two_chunks_the_second_fails", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {H: [series(t0=T0 - 86400 * 40), {"status": 400, "body": {"error": "Bad"}}]}),
    hist("history_over_two_chunks_the_first_fails", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {H: [{"status": 400, "body": {"error": "Bad"}}, series()]}),
    hist("history_overlapping_times_keep_the_later_values", {"start_date": "2026-09-01", "end_date": "2026-11-02"}, {H: [series(), series(base=90000)]}),
    hist("history_a_range_in_the_future_is_no_candles", {"start_date": "2099-01-01", "end_date": "2099-01-05"}, {}),
    hist("history_an_exchange_nubra_does_not_serve", {"symbol": "USDINR", "exchange": "CDS"}, {H: series()}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {}),
]


def before_each(sc):
    """A recording has no use for the second between chunks."""
    import importlib

    importlib.import_module("broker.nubra.api.data").time.sleep = lambda *a, **k: None
