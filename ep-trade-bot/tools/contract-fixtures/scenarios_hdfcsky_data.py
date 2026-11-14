"""HDFC Sky quotes, depth and several quotes: /fetch-ltp gives the last price and previous close, the day's candle (chart data) gives open, high, low and volume, and the feed snapshot that would fill the book is empty in a recording."""

from datetime import datetime as _datetime

from scenarios_hdfcsky import AUTH_TOKEN, B, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS, req  # noqa: F401

BROKER = "hdfcsky"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
LB = f"PUT {B}/fetch-ltp"
CB = "GET /oapi/charts-api/charts/v1/fetch-candle"


def row(token, exchange, ltp, prev):
    return {"token": str(token), "exchange": exchange, "ltp": ltp, "prev_close": prev}


def ok(*rows):
    return {"body": {"status": "success", "data": list(rows)}}


def candles(*rows, meta=None):
    return {"body": {"meta": meta or {"err_code": "success"}, "data": {"results": [list(r) for r in rows]}}}


# [open, high, low, close, volume, oi, "DD-MM-YYYY", cumulative volume]
TODAY = [801.0, 809.5, 799.0, 805.45, 1234567, 0, "13-11-2026", 1234567]
YESTERDAY = [790.0, 805.0, 788.0, 800.0, 999999, 0, "12-11-2026", 0]
SBIN = ok(row(3045, "NSE", 805.45, 800.0))
FUT = ok(row(49500, "NFO", 24005.5, 23990.0))
INDEX = ok(row(26000, "NSE_INDEX", 24100.25, 24050.0))
EMPTY = ok()
REFUSED = {"status": 401, "body": {"status": "error", "message": "Invalid token"}}
LIMITED = {"status": 429, "body": {"status": "error", "message": "Too many requests"}}
SERVER_ERROR = {"status": 500, "body": {"status": "error", "message": "boom"}}
CHART_REFUSED = {"status": 400, "body": {"meta": {"displayMessage": "Allowed Date range exceeded"}}}
CHART_ERR = candles(meta={"err_code": "invalid", "displayMessage": "No data for the symbol"})

ALL12 = [("SBIN", "NSE"), ("INFY", "NSE"), ("RELIANCE", "NSE"), ("NIFTY", "NSE_INDEX"), ("NIFTY25NOV26FUT", "NFO"), ("NIFTY25NOV2624000CE", "NFO"), ("NIFTY25NOV2624000PE", "NFO"),
         ("RELIANCE25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO"), ("SENSEX", "BSE_INDEX"), ("GOLDM25NOV26FUT", "MCX"), ("USDINR25NOV26FUT", "CDS")]


def legs(*pairs):
    return {"symbols": [{"symbol": s, "exchange": e} for s, e in pairs]}


FIRST10 = ok(row(3045, "NSE", 805.45, 800.0), row(1594, "NSE", 1499.5, 1490.0), row(2885, "NSE", 2901.25, 2890.0), row(26000, "NSE_INDEX", 24100.25, 24050.0),
             row(49500, "NFO", 24005.5, 23990.0), row(49599, "NFO", 100.5, 95.0), row(49600, "NFO", 90.25, 91.0), row(52000, "NFO", 2905.0, 2895.0),
             row(60001, "BFO", 75000.5, 74900.0), row(26001, "BSE_INDEX", 79050.5, 78980.0))
LAST2 = ok(row(80001, "MCX", 75000.0, 74500.0), row(90001, "CDS", 83.25, 83.0))


def q(name, symbol, exchange, broker, path="/api/v1/quotes"):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


