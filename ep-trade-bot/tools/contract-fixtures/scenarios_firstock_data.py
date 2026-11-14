"""Firstock quotes, depth and several quotes: POST /getQuote (a quote and the depth are the same call) and POST /getMultiQuotes (fifty a call). An index is asked for under the cash exchange. A plain-text rate-limit answer is tried again once."""

import scenarios_firstock as s
from scenarios_firstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V, quote, req  # noqa: F401

BROKER = "firstock"
QQ, MQ = f"POST {V}/getQuote", f"POST {V}/getMultiQuotes"
EXTRA = [s._sym("BANKNIFTY", "Nifty Bank", "NSE_INDEX", 26009, "BANKNIFTY", 1, "INDEX"), s._sym("SENSEX", "SENSEX", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX")]
MANY = [s._sym(f"STK{i:02d}", f"STK{i:02d}-EQ", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 53)]
SYMBOLS = s.SYMBOLS + EXTRA + MANY
LIMITED = {"status": 429, "body": "Rate limit exceeded, retry later"}


def q(name, symbol, exchange, broker, path="/api/v1/quotes"):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def row(exchange, symbol, ltp="805.45", **extra):
    d = quote(ltp, **extra)["body"]["data"]
    d.update({"exchange": exchange, "tradingSymbol": symbol})
    return d


def multi(*rows):
    return {"body": {"status": "success", "data": list(rows)}}


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


SCENARIOS = [
    q("quotes_equity", "SBIN", "NSE", {QQ: quote()}),
    q("quotes_text_numbers", "SBIN", "NSE", {QQ: quote("805.45", volume="1234567.0", openInterest="12.0")}),
    q("quotes_missing_numbers_are_zero", "SBIN", "NSE", {QQ: {"body": {"status": "success", "data": {"lastTradedPrice": "805.45"}}}}),
    q("quotes_empty_data", "SBIN", "NSE", {QQ: {"body": {"status": "success", "data": {}}}}),
    q("quotes_future_with_open_interest", "NIFTY25NOV26FUT", "NFO", {QQ: quote("24005.5", openInterest="1234500")}),
    q("quotes_commodity", "GOLDM25NOV26FUT", "MCX", {QQ: quote("75000.0")}),
    q("quotes_currency", "USDINR25NOV26FUT", "CDS", {QQ: quote("83.25")}),
    q("quotes_bse_equity", "TATAMOTORS", "BSE", {QQ: quote("500.5")}),
    q("quotes_nse_index_is_asked_as_the_cash_exchange", "NIFTY", "NSE_INDEX", {QQ: quote("24100.25")}),
    q("quotes_bse_index_is_asked_as_the_cash_exchange", "SENSEX", "BSE_INDEX", {QQ: quote("79050.5")}),
    q("quotes_refused", "SBIN", "NSE", {QQ: {"body": {"status": "failed", "error": {"message": "Invalid symbol"}}}}),
    q("quotes_refused_without_a_message", "SBIN", "NSE", {QQ: {"body": {"status": "failed"}}}),
    q("quotes_rate_limited_is_tried_again", "SBIN", "NSE", {QQ: [LIMITED, quote()]}),
    q("quotes_rate_limited_every_time", "SBIN", "NSE", {QQ: [LIMITED, LIMITED]}),
    q("quotes_empty_answer", "SBIN", "NSE", {QQ: {"status": 200, "body": ""}}),
    q("quotes_unknown_symbol", "NOPE", "NSE", {QQ: quote()}),
    q("depth_equity", "SBIN", "NSE", {QQ: quote()}, "/api/v1/depth"),
    q("depth_index", "NIFTY", "NSE_INDEX", {QQ: quote("24100.25")}, "/api/v1/depth"),
    q("depth_text_levels", "SBIN", "NSE", {QQ: quote(bestBuyPrice3="805.30", bestBuyQuantity3="300.0", totalBuyQuantity="5000")}, "/api/v1/depth"),
    q("depth_missing_levels_are_zero", "SBIN", "NSE", {QQ: {"body": {"status": "success", "data": {"lastTradedPrice": "805.45", "bestBuyPrice1": "805.4"}}}}, "/api/v1/depth"),
    q("depth_refused", "SBIN", "NSE", {QQ: {"body": {"status": "failed", "error": {"message": "Invalid symbol"}}}}, "/api/v1/depth"),
    q("depth_unknown_symbol", "NOPE", "NSE", {QQ: quote()}, "/api/v1/depth"),
    req("multiquotes_equities", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {MQ: multi(row("NSE", "SBIN-EQ"), row("NSE", "INFY-EQ", "1500.5"))}),
    req("multiquotes_different_exchanges", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("GOLDM25NOV26FUT", "MCX"), ("TATAMOTORS", "BSE")),
        {MQ: multi(row("NSE", "SBIN-EQ"), row("NFO", "NIFTY25NOV26F", "24005.5", openInterest="1000"), row("MCX", "GOLDM25NOV26FUT", "75000.0"), row("BSE", "TATAMOTORS-EQ", "500.5"))}),
    req("multiquotes_indices_are_asked_as_the_cash_exchange", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")), {MQ: multi(row("NSE", "Nifty 50", "24100.25"), row("BSE", "SENSEX", "79050.5"))}),
    req("multiquotes_a_row_that_matches_no_request_keeps_its_own_names", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: multi(row("NSE", "OTHER-EQ"))}),
    req("multiquotes_a_missing_row_is_left_out", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {MQ: multi(row("NSE", "SBIN-EQ"))}),
    req("multiquotes_text_numbers", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: multi(row("NSE", "SBIN-EQ", volume="1234567.0", openInterest="12"))}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: {"body": {"status": "failed", "error": {"message": "Bad request"}}}}),
    req("multiquotes_refused_with_a_top_level_message", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: {"body": {"status": "failed", "message": "Bad request"}}}),
    req("multiquotes_rate_limited_is_tried_again", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: [LIMITED, multi(row("NSE", "SBIN-EQ"))]}),
    req("multiquotes_unknown_symbol_among_known", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE")), {MQ: multi(row("NSE", "SBIN-EQ"))}),
    req("multiquotes_more_than_fifty_go_in_batches", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 53)]),
        {MQ: [multi(*[row("NSE", f"STK{i:02d}-EQ", f"{100 + i}.0") for i in range(1, 51)]), multi(*[row("NSE", f"STK{i:02d}-EQ", f"{100 + i}.0") for i in range(51, 53)])]}),
]


def patch():
    import importlib

    data = importlib.import_module("broker.firstock.api.data")
    data.time.sleep = lambda seconds: None
