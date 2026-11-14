"""HDFC Securities quotes, depth and several quotes: /fetch-ltp is the only REST market-data call (last price and previous close), and the feed snapshot that fills the rest is empty in a recording."""

from scenarios_hdfcsecurities import AUTH_TOKEN, B, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS, patch, req  # noqa: F401

BROKER = "hdfcsecurities"
LB = f"PUT {B}/fetch-ltp"


def row(token, exchange, ltp, prev, **extra):
    d = {"token": str(token), "exchange": exchange, "ltp": ltp, "prev_close": prev}
    d.update(extra)
    return d


def ok(*rows):
    return {"body": {"status": "success", "data": list(rows)}}


SBIN = ok(row(3045, "NSE", 805.45, 800.0))
FUT = ok(row(49500, "NFO", 24005.5, 23990.0))
INDEX = ok(row(26000, "NSE_INDEX", 24100.25, 24050.0))
EMPTY = ok()
REFUSED = {"status": 401, "body": {"status": "error", "message": "Invalid token"}}
LIMITED = {"status": 429, "body": {"status": "error", "message": "Too many requests"}}
SERVER_ERROR = {"status": 500, "body": {"status": "error", "message": "boom"}}
NO_DATA = {"body": {"status": "success", "data": None}}

ALL12 = [("SBIN", "NSE"), ("INFY", "NSE"), ("RELIANCE", "NSE"), ("NIFTY", "NSE_INDEX"), ("NIFTY25NOV26FUT", "NFO"), ("NIFTY25NOV2624000CE", "NFO"), ("NIFTY25NOV2624000PE", "NFO"),
         ("RELIANCE25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO"), ("SENSEX", "BSE_INDEX"), ("GOLDM25NOV26FUT", "MCX"), ("USDINR25NOV26FUT", "CDS")]


def legs(*pairs):
    return {"symbols": [{"symbol": s, "exchange": e} for s, e in pairs]}


FIRST10 = ok(row(3045, "NSE", 805.45, 800.0), row(1594, "NSE", 1499.5, 1490.0), row(2885, "NSE", 2901.25, 2890.0), row(26000, "NSE_INDEX", 24100.25, 24050.0),
             row(49500, "NFO", 24005.5, 23990.0), row(49599, "NFO", 100.5, 95.0), row(49600, "NFO", 90.25, 91.0), row(52000, "NFO", 2905.0, 2895.0),
             row(60001, "", 75000.5, 74900.0), row(26001, "BSE_INDEX", 79050.5, 78980.0))
LAST2 = ok(row(80001, "MCX", 75000.0, 74500.0), row(90001, "", 83.25, 83.0))

SCENARIOS = [
    req("quotes_equity", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: SBIN}),
    req("quotes_futures", "/api/v1/quotes", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {LB: FUT}),
    req("quotes_index", "/api/v1/quotes", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {LB: INDEX}),
    req("quotes_a_blank_exchange_is_recovered_for_bfo", "/api/v1/quotes", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {LB: ok(row(60001, "", 75000.5, 74900.0))}),
    req("quotes_a_blank_exchange_is_recovered_for_currency", "/api/v1/quotes", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, {LB: ok(row(90001, "", 83.25, 83.0))}),
    req("quotes_a_lowercase_exchange_is_read", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: ok(row(3045, "nse", 805.45, 800.0))}),
    req("quotes_numbers_as_text", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: ok(row(3045, "NSE", "805.45", "800"))}),
    req("quotes_null_numbers_are_zero", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: ok(row(3045, "NSE", None, None))}),
    req("quotes_instrument_missing_from_the_answer", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: EMPTY}),
    req("quotes_answer_for_another_instrument", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: ok(row(1594, "NSE", 1499.5, 1490.0))}),
    req("quotes_no_data_in_the_answer", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: NO_DATA}),
    req("quotes_refused", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: REFUSED}),
    req("quotes_server_error", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: SERVER_ERROR}),
    req("quotes_a_rate_limited_call_is_retried", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: [LIMITED, LIMITED, SBIN]}),
    req("quotes_rate_limited_every_time", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {LB: [LIMITED, LIMITED, LIMITED, LIMITED, SBIN]}),
    req("quotes_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}, {LB: SBIN}),
    req("depth_equity", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {LB: SBIN}),
    req("depth_futures", "/api/v1/depth", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {LB: FUT}),
    req("depth_instrument_missing_from_the_answer", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {LB: EMPTY}),
    req("depth_refused", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {LB: REFUSED}),
    req("depth_unknown_symbol", "/api/v1/depth", {"symbol": "NOPE", "exchange": "NSE"}, {LB: SBIN}),
    req("multiquotes", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE"), ("INFY", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO")),
        {LB: ok(row(3045, "NSE", 805.45, 800.0), row(1594, "NSE", 1499.5, 1490.0), row(49500, "NFO", 24005.5, 23990.0), row(60001, "", 75000.5, 74900.0))}),
    req("multiquotes_a_leg_missing_from_the_answer", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {LB: SBIN}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE")), {LB: REFUSED}),
    req("multiquotes_every_leg_unknown", "/api/v1/multiquotes", legs(("NOPE", "NSE"), ("NADA", "NFO")), {LB: SBIN}),
    req("multiquotes_the_same_instrument_twice", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("SBIN", "NSE")), {LB: SBIN}),
    req("multiquotes_twelve_go_in_batches_of_ten", "/api/v1/multiquotes", legs(*ALL12), {LB: [FIRST10, LAST2]}),
    req("multiquotes_a_rate_limited_batch_is_retried", "/api/v1/multiquotes", legs(*ALL12), {LB: [LIMITED, FIRST10, LIMITED, LAST2]}),
    req("multiquotes_one_batch_refused_the_other_answered", "/api/v1/multiquotes", legs(*ALL12), {LB: [REFUSED, LAST2]}),
    req("multiquotes_derivative_legs_have_zero_open_interest", "/api/v1/multiquotes", legs(("NIFTY25NOV26FUT", "NFO"), ("GOLDM25NOV26FUT", "MCX")), {LB: ok(row(49500, "NFO", 24005.5, 23990.0), row(80001, "MCX", 75000.0, 74500.0))}),
]
