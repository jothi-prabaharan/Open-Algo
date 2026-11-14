"""Kotak Neo quotes, depth and several quotes: GET /script-details/1.0/quotes/neosymbol/{exchange|token,...}/all with the access token as the Authorization header. An index is asked for by name, trying the known spellings in turn. Twenty-five instruments go in a request. A 429 is tried again after half a second, a second and two."""

import scenarios_kotak as s
from scenarios_kotak import AUTH_TOKEN, ENV, ENCODE_PIPE, READ_POSTS, SEED_API_KEY_ROW, req  # noqa: F401

BROKER = "kotak"
Q = s.Q
EXTRA = [
    s._sym("NIFTY", "Nifty 50", "NSE_INDEX", "nse_cm", 26000, "NIFTY", 1, "INDEX"),
    s._sym("BANKNIFTY", "Nifty Bank", "NSE_INDEX", "nse_cm", 26009, "BANKNIFTY", 1, "INDEX"),
    s._sym("MIDCPNIFTY", "Nifty Mid Select", "NSE_INDEX", "nse_cm", 26074, "MIDCPNIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", "bse_cm", 1, "SENSEX", 1, "INDEX"),
    s._sym("OTHERIDX", "Other Idx", "NSE_INDEX", "nse_cm", 26099, "OTHERIDX", 1, "INDEX"),
]
MANY = [s._sym(f"STK{i:02d}", f"STK{i:02d}-EQ", "NSE", "nse_cm", 9000 + i, f"STOCK {i}") for i in range(1, 28)]
SYMBOLS = s.SYMBOLS + EXTRA + MANY


def u(query):
    """The request key for a query, as the path goes on the wire."""
    return f"{Q}/{query.replace('|', '%7C').replace(' ', '%20')}/all"


def quote(exchange, token, display, ltp="805.45", buy=5, sell=5, depth=True, **extra):
    d = {"exchange": exchange, "exchange_token": str(token), "display_symbol": display, "ltp": ltp, "ohlc": {"open": "801.00", "high": "809.50", "low": "799.00", "close": "800.00"},
         "last_volume": "1234567", "open_int": "12", "total_buy": "50000", "total_sell": "61000", "last_traded_quantity": "25"}
    if depth:
        d["depth"] = {"buy": [{"price": f"{805.4 - 0.05 * i:.2f}", "quantity": str(100 * (i + 1)), "orders": str(i + 1)} for i in range(buy)],
                      "sell": [{"price": f"{805.5 + 0.05 * i:.2f}", "quantity": str(110 * (i + 1)), "orders": str(i + 1)} for i in range(sell)]}
    d.update(extra)
    return d


def ok(*items):
    return {"body": list(items)}


SBIN = quote("nse_cm", 3045, "SBIN-EQ")
LIMITED = {"status": 429, "body": {"message": "too many request received"}}
NOT_OK = {"body": {"stat": "Not_Ok", "emsg": "Invalid neosymbol", "stCode": 1009}}


