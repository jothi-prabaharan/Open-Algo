"""Samco quotes, depth and several quotes: GET /quote/getQuote, GET /quote/indexQuote, POST /marketDepth and POST /quote/multiQuote (25 a call). MCX derivatives are asked for under MFO. A rate-limited or failing read is tried again three times."""

import scenarios_samco as s
from scenarios_samco import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, req  # noqa: F401

BROKER = "samco"
QQ, IQ, DP, MQ = "GET /quote/getQuote", "GET /quote/indexQuote", "POST /marketDepth", "POST /quote/multiQuote"
SEGMENT = {"NSE": "NSE", "BSE": "BSE", "NFO": "NFO", "BFO": "BFO", "MCX": "MFO", "CDS": "CDS", "NSE_INDEX": "NSE", "BSE_INDEX": "BSE"}


def _tokened(rows):
    out = []
    for r in rows:
        r = dict(r)
        r["token"] = f"{r['token']}_{r['brexchange']}"
        out.append(r)
    return out


EXTRA = [
    s._sym("BANKNIFTY", "NIFTY BANK", "NSE_INDEX", 26009, "BANKNIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX"),
    s._sym("INDIAVIX", "INDIA VIX", "NSE_INDEX", 26017, "INDIAVIX", 1, "INDEX"),
]
MANY = [s._sym(f"STK{i:02d}", f"STK{i:02d}_EQ", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 27)]
SYMBOLS = _tokened(s.SYMBOLS + EXTRA + MANY)
RATE = {"status": 429, "body": {"status": "Failure", "statusMessage": "Too many requests"}}
DOWN = {"status": 500, "body": {"status": "Failure", "statusMessage": "Internal error", "msgId": "m1", "serverTime": "t"}}
DENIED = {"status": 403, "body": {"status": "Failure", "statusMessage": "Forbidden"}}


def quote(ltp=805.45, **extra):
    return s.quote(ltp, **extra)


def index_quote(spot=24100.25, **extra):
    d = {"indexName": "NIFTY 50", "spotPrice": str(spot), "openValue": "24050.00", "highValue": "24150.50", "lowValue": "24010.00", "closeValue": "24000.00", "totalTradedVolume": "1,000,000", "listingId": "-23"}
    d.update(extra)
    return {"body": {"status": "Success", "indexDetails": [d]}}


def q(name, symbol, exchange, broker, path="/api/v1/quotes"):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def mq(token, symbol, exchange="NSE", ltp=805.45, **extra):
    d = {"symbol": token, "exchange": exchange, "tradingSymbol": symbol, "symbolName": symbol, "bidPrice": "805.40", "askPrice": "805.50", "bidSize": "100", "askSize": "110", "open": "801.00", "high": "809.50", "low": "799.00",
         "lastTradePrice": str(ltp), "previousClose": "800.00", "totalTradeVolume": "1,234,567", "openInterest": "0"}
    d.update(extra)
    return d


def multi(*rows):
    return {"body": {"status": "Success", "multiQuotes": list(rows)}}


def levels(prefix, n, base, step):
    return [{f"{prefix}Price": f"{base + step * i:.2f}", f"{prefix}Size": str(100 * (i + 1))} for i in range(n)]


def depth(buy=5, sell=5, **extra):
    d = {"bestFiveBid": levels("bid", buy, 805.4, -0.05), "bestFiveAsk": levels("ask", sell, 805.5, 0.05), "tBuyQty": "5,000", "tSellQty": "6100"}
    d.update(extra)
    return {"body": {"status": "Success", "MarketDepthDetails": {"marketDepth": d}}}


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


def many(*pairs, name):
    return req(name, "/api/v1/multiquotes", legs(*pairs), {})


