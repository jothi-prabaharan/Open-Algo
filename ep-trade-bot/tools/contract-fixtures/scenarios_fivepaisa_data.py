"""5paisa quotes, depth and several quotes: POST MarketSnapshot (the quote and the several), POST V2/MarketDepth (the best bid and ask, the ladder). A scrip with token 0 is asked for by its name."""

import scenarios_fivepaisa as s
from scenarios_fivepaisa import AUTH_TOKEN, DEPTH, ENV, READ_POSTS, SEED_API_KEY_ROW, SNAP, SVC, head, mdepth, req, snap  # noqa: F401

BROKER = "fivepaisa"
EXTRA = [
    s._sym("BANKNIFTY", "BANKNIFTY", "NSE_INDEX", 999920005, "BANKNIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", 999901, "SENSEX", 1, "INDEX"),
    s._sym("NIFTYBEES", "NIFTYBEES_EQ", "NSE", 10576, "NIFTY BEES"),
    s._sym("ZEROTOKEN", "ZEROTOKEN_EQ", "NSE", 0, "ZERO TOKEN"),
    s._sym("ZEROTWO", "ZEROTWO_EQ", "NSE", 0, "ZERO TWO"),
]
MANY = [s._sym(f"STK{i:02d}", f"STK{i:02d}_EQ", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 53)]
SYMBOLS = s.SYMBOLS + EXTRA + MANY
DENIED = {"head": head(False, "Invalid token"), "body": {}}


def q(name, symbol, exchange, broker, path="/api/v1/quotes"):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def row(code, ltp=805.45, **extra):
    return snap(ltp, ScripCode=code, **extra)["body"]["Data"][0]


def many(*rows):
    return {"head": head(), "body": {"Data": list(rows)}}


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


