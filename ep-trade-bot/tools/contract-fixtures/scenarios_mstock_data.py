"""mStock quotes and several quotes: GET /instruments/quote with a JSON body (mode OHLC and the tokens by exchange). The answer holds last price and OHLC only, so bid, ask, volume and open interest are zero."""

import scenarios_mstock as s
from scenarios_mstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V, req  # noqa: F401

BROKER = "mstock"
EXTRA = [s._sym("MCXENERGY", "MCXENERGY", "MCX_INDEX", 55001, "MCXENERGY", 1, "INDEX"), s._sym("NOMASTER", "NOMASTER", "NSE", 1234567, "NOMASTER")]
MANY = [s._sym(f"STK{i:03d}", f"STK{i:03d}-EQ", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 503)]
SYMBOLS = s.SYMBOLS + EXTRA + MANY
Q = f"GET {V}/instruments/quote"


def fetched(*rows, status=True):
    return {"body": {"status": status, "data": {"fetched": list(rows), "unfetched": []}}}


def row(token="3045", ltp=805.45, **extra):
    d = {"symbolToken": token, "ltp": ltp, "open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0}
    d.update(extra)
    return d


def one(name, symbol, exchange, reply):
    return req(name, "/api/v1/quotes", {"symbol": symbol, "exchange": exchange}, {Q: reply})


def many(name, pairs, reply):
    return req(name, "/api/v1/multiquotes", {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}, {Q: reply} if not isinstance(reply, dict) or "body" in reply or "status" in reply else reply)


SCENARIOS = [
    one("quote", "SBIN", "NSE", fetched(row())),
    one("quote_with_a_volume", "SBIN", "NSE", fetched(row(volume=1234567))),
    one("quote_with_a_volume_as_text", "SBIN", "NSE", fetched(row(volume="1500"))),
    one("quote_with_a_zero_volume", "SBIN", "NSE", fetched(row(volume=0))),
    one("quote_with_numbers_as_text", "SBIN", "NSE", fetched(row(ltp="805.5", open="801", high="809", low="799", close="800"))),
    one("quote_with_missing_prices_is_zero", "SBIN", "NSE", fetched({"symbolToken": "3045", "ltp": 805.45})),
    one("quote_of_a_derivative", "NIFTY25NOV26FUT", "NFO", fetched(row("49500", 24005.5))),
    one("quote_of_an_index", "NIFTY", "NSE_INDEX", fetched(row("26000", 24100.25))),
    one("quote_of_the_bse_index", "SENSEX", "BSE_INDEX", fetched(row("1", 80000))),
    one("quote_of_a_commodity", "GOLDM25NOV26FUT", "MCX", fetched(row("80001", 75010))),
    one("quote_of_a_commodity_index", "MCXENERGY", "MCX_INDEX", fetched(row("55001", 5000))),
    one("quote_takes_the_first_row_whatever_its_token", "SBIN", "NSE", fetched(row("9999", 1), row("3045", 2))),
    one("quote_with_no_rows", "SBIN", "NSE", fetched()),
    one("quote_status_false", "SBIN", "NSE", {"body": {"status": False, "message": "Invalid token"}}),
    one("quote_status_false_without_a_message", "SBIN", "NSE", {"body": {"status": False}}),
    one("quote_status_true_as_text_is_not_a_success", "SBIN", "NSE", {"body": {"status": "true", "data": {"fetched": [row()]}}}),
    one("quote_http_error", "SBIN", "NSE", {"status": 500, "body": {"status": False, "message": "down"}}),
    one("quote_not_json", "SBIN", "NSE", {"status": 200, "body": "<html>"}),
    one("quote_with_a_price_that_is_not_a_number", "SBIN", "NSE", fetched(row(ltp="n/a"))),
    one("quote_with_a_null_price", "SBIN", "NSE", fetched(row(ltp=None))),
    one("quote_without_data", "SBIN", "NSE", {"body": {"status": True}}),
    req("quote_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}),
    many("multiquotes", [("SBIN", "NSE"), ("INFY", "NSE"), ("TATAMOTORS", "BSE")], fetched(row(), row("1594", 1500), row("500570", 500))),
    many("multiquotes_one_missing", [("SBIN", "NSE"), ("INFY", "NSE")], fetched(row())),
    many("multiquotes_all_missing", [("SBIN", "NSE"), ("INFY", "NSE")], fetched()),
    many("multiquotes_an_unrequested_row_is_ignored", [("SBIN", "NSE")], fetched(row(), row("777", 1))),
    many("multiquotes_by_exchange", [("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO"), ("NIFTY", "NSE_INDEX")], fetched(row(), row("49500", 24000), row("60001", 80000), row("26000", 24100))),
    many("multiquotes_a_row_that_arrives_twice", [("SBIN", "NSE")], fetched(row(), row(ltp=806))),
    many("multiquotes_with_volume", [("SBIN", "NSE"), ("INFY", "NSE")], fetched(row(volume="100"), row("1594", 1500, volume=0))),
    many("multiquotes_status_false", [("SBIN", "NSE"), ("INFY", "NSE")], {"body": {"status": False, "message": "Session expired"}}),
    many("multiquotes_http_error", [("SBIN", "NSE")], {"status": 500, "body": {"x": 1}}),
    many("multiquotes_not_json", [("SBIN", "NSE")], {"status": 200, "body": "<html>"}),
    many("multiquotes_with_a_price_that_is_not_a_number", [("SBIN", "NSE"), ("INFY", "NSE")], fetched(row(ltp="n/a"), row("1594", 1500))),
    req("multiquotes_a_symbol_the_master_lacks", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}]}, {Q: fetched(row())}),
    req("multiquotes_five_hundred_and_two_go_in_two_batches", "/api/v1/multiquotes", {"symbols": [{"symbol": f"STK{i:03d}", "exchange": "NSE"} for i in range(1, 503)]},
        {Q: [fetched(*[row(str(9000 + i), 100 + i) for i in range(1, 501)]), fetched(row("9501", 600), row("9502", 601))]}),
]