SCENARIOS = [
    q("quotes_equity", "SBIN", "NSE", {QQ: quote()}),
    q("quotes_no_best_prices", "SBIN", "NSE", {QQ: quote(bestBids=[], bestAsks=[])}),
    q("quotes_text_numbers_with_commas", "SBIN", "NSE", {QQ: quote("1,805.45", openValue="1,801.00", totalTradedVolume="12,34,567")}),
    q("quotes_missing_numbers_are_zero", "SBIN", "NSE", {QQ: {"body": {"status": "Success", "quoteDetails": {"lastTradedPrice": "805.45"}}}}),
    q("quotes_future_with_open_interest", "NIFTY25NOV26FUT", "NFO", {QQ: quote(24005.5, openInterest="1,234,500")}),
    q("quotes_commodity_asks_for_mfo", "GOLDM25NOV26FUT", "MCX", {QQ: quote(75000.0, openInterest="500")}),
    q("quotes_currency", "USDINR25NOV26FUT", "CDS", {QQ: quote(83.25)}),
    q("quotes_bse_equity", "TATAMOTORS", "BSE", {QQ: quote(500.5)}),
    q("quotes_bse_derivative", "SENSEX26NOVFUT", "BFO", {QQ: quote(80000.0)}),
    q("quotes_index", "NIFTY", "NSE_INDEX", {IQ: index_quote()}),
    q("quotes_bse_index", "SENSEX", "BSE_INDEX", {IQ: index_quote(79050.5, indexName="SENSEX")}),
    q("quotes_index_name_with_spaces_is_encoded", "BANKNIFTY", "NSE_INDEX", {IQ: index_quote(52000.0, indexName="NIFTY BANK")}),
    q("quotes_index_without_details", "NIFTY", "NSE_INDEX", {IQ: {"body": {"status": "Success", "indexDetails": []}}}),
    q("quotes_index_refused", "NIFTY", "NSE_INDEX", {IQ: {"body": {"status": "Failure", "statusMessage": "Index not found"}}}),
    q("quotes_refused", "SBIN", "NSE", {QQ: {"body": {"status": "Failure", "statusMessage": "Symbol not found"}}}),
    q("quotes_refused_without_a_message", "SBIN", "NSE", {QQ: {"body": {"status": "Failure"}}}),
    q("quotes_without_details", "SBIN", "NSE", {QQ: {"body": {"status": "Success", "quoteDetails": {}}}}),
    q("quotes_session_refused", "SBIN", "NSE", {QQ: DENIED}),
    q("quotes_rate_limited_is_tried_again", "SBIN", "NSE", {QQ: [RATE, RATE, quote()]}),
    q("quotes_rate_limited_every_time", "SBIN", "NSE", {QQ: [RATE, RATE, RATE, RATE, quote()]}),
    q("quotes_server_error_is_tried_again", "SBIN", "NSE", {QQ: [DOWN, quote()]}),
    q("quotes_server_error_every_time", "SBIN", "NSE", {QQ: [DOWN, DOWN, DOWN, DOWN, quote()]}),
    q("quotes_unknown_symbol", "NOPE", "NSE", {QQ: quote()}),
    q("depth_equity", "SBIN", "NSE", {DP: depth(), QQ: quote()}, "/api/v1/depth"),
    q("depth_fewer_than_five_levels", "SBIN", "NSE", {DP: depth(3, 2), QQ: quote()}, "/api/v1/depth"),
    q("depth_more_than_five_levels", "SBIN", "NSE", {DP: depth(7, 7), QQ: quote()}, "/api/v1/depth"),
    q("depth_future", "NIFTY25NOV26FUT", "NFO", {DP: depth(), QQ: quote(24005.5, openInterest="900")}, "/api/v1/depth"),
    q("depth_commodity_asks_for_mfo", "GOLDM25NOV26FUT", "MCX", {DP: depth(), QQ: quote(75000.0)}, "/api/v1/depth"),
    q("depth_bse_equity", "TATAMOTORS", "BSE", {DP: depth(), QQ: quote(500.5)}, "/api/v1/depth"),
    q("depth_when_the_quote_fails_totals_are_zero", "SBIN", "NSE", {DP: depth(), QQ: {"body": {"status": "Failure", "statusMessage": "no"}}}, "/api/v1/depth"),
    q("depth_index_has_none", "NIFTY", "NSE_INDEX", {IQ: index_quote()}, "/api/v1/depth"),
    q("depth_refused", "SBIN", "NSE", {DP: {"body": {"status": "Failure", "statusMessage": "No depth"}}}, "/api/v1/depth"),
    q("depth_without_details", "SBIN", "NSE", {DP: {"body": {"status": "Success", "MarketDepthDetails": {}}}}, "/api/v1/depth"),
    q("depth_rate_limited_is_tried_again", "SBIN", "NSE", {DP: [RATE, depth()], QQ: quote()}, "/api/v1/depth"),
    q("depth_unknown_symbol", "NOPE", "NSE", {DP: depth(), QQ: quote()}, "/api/v1/depth"),
    req("multiquotes_equities", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {MQ: multi(mq("3045_NSE", "SBIN_EQ"), mq("1594_NSE", "INFY_EQ", ltp=1500.5))}),
    req("multiquotes_different_exchanges_in_one_call", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO"), ("GOLDM25NOV26FUT", "MCX"), ("TATAMOTORS", "BSE")),
        {MQ: multi(mq("3045_NSE", "SBIN_EQ"), mq("49500_NFO", "NIFTY25NOV26FUT", "NFO", 24005.5, openInterest="1000"), mq("80001_MFO", "GOLDM25NOV26FUT", "MCX", 75000.0), mq("500570_BSE", "TATAMOTORS_EQ", "BSE", 500.5))}),
    req("multiquotes_joined_by_name_when_the_token_differs", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO")),
        {MQ: multi(mq("OTHER_NSE", "SBIN_EQ"), mq("OTHER_NFO", "NIFTY25NOV26FUT", "NFO", 24005.5))}),
    req("multiquotes_joined_by_the_symbol_name", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: multi(mq("OTHER_NSE", "SBIN-EQ", symbolName="SBIN_EQ"))}),
    req("multiquotes_two_rows_name_the_same_symbol", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: multi(mq("OTHER_1", "SBIN_EQ", ltp=111.0), mq("OTHER_2", "ZZZ", ltp=222.0, symbolName="SBIN_EQ"))}),
    req("multiquotes_one_missing", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {MQ: multi(mq("3045_NSE", "SBIN_EQ"))}),
    req("multiquotes_indices_are_asked_one_by_one", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")), {MQ: multi(mq("3045_NSE", "SBIN_EQ")), IQ: [index_quote(), index_quote(79050.5)]}),
    req("multiquotes_an_index_that_fails", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("BANKNIFTY", "NSE_INDEX")), {IQ: [index_quote(), {"body": {"status": "Failure", "statusMessage": "Index not found"}}]}),
    req("multiquotes_indices_only", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"),), {IQ: index_quote()}),
    req("multiquotes_text_numbers", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: multi(mq("3045_NSE", "SBIN_EQ", bidPrice="", askSize=None, lastTradePrice="1,805.45", openInterest="12,000"))}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: {"body": {"status": "Failure", "statusMessage": "Bad request"}}}),
    req("multiquotes_rate_limited_is_tried_again", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: [RATE, multi(mq("3045_NSE", "SBIN_EQ"))]}),
    req("multiquotes_server_error_every_time", "/api/v1/multiquotes", legs(("SBIN", "NSE"),), {MQ: [DOWN, DOWN, DOWN, DOWN, multi(mq("3045_NSE", "SBIN_EQ"))]}),
    req("multiquotes_unknown_symbol_among_known", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE")), {MQ: multi(mq("3045_NSE", "SBIN_EQ"))}),
    req("multiquotes_more_than_twenty_five_go_in_batches", "/api/v1/multiquotes", legs(*[(f"STK{i:02d}", "NSE") for i in range(1, 27)]),
        {MQ: [multi(*[mq(f"{9000 + i}_NSE", f"STK{i:02d}_EQ", ltp=100 + i) for i in range(1, 26)]), multi(mq("9026_NSE", "STK26_EQ", ltp=126))]}),
]


def patch():
    import importlib

    data = importlib.import_module("broker.samco.api.data")
    data.time.sleep = lambda seconds: None