SCENARIOS = [
    q("quotes_equity", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth()}),
    q("quotes_best_prices_are_the_highest_bid_and_the_lowest_ask", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=((805.35, 100), (805.4, 50), (805.3, 10)), sells=((805.6, 10), (805.5, 20)))}),
    q("quotes_no_depth_orders", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=(), sells=())}),
    q("quotes_depth_fails_leaves_bid_and_ask_zero", "SBIN", "NSE", {SNAP: snap(), DEPTH: {"head": head(False, "No depth"), "body": {}}}),
    q("quotes_depth_http_error_leaves_bid_and_ask_zero", "SBIN", "NSE", {SNAP: snap(), DEPTH: {"status": 500, "body": {"x": 1}}}),
    q("quotes_previous_close_falls_back", "SBIN", "NSE", {SNAP: snap(PClose=0, PreviousClose=799.5), DEPTH: mdepth()}),
    q("quotes_previous_close_falls_back_to_close", "SBIN", "NSE", {SNAP: snap(PClose=0, Close=798.5), DEPTH: mdepth()}),
    q("quotes_missing_numbers_are_zero", "SBIN", "NSE", {SNAP: {"head": head(), "body": {"Data": [{"ScripCode": 3045, "LastTradedPrice": 805.45}]}}, DEPTH: mdepth()}),
    q("quotes_future", "NIFTY25NOV26FUT", "NFO", {SNAP: snap(24005.5, ScripCode=49500, OpenInterest=1234500), DEPTH: mdepth()}),
    q("quotes_commodity", "GOLDM25NOV26FUT", "MCX", {SNAP: snap(75000.0, ScripCode=80001), DEPTH: mdepth()}),
    q("quotes_currency", "USDINR25NOV26FUT", "CDS", {SNAP: snap(83.25, ScripCode=90001), DEPTH: mdepth()}),
    q("quotes_bse_derivative", "SENSEX26NOVFUT", "BFO", {SNAP: snap(80000.0, ScripCode=60001), DEPTH: mdepth()}),
    q("quotes_index_asked_as_the_index_exchange", "NIFTY", "NSE_INDEX", {SNAP: snap(24100.25, ScripCode=999920000), DEPTH: mdepth()}),
    q("quotes_bse_index", "SENSEX", "BSE_INDEX", {SNAP: snap(79050.5, ScripCode=999901), DEPTH: mdepth()}),
    q("quotes_a_share_whose_name_has_nifty_in_it_is_not_an_index", "NIFTYBEES", "NSE", {SNAP: snap(260.5, ScripCode=10576), DEPTH: mdepth()}),
    q("quotes_token_zero_is_asked_for_by_name", "ZEROTOKEN", "NSE", {SNAP: snap(10.5, ScripCode=0), DEPTH: mdepth()}),
    q("quotes_refused", "SBIN", "NSE", {SNAP: {"head": head(False, "Invalid scrip"), "body": {}}}),
    q("quotes_no_data", "SBIN", "NSE", {SNAP: {"head": head(), "body": {"Data": []}}}),
    q("quotes_http_error", "SBIN", "NSE", {SNAP: {"status": 500, "body": {"x": 1}}}),
    q("quotes_unknown_symbol", "NOPE", "NSE", {SNAP: snap(), DEPTH: mdepth()}),
    q("depth_equity", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=((805.4, 100), (805.35, 200), (805.3, 300), (805.25, 400), (805.2, 500)), sells=((805.5, 110), (805.55, 220), (805.6, 330), (805.65, 440), (805.7, 550)))}, "/api/v1/depth"),
    q("depth_unsorted_levels_are_sorted", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=((805.3, 100), (805.4, 200), (805.35, 300)), sells=((805.6, 10), (805.5, 20), (805.55, 30)))}, "/api/v1/depth"),
    q("depth_fewer_than_five_levels", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=((805.4, 100),), sells=())}, "/api/v1/depth"),
    q("depth_more_than_five_levels_totals_count_all", "SBIN", "NSE", {SNAP: snap(), DEPTH: mdepth(buys=tuple((805.4 - 0.05 * i, 100 + i) for i in range(8)), sells=tuple((805.5 + 0.05 * i, 110 + i) for i in range(8)))}, "/api/v1/depth"),
    q("depth_with_no_orders", "SBIN", "NSE", {SNAP: snap(), DEPTH: {"head": head(), "body": {}}}, "/api/v1/depth"),
    q("depth_future", "NIFTY25NOV26FUT", "NFO", {SNAP: snap(24005.5, ScripCode=49500, OpenInterest=900, LastTradedQty=75), DEPTH: mdepth()}, "/api/v1/depth"),
    q("depth_index", "NIFTY", "NSE_INDEX", {SNAP: snap(24100.25, ScripCode=999920000), DEPTH: mdepth()}, "/api/v1/depth"),
    q("depth_snapshot_refused", "SBIN", "NSE", {SNAP: {"head": head(False, "Invalid scrip"), "body": {}}}, "/api/v1/depth"),
    q("depth_snapshot_has_no_data", "SBIN", "NSE", {SNAP: {"head": head(), "body": {"Data": []}}}, "/api/v1/depth"),
    q("depth_depth_refused", "SBIN", "NSE", {SNAP: snap(), DEPTH: {"head": head(False, "No depth"), "body": {}}}, "/api/v1/depth"),
    q("depth_unknown_symbol", "NOPE", "NSE", {SNAP: snap(), DEPTH: mdepth()}, "/api/v1/depth"),
    req("multiquotes_equities", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {SNAP: many(row(3045), row(1594, 1500.5))}),
    req("multiquotes_different_exchanges", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("GOLDM25NOV26FUT", "MCX"), ("TATAMOTORS", "BSE")), {SNAP: many(row(3045), row(49500, 24005.5, OpenInterest=1000), row(80001, 75000.0), row(500570, 500.5))}),
    req("multiquotes_an_index", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SBIN", "NSE")), {SNAP: many(row(999920000, 24100.25), row(3045))}),
    req("multiquotes_a_quote_that_matches_nothing_is_left_out", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {SNAP: many(row(3045))}),
    req("multiquotes_an_extra_quote_is_ignored", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {SNAP: many(row(3045), row(7777))}),
    req("multiquotes_token_zero_matches_by_name", "/api/v1/multiquotes", legs(("ZEROTOKEN", "NSE"), ("ZEROTWO", "NSE")), {SNAP: many(row(0, 10.5, ScripData="ZEROTWO_EQ"), row(0, 11.5, ScripData="ZEROTOKEN_EQ"))}),
    req("multiquotes_matched_by_the_symbol_field", "/api/v1/multiquotes", legs(("ZEROTOKEN", "NSE"),), {SNAP: many(row(5, 10.5, Symbol="ZEROTOKEN_EQ"))}),
    req("multiquotes_previous_close_falls_back", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {SNAP: many(row(3045, PClose=0, PreviousClose=799.5))}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {SNAP: {"head": head(False, "Bad request"), "body": {}}}),
    req("multiquotes_http_error", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {SNAP: {"status": 500, "body": {"x": 1}}}),
    req("multiquotes_no_data", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {SNAP: {"head": head(), "body": {"Data": []}}}),
    req("multiquotes_unknown_symbol_among_known", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE")), {SNAP: many(row(3045))}),
    req("multiquotes_more_than_fifty_go_in_batches", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 53)]),
        {SNAP: [many(*[row(9000 + i, 100.0 + i) for i in range(1, 51)]), many(*[row(9000 + i, 100.0 + i) for i in range(51, 53)])]}),
]


def patch():
    import importlib

    data = importlib.import_module("broker.fivepaisa.api.data")
    data.time.sleep = lambda seconds: None