SCENARIOS = [
    q("quotes_equity", "SBIN", "NSE", {LB: SBIN, CB: candles(YESTERDAY, TODAY)}),
    q("quotes_the_latest_candle_is_chosen_by_its_date_text", "SBIN", "NSE", {LB: SBIN, CB: candles([1, 2, 0.5, 1.5, 10, 0, "30-10-2026", 0], [3, 4, 2.5, 3.5, 20, 0, "02-11-2026", 0])}),
    q("quotes_futures", "NIFTY25NOV26FUT", "NFO", {LB: FUT, CB: candles(TODAY)}),
    q("quotes_option", "NIFTY25NOV2624000CE", "NFO", {LB: ok(row(49599, "NFO", 7.35, 7.0)), CB: candles(TODAY)}),
    q("quotes_index", "NIFTY", "NSE_INDEX", {LB: INDEX, CB: candles(TODAY)}),
    q("quotes_bse_index", "SENSEX", "BSE_INDEX", {LB: ok(row(26001, "BSE_INDEX", 79050.5, 78980.0)), CB: candles(TODAY)}),
    q("quotes_bse_derivative", "SENSEX26NOVFUT", "BFO", {LB: ok(row(60001, "BFO", 75000.5, 74900.0)), CB: candles(TODAY)}),
    q("quotes_commodity", "GOLDM25NOV26FUT", "MCX", {LB: ok(row(80001, "MCX", 75000.0, 74500.0)), CB: candles(TODAY)}),
    q("quotes_currency", "USDINR25NOV26FUT", "CDS", {LB: ok(row(90001, "CDS", 83.25, 83.0)), CB: candles(TODAY)}),
    q("quotes_bse_equity", "TATAMOTORS", "BSE", {LB: ok(row(500570, "BSE", 500.5, 499.0)), CB: candles(TODAY)}),
    q("quotes_numbers_as_text", "SBIN", "NSE", {LB: ok(row(3045, "NSE", "805.45", "800")), CB: candles(["801", "809.5", "799", "805.45", "1234567", "0", "13-11-2026", "0"])}),
    q("quotes_null_numbers_are_zero", "SBIN", "NSE", {LB: ok(row(3045, "NSE", None, None)), CB: candles([None, None, None, None, None, None, "13-11-2026", None])}),
    q("quotes_a_lowercase_exchange_is_read", "SBIN", "NSE", {LB: ok(row(3045, "nse", 805.45, 800.0)), CB: candles(TODAY)}),
    q("quotes_instrument_missing_from_the_answer", "SBIN", "NSE", {LB: EMPTY, CB: candles(TODAY)}),
    q("quotes_refused", "SBIN", "NSE", {LB: REFUSED, CB: candles(TODAY)}),
    q("quotes_server_error", "SBIN", "NSE", {LB: SERVER_ERROR, CB: candles(TODAY)}),
    q("quotes_a_rate_limited_call_is_retried", "SBIN", "NSE", {LB: [LIMITED, LIMITED, SBIN], CB: candles(TODAY)}),
    q("quotes_succeeds_on_the_fourth_try", "SBIN", "NSE", {LB: [LIMITED, LIMITED, LIMITED, SBIN], CB: candles(TODAY)}),
    q("quotes_rate_limited_every_time", "SBIN", "NSE", {LB: [LIMITED, LIMITED, LIMITED, LIMITED, SBIN], CB: candles(TODAY)}),
    q("quotes_no_candles_leave_the_day_figures_at_zero", "SBIN", "NSE", {LB: SBIN, CB: candles()}),
    q("quotes_candle_refused", "SBIN", "NSE", {LB: SBIN, CB: CHART_REFUSED}),
    q("quotes_candle_error_code", "SBIN", "NSE", {LB: SBIN, CB: CHART_ERR}),
    q("quotes_candle_rate_limited_then_answered", "SBIN", "NSE", {LB: SBIN, CB: [LIMITED, candles(TODAY)]}),
    q("quotes_candle_succeeds_on_the_fourth_try", "SBIN", "NSE", {LB: SBIN, CB: [LIMITED, LIMITED, LIMITED, candles(TODAY)]}),
    q("quotes_candle_error_code_ok_is_a_success", "SBIN", "NSE", {LB: SBIN, CB: candles(TODAY, meta={"err_code": "OK"})}),
    q("quotes_candle_error_code_zero_is_a_success", "SBIN", "NSE", {LB: SBIN, CB: candles(TODAY, meta={"err_code": "0"})}),
    q("quotes_candle_rate_limited_every_time", "SBIN", "NSE", {LB: SBIN, CB: [LIMITED, LIMITED, LIMITED, LIMITED, candles(TODAY)]}),
    q("quotes_candle_row_too_short", "SBIN", "NSE", {LB: SBIN, CB: candles([1, 2, 3])}),
    q("quotes_unknown_symbol", "NOPE", "NSE", {LB: SBIN, CB: candles(TODAY)}),
    q("depth_equity", "SBIN", "NSE", {LB: SBIN, CB: candles(TODAY)}, "/api/v1/depth"),
    q("depth_futures", "NIFTY25NOV26FUT", "NFO", {LB: FUT, CB: candles(TODAY)}, "/api/v1/depth"),
    q("depth_instrument_missing_from_the_answer", "SBIN", "NSE", {LB: EMPTY, CB: candles(TODAY)}, "/api/v1/depth"),
    q("depth_refused", "SBIN", "NSE", {LB: REFUSED, CB: candles(TODAY)}, "/api/v1/depth"),
    q("depth_unknown_symbol", "NOPE", "NSE", {LB: SBIN, CB: candles(TODAY)}, "/api/v1/depth"),
    req("multiquotes", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE"), ("INFY", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO")),
        {LB: ok(row(3045, "NSE", 805.45, 800.0), row(1594, "NSE", 1499.5, 1490.0), row(49500, "NFO", 24005.5, 23990.0), row(60001, "BFO", 75000.5, 74900.0))}),
    req("multiquotes_a_leg_missing_from_the_answer", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {LB: SBIN}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE")), {LB: REFUSED}),
    req("multiquotes_every_leg_unknown", "/api/v1/multiquotes", legs(("NOPE", "NSE"), ("NADA", "NFO")), {LB: SBIN}),
    req("multiquotes_the_same_instrument_twice", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("SBIN", "NSE")), {LB: SBIN}),
    req("multiquotes_indexes_use_their_own_codes", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")), {LB: ok(row(26000, "NSE_INDEX", 24100.25, 24050.0), row(26001, "BSE_INDEX", 79050.5, 78980.0))}),
    req("multiquotes_twelve_go_in_batches_of_ten", "/api/v1/multiquotes", legs(*ALL12), {LB: [FIRST10, LAST2]}),
    req("multiquotes_a_rate_limited_batch_is_retried", "/api/v1/multiquotes", legs(*ALL12), {LB: [LIMITED, FIRST10, LIMITED, LAST2]}),
    req("multiquotes_one_batch_refused_the_other_answered", "/api/v1/multiquotes", legs(*ALL12), {LB: [REFUSED, LAST2]}),
]


def patch():
    import importlib

    data = importlib.import_module("broker.hdfcsky.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed.replace(tzinfo=None) if tz is None else fixed.astimezone(tz)

    data.datetime = _Frozen
    data.BrokerData._collect_feed_snapshot = lambda self, instruments, is_complete: {}
