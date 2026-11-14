"""IIFL Capital quotes, depth and several quotes: POST /marketdata/marketquotes (several at once), /marketdata/marketdepth, and /marketdata/openinterest (one instrument at a time, for futures and options only, best effort)."""

import scenarios_iiflcapital as s
from scenarios_iiflcapital import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS, req  # noqa: F401

BROKER = "iiflcapital"
QEP, DEP, OEP = "POST /v1/marketdata/marketquotes", "POST /v1/marketdata/marketdepth", "POST /v1/marketdata/openinterest"
LIMITED = {"status": 429, "body": {"status": "error", "message": "Too many requests"}}


def row(token, exchange="NSEEQ", ltp=805.45, **extra):
    d = {"exchange": exchange, "instrumentId": str(token), "ltp": ltp, "open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0, "volume": 1234567, "bestBidPrice": 805.4, "bestAskPrice": 805.5}
    d.update(extra)
    return d


def ok(*rows):
    return {"body": {"status": "Ok", "result": list(rows)}}


def oi(value=12000, key="openInterest"):
    return {"body": {"status": "Ok", "result": {key: value}}}


def level(price, qty, orders):
    return {"price": price, "quantity": qty, "orders": orders}


BUYS = [level(805.4 - 0.05 * i, 100 * (i + 1), i + 1) for i in range(5)]
SELLS = [level(805.5 + 0.05 * i, 110 * (i + 1), 2 * (i + 1)) for i in range(5)]


def drow(token, exchange="NSEEQ", **extra):
    d = {"exchange": exchange, "instrumentId": str(token), "ltp": 805.45, "ltq": 25, "open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0, "volume": 1234567, "oi": 0,
         "totalBidQuantity": 5000, "totalAskQuantity": 6100, "depth": {"buy": BUYS, "sell": SELLS}}
    d.update(extra)
    return d


def q(name, symbol, exchange, broker, path="/api/v1/quotes"):
    return req(name, path, {"symbol": symbol, "exchange": exchange}, broker)


def legs(*pairs):
    return {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}


EXPIRED = {"status": 401, "body": {"status": "error", "message": "Invalid session"}}
SCENARIOS = [
    q("quotes_equity_has_no_open_interest_call", "SBIN", "NSE", {QEP: ok(row(3045))}),
    q("quotes_future_reads_open_interest", "NIFTY25NOV26FUT", "NFO", {QEP: ok(row(49500, "NSEFO", 24005.5)), OEP: oi()}),
    q("quotes_open_interest_under_its_short_name", "NIFTY25NOV26FUT", "NFO", {QEP: ok(row(49500, "NSEFO", 24005.5)), OEP: oi(777, "oi")}),
    q("quotes_open_interest_in_a_list", "NIFTY25NOV2624000CE", "NFO", {QEP: ok(row(49599, "NSEFO", 7.35)), OEP: {"body": {"status": "Ok", "result": [{"openInterest": 4321}]}}}),
    q("quotes_open_interest_that_fails_is_zero", "NIFTY25NOV26FUT", "NFO", {QEP: ok(row(49500, "NSEFO", 24005.5)), OEP: {"status": 500, "body": {"status": "error", "message": "down"}}}),
    q("quotes_open_interest_not_an_object_is_zero", "NIFTY25NOV26FUT", "NFO", {QEP: ok(row(49500, "NSEFO", 24005.5)), OEP: {"body": {"status": "Ok", "result": 5}}}),
    q("quotes_commodity", "GOLDM25NOV26FUT", "MCX", {QEP: ok(row(80001, "MCXCOMM", 75000.0)), OEP: oi(55)}),
    q("quotes_currency", "USDINR25NOV26FUT", "CDS", {QEP: ok(row(90001, "NSECURR", 83.25)), OEP: oi(66)}),
    q("quotes_bse_equity", "TATAMOTORS", "BSE", {QEP: ok(row(500570, "BSEEQ", 500.5))}),
    q("quotes_index_uses_the_cash_segment", "NIFTY", "NSE_INDEX", {QEP: ok(row(26000, "NSEEQ", 24100.25))}),
    q("quotes_bse_index", "SENSEX", "BSE_INDEX", {QEP: ok(row(26001, "BSEEQ", 79050.5))}),
    q("quotes_other_names_for_the_figures", "SBIN", "NSE", {QEP: ok({"exchange": "NSEEQ", "instrumentId": "3045", "lastTradedPrice": 805.45, "openPrice": 801, "highPrice": 809.5, "lowPrice": 799, "previousClose": 800, "tradedVolume": 1000, "askPrice": 805.5, "bidPrice": 805.4})}),
    q("quotes_the_ask_under_its_misspelt_name", "SBIN", "NSE", {QEP: ok(row(3045, bestAskPrice=None, besAskPrice=805.6))}),
    q("quotes_text_figures", "SBIN", "NSE", {QEP: ok(row(3045, ltp="805.45", volume="1234567", open="-", high="", close="x"))}),
    q("quotes_touchline_style_row", "SBIN", "NSE", {QEP: ok({"instrumentId": "3045", "touchline": {"LastTradedPrice": 805.45, "Open": 801, "High": 809.5, "Low": 799, "Close": 800, "TotalTradedQuantity": 1000, "BidInfo": {"Price": 805.4}, "AskInfo": {"Price": 805.5}}})}),
    q("quotes_best_prices_from_the_first_depth_level", "SBIN", "NSE", {QEP: ok({"instrumentId": "3045", "ltp": 805.45, "depth": {"buy": [{"price": 805.4}], "sell": [{"price": 805.5}]}})}),
    q("quotes_rows_in_a_data_container", "SBIN", "NSE", {QEP: {"body": {"status": "Ok", "data": {"quotes": [row(3045)]}}}}),
    q("quotes_one_row_directly_in_the_result", "SBIN", "NSE", {QEP: {"body": {"status": "Ok", "result": row(3045)}}}),
    q("quotes_the_row_carries_an_error", "SBIN", "NSE", {QEP: ok({"instrumentId": "3045", "status": "failed", "message": "Instrument not found"})}),
    q("quotes_the_row_error_without_a_message", "SBIN", "NSE", {QEP: ok({"instrumentId": "3045", "status": "error"})}),
    q("quotes_a_row_that_is_not_a_quote", "SBIN", "NSE", {QEP: ok({"foo": "bar"})}),
    q("quotes_no_rows", "SBIN", "NSE", {QEP: {"body": {"status": "Ok", "result": []}}}),
    q("quotes_refused_with_a_message", "SBIN", "NSE", {QEP: {"status": 400, "body": {"status": "error", "message": "Invalid instrument"}}}),
    q("quotes_refused_with_an_error_text", "SBIN", "NSE", {QEP: {"status": 400, "body": {"error": "bad request"}}}),
    q("quotes_refused_with_nothing_useful", "SBIN", "NSE", {QEP: {"status": 500, "body": {"x": 1}}}),
    q("quotes_expired_session", "SBIN", "NSE", {QEP: EXPIRED}),
    q("quotes_a_rate_limited_call_is_tried_again", "SBIN", "NSE", {QEP: [LIMITED, ok(row(3045))]}),
    q("quotes_rate_limited_every_time", "SBIN", "NSE", {QEP: [LIMITED, LIMITED, LIMITED, LIMITED, ok(row(3045))]}),
    q("quotes_unknown_symbol", "NOPE", "NSE", {QEP: ok(row(3045))}),
    q("depth_equity", "SBIN", "NSE", {DEP: ok(drow(3045))}, "/api/v1/depth"),
    q("depth_future_reads_open_interest_when_the_answer_has_none", "NIFTY25NOV26FUT", "NFO", {DEP: ok(drow(49500, "NSEFO")), OEP: oi(9000)}, "/api/v1/depth"),
    q("depth_future_with_open_interest_in_the_answer", "NIFTY25NOV26FUT", "NFO", {DEP: ok(drow(49500, "NSEFO", oi=8000))}, "/api/v1/depth"),
    q("depth_fewer_than_five_levels_are_padded", "SBIN", "NSE", {DEP: ok(drow(3045, depth={"buy": BUYS[:2], "sell": []}))}, "/api/v1/depth"),
    q("depth_twenty_levels_are_cut_to_five", "SBIN", "NSE", {DEP: ok(drow(3045, depth={"buy": [level(800 - i, 10, 1) for i in range(20)], "sell": [level(810 + i, 10, 1) for i in range(20)]}))}, "/api/v1/depth"),
    q("depth_levels_as_lists", "SBIN", "NSE", {DEP: ok(drow(3045, depth={"buy": [[805.4, 100, 3], [805.35, 200]], "sell": [[805.5, 110, 4]]}))}, "/api/v1/depth"),
    q("depth_totals_are_the_sum_of_the_levels_when_missing", "SBIN", "NSE", {DEP: ok(drow(3045, totalBidQuantity=None, totalAskQuantity=None))}, "/api/v1/depth"),
    q("depth_other_names_for_the_totals", "SBIN", "NSE", {DEP: ok(drow(3045, totalBidQuantity=None, totalAskQuantity=None, totBuyQuan=111, totSellQuan=222))}, "/api/v1/depth"),
    q("depth_no_rows", "SBIN", "NSE", {DEP: {"body": {"status": "Ok", "result": []}}}, "/api/v1/depth"),
    q("depth_refused", "SBIN", "NSE", {DEP: {"status": 400, "body": {"status": "error", "message": "Invalid instrument"}}}, "/api/v1/depth"),
    q("depth_unknown_symbol", "NOPE", "NSE", {DEP: ok(drow(3045))}, "/api/v1/depth"),
    req("multiquotes", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NOPE", "NSE"), ("INFY", "NSE"), ("TATAMOTORS", "BSE")),
        {QEP: ok(row(3045), row(1594, ltp=1499.5), row(500570, "BSEEQ", 500.5))}),
    req("multiquotes_a_future_reads_its_open_interest", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO")), {QEP: ok(row(3045), row(49500, "NSEFO", 24005.5)), OEP: oi(12000)}),
    req("multiquotes_rows_without_an_identity_are_read_by_position", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")),
        {QEP: ok({"ltp": 805.45, "open": 801, "close": 800}, {"ltp": 1499.5, "open": 1490, "close": 1488})}),
    req("multiquotes_a_leg_missing_from_the_answer", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {QEP: ok(row(3045))}),
    req("multiquotes_a_row_with_an_error", "/api/v1/multiquotes", legs(("SBIN", "NSE"), ("INFY", "NSE")), {QEP: ok(row(3045), {"exchange": "NSEEQ", "instrumentId": "1594", "status": "error", "message": "Suspended"})}),
    req("multiquotes_the_index_is_found_under_its_cash_segment", "/api/v1/multiquotes", legs(("NIFTY", "NSE_INDEX"), ("SBIN", "NSE")), {QEP: ok(row(26000, "NSEEQ", 24100.25), row(3045))}),
    req("multiquotes_every_leg_unknown", "/api/v1/multiquotes", legs(("NOPE", "NSE"), ("NADA", "NFO")), {}),
    req("multiquotes_refused", "/api/v1/multiquotes", legs(("SBIN", "NSE")), {QEP: {"status": 400, "body": {"status": "error", "message": "Invalid instrument"}}}),
    req("multiquotes_no_rows", "/api/v1/multiquotes", legs(("SBIN", "NSE")), {QEP: {"body": {"status": "Ok", "result": []}}}),
]


def patch():
    pass