def one(name, path, symbol, exchange, broker):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/quotes", {"apikey": "wrong-key", "symbol": "SBIN", "exchange": "NSE"}),
    one("quotes_equity", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): ok(SBIN)}),
    one("quotes_future", "/api/v1/quotes", "NIFTY25NOV26FUT", "NFO", {u("nse_fo|49500"): ok(quote("nse_fo", 49500, "NIFTY25NOV26FUT", "24005.5"))}),
    one("quotes_mcx", "/api/v1/quotes", "GOLDM25NOV26FUT", "MCX", {u("mcx_fo|80001"): ok(quote("mcx_fo", 80001, "GOLDM25NOV26FUT", "75100"))}),
    one("quotes_currency", "/api/v1/quotes", "USDINR25NOV26FUT", "CDS", {u("cde_fo|90001"): ok(quote("cde_fo", 90001, "USDINR25NOV26FUT", "83.5"))}),
    one("quotes_bse", "/api/v1/quotes", "TATAMOTORS", "BSE", {u("bse_cm|500570"): ok(quote("bse_cm", 500570, "TATAMOTORS-EQ", "500.5"))}),
    one("quotes_without_depth_uses_the_last_price", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", depth=False))}),
    one("quotes_with_null_depth", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", depth=None))}),
    one("quotes_index", "/api/v1/quotes", "NIFTY", "NSE_INDEX", {u("nse_cm|Nifty 50"): ok(quote("nse_cm", 26000, "Nifty 50", "24100.25"))}),
    one("quotes_bank_index", "/api/v1/quotes", "BANKNIFTY", "NSE_INDEX", {u("nse_cm|Nifty Bank"): ok(quote("nse_cm", 26009, "Nifty Bank", "51000.5"))}),
    one("quotes_bse_index", "/api/v1/quotes", "SENSEX", "BSE_INDEX", {u("bse_cm|SENSEX"): ok(quote("bse_cm", 1, "SENSEX", "80000.5"))}),
    one("quotes_index_tries_the_next_spelling", "/api/v1/quotes", "MIDCPNIFTY", "NSE_INDEX", {u("nse_cm|Nifty Mid Select"): ok(), u("nse_cm|Nifty Midcap Sel"): ok(quote("nse_cm", 26074, "Nifty Midcap Sel", "12000.5"))}),
    one("quotes_index_with_no_spelling_known", "/api/v1/quotes", "OTHERIDX", "NSE_INDEX", {u("nse_cm|OTHERIDX"): ok(quote("nse_cm", 26099, "OTHERIDX", "100.5"))}),
    one("quotes_index_every_spelling_fails", "/api/v1/quotes", "MIDCPNIFTY", "NSE_INDEX", {u("nse_cm|Nifty Mid Select"): NOT_OK, u("nse_cm|Nifty Midcap Sel"): NOT_OK, u("nse_cm|Nifty Midcap Select"): NOT_OK, u("nse_cm|NIFTY MID SELECT"): NOT_OK}),
    one("quotes_neo_error", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): NOT_OK}),
    one("quotes_empty_answer", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): ok()}),
    one("quotes_http_error", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): {"status": 500, "body": "oops"}}),
    one("quotes_rate_limited_is_tried_again", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): [LIMITED, ok(SBIN)]}),
    one("quotes_rate_limited_three_times_then_served", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): [LIMITED, LIMITED, LIMITED, ok(SBIN)]}),
    one("quotes_rate_limited_every_time", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): LIMITED}),
    one("quotes_text_numbers", "/api/v1/quotes", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", last_volume="16131960.0000", open_int="7"))}),
    one("quotes_unknown_symbol", "/api/v1/quotes", "NOPE", "NSE", {}),
    one("depth_equity", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): ok(SBIN)}),
    one("depth_fewer_levels_are_padded", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", buy=2, sell=1))}),
    one("depth_more_levels_are_cut", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", buy=7, sell=7))}),
    one("depth_totals_fall_back_to_the_levels", "/api/v1/depth", "NIFTY25NOV26FUT", "NFO", {u("nse_fo|49500"): ok(quote("nse_fo", 49500, "NIFTY25NOV26FUT", total_buy="0", total_sell="0"))}),
    one("depth_totals_missing", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): ok({**quote("nse_cm", 3045, "SBIN-EQ"), "total_buy": None, "total_sell": None})}),
    one("depth_without_a_book", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", depth=False))}),
    one("depth_index", "/api/v1/depth", "NIFTY", "NSE_INDEX", {u("nse_cm|Nifty 50"): ok(quote("nse_cm", 26000, "Nifty 50", "24100.25"))}),
    one("depth_neo_error", "/api/v1/depth", "SBIN", "NSE", {u("nse_cm|3045"): NOT_OK}),
    one("depth_index_every_spelling_fails", "/api/v1/depth", "MIDCPNIFTY", "NSE_INDEX", {u("nse_cm|Nifty Mid Select"): NOT_OK, u("nse_cm|Nifty Midcap Sel"): NOT_OK, u("nse_cm|Nifty Midcap Select"): NOT_OK, u("nse_cm|NIFTY MID SELECT"): NOT_OK}),
    one("depth_unknown_symbol", "/api/v1/depth", "NOPE", "NSE", {}),
    req("multiquotes_equities", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {u("nse_cm|3045,nse_cm|1594"): ok(SBIN, quote("nse_cm", 1594, "INFY-EQ", "1500.5"))}),
    req("multiquotes_different_exchanges", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("GOLDM25NOV26FUT", "MCX"), ("TATAMOTORS", "BSE")),
        {u("nse_cm|3045,nse_fo|49500,mcx_fo|80001,bse_cm|500570"): ok(SBIN, quote("nse_fo", 49500, "NIFTY25NOV26FUT", "24005.5"), quote("mcx_fo", 80001, "GOLDM25NOV26FUT", "75100"), quote("bse_cm", 500570, "TATAMOTORS-EQ", "500.5"))}),
    req("multiquotes_indices_use_the_first_spelling", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")),
        {u("nse_cm|Nifty 50,bse_cm|SENSEX"): ok(quote("nse_cm", 26000, "Nifty 50", "24100.25"), quote("bse_cm", 1, "SENSEX", "80000.5"))}),
    req("multiquotes_matched_by_the_display_name", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {u("nse_cm|3045"): ok(quote("nse_cm", "other", "SBIN-EQ"))}),
    req("multiquotes_a_missing_row_is_an_error_entry", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {u("nse_cm|3045,nse_cm|1594"): ok(SBIN)}),
    req("multiquotes_an_unknown_symbol_is_an_error_entry", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE")), {u("nse_cm|3045"): ok(SBIN)}),
    req("multiquotes_only_unknown_symbols", "/api/v1/multiquotes", legs(("NOPE", "NSE"),), {}),
    req("multiquotes_text_numbers", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {u("nse_cm|3045"): ok(quote("nse_cm", 3045, "SBIN-EQ", last_volume="1234567.5", open_int="12"))}),
    req("multiquotes_neo_error", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {u("nse_cm|3045"): NOT_OK}),
    req("multiquotes_empty_answer", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {u("nse_cm|3045"): ok()}),
    req("multiquotes_rate_limited_is_tried_again", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {u("nse_cm|3045"): [LIMITED, ok(SBIN)]}),
    req("multiquotes_more_than_twenty_five_go_in_batches", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 28)]),
        {u(",".join(f"nse_cm|{9000 + i}" for i in range(1, 26))): ok(*[quote("nse_cm", 9000 + i, f"STK{i:02d}-EQ", str(100 + i)) for i in range(1, 26)]),
         u("nse_cm|9026,nse_cm|9027"): ok(*[quote("nse_cm", 9000 + i, f"STK{i:02d}-EQ", str(100 + i)) for i in (26, 27)])}),
    req("multiquotes_a_failed_batch_leaves_error_entries", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 28)]),
        {u(",".join(f"nse_cm|{9000 + i}" for i in range(1, 26))): {"status": 500, "body": "oops"}, u("nse_cm|9026,nse_cm|9027"): ok(*[quote("nse_cm", 9000 + i, f"STK{i:02d}-EQ", str(100 + i)) for i in (26, 27)])}),
]


def patch():
    pass
