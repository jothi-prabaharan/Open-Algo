"""Groww quotes, depth and several quotes: GET /v1/live-data/quote with the exchange, segment and trading symbol for one symbol (an index is asked for in the cash segment of its exchange). Several quotes ask /v1/live-data/ohlc and /v1/live-data/ltp for each segment, 50 symbols a
request, and for F&O symbols then add the bid, ask, volume and open interest from one quote request each. A call that Groww refuses is an error beside the symbol."""

import scenarios_groww as s
from scenarios_groww import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401

BROKER = "groww"
READ_POSTS = []
SYMBOLS = s.SYMBOLS + [
    s._sym("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "NIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "SENSEX", 1, "INDEX"),
    s._sym("NOBRSYMBOL", "", "NSE", "NSE", 7001, "NO BROKER SYMBOL"),
    s._sym("SBIN30SEP25FUT", "", "NFO", "NSE", 7002, "SBIN", 750, "FUT", "30-SEP-25", -1.0, 0.05),
    s._sym("SBIN30SEP25800CE", "", "NFO", "NSE", 7003, "SBIN", 750, "CE", "30-SEP-25", 800.0, 0.05),
] + [s._sym(f"STK{i:02d}", f"STK{i:02d}", "NSE", "NSE", 9000 + i, f"STOCK {i}") for i in range(1, 54)]
QUOTE, OHLC, LTP = "GET /v1/live-data/quote", "GET /v1/live-data/ohlc", "GET /v1/live-data/ltp"


def quote(ltp=805.45, **extra):
    p = {"last_price": ltp, "ohlc": {"open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0}, "day_change": 5.45, "day_change_perc": 0.68, "volume": 1234567, "bid_price": 805.4, "bid_quantity": 100, "offer_price": 805.5, "offer_quantity": 110,
         "total_buy_quantity": 45000, "total_sell_quantity": 52000, "open_interest": 7000, "last_trade_time": 1793700000000, "last_trade_quantity": 25, "upper_circuit_limit": 880.0, "lower_circuit_limit": 720.0,
         "depth": {"buy": [{"price": 805.4 - 0.05 * i, "quantity": 100 * (i + 1)} for i in range(5)], "sell": [{"price": 805.5 + 0.05 * i, "quantity": 110 * (i + 1)} for i in range(5)]}}
    p.update(extra)
    return s.ok(p)


def one(name, path, symbol, exchange, broker):
    return {"name": name, "path": path, "request": {"symbol": symbol, "exchange": exchange}, "broker": broker}


def many(name, pairs, broker):
    return {"name": name, "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": a, "exchange": b} for a, b in pairs]}, "broker": broker}


def ohlc_reply(**prices):
    return s.ok({k: {"open": 800.0, "high": 810.0, "low": 795.0, "close": 799.0} for k in prices} if not prices else prices)


OH = {"NSE_SBIN": {"open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0}, "NSE_INFY": {"open": 1495.0, "high": 1510.0, "low": 1490.0, "close": 1500.0}, "BSE_TATAMOTORS": "{open: 499.5,high: 505.5,low: 498.5,close: 500.0}"}
LT = {"NSE_SBIN": 805.45, "NSE_INFY": 1499.5, "BSE_TATAMOTORS": 501.25}
FOH = {"NSE_NIFTY26NOVFUT": {"open": 24000.0, "high": 24100.0, "low": 23950.0, "close": 24005.0}, "BSE_SENSEX26NOVFUT": {"open": 80000.0, "high": 80500.0, "low": 79900.0, "close": 80100.0}}
FLT = {"NSE_NIFTY26NOVFUT": 24010.5, "BSE_SENSEX26NOVFUT": 80120.0}
FQ = quote(24010.5, ohlc={"open": 24000.0, "high": 24100.0, "low": 23950.0, "close": 24005.0}, bid_price=24010.0, offer_price=24011.0, volume=500000, open_interest=1200000)

SCENARIOS = [
    one("quotes_equity", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: quote()}),
    one("quotes_bse", "/api/v1/quotes", "TATAMOTORS", "BSE", {QUOTE: quote(501.25)}),
    one("quotes_index", "/api/v1/quotes", "NIFTY", "NSE_INDEX", {QUOTE: quote(24100.25)}),
    one("quotes_bse_index", "/api/v1/quotes", "SENSEX", "BSE_INDEX", {QUOTE: quote(80000.5)}),
    one("quotes_future_has_open_interest", "/api/v1/quotes", "NIFTY25NOV26FUT", "NFO", {QUOTE: FQ}),
    one("quotes_bse_future", "/api/v1/quotes", "SENSEX26NOVFUT", "BFO", {QUOTE: FQ}),
    one("quotes_equity_has_no_open_interest", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: quote(open_interest=9999)}),
    one("quotes_ohlc_as_text", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: quote(ohlc="{open: 149.50,high: 150.50,low: 148.50,close: 149.50}")}),
    one("quotes_ohlc_as_text_that_does_not_parse", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: quote(ohlc="{open: x}")}),
    one("quotes_ohlc_missing", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({"last_price": 805.45})}),
    one("quotes_ohlc_null", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: quote(ohlc=None)}),
    one("quotes_other_names_for_bid_and_ask", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({"last_price": 805.45, "bid": 805.4, "best_offer_price": 805.5, "bid_size": 11, "offer_size": 12, "total_volume": 777, "oi": 5, "ohlc": {}})}),
    one("quotes_bid_and_ask_from_the_depth", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({"last_price": 805.45, "ohlc": {"close": 800}, "depth": {"buy": [{"price": 805.3, "quantity": 7}], "sell": [{"price": 805.6, "quantity": 8}]}})}),
    one("quotes_values_as_text", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({"last_price": "805.45", "volume": "1234567", "bid_price": "805.4", "ohlc": {"open": "801", "close": "800"}})}),
    one("quotes_values_that_are_not_numbers", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({"last_price": "x", "volume": "y", "ohlc": {"open": "z"}})}),
    one("quotes_with_an_empty_payload", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.ok({})}),
    one("quotes_payload_that_is_not_an_object", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: {"body": {"status": "SUCCESS", "payload": "x"}}}),
    one("quotes_failure_status", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: {"body": {"status": "FAILURE", "error": {"message": "Not found"}}}}),
    one("quotes_refused_with_a_reason", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.fail(400, "Invalid trading symbol", "GA050")}),
    one("quotes_unauthorised", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.fail(401, "Invalid token", "GA002")}),
    one("quotes_server_error_with_no_json", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: {"status": 500, "body": "oops"}}),
    one("quotes_throttled_then_answered", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: [s.THROTTLED, quote()]}),
    one("quotes_throttled_every_time", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.THROTTLED}),
    one("quotes_answer_is_not_json", "/api/v1/quotes", "SBIN", "NSE", {QUOTE: s.NOT_JSON}),
    one("quotes_mcx_is_refused", "/api/v1/quotes", "GOLDM25NOV26FUT", "MCX", {}),
    one("quotes_symbol_with_no_broker_symbol", "/api/v1/quotes", "NOBRSYMBOL", "NSE", {QUOTE: quote()}),
    one("quotes_future_with_no_broker_symbol_is_converted", "/api/v1/quotes", "SBIN30SEP25FUT", "NFO", {QUOTE: FQ}),
    one("quotes_option_with_no_broker_symbol_is_converted", "/api/v1/quotes", "SBIN30SEP25800CE", "NFO", {QUOTE: FQ}),
    one("depth_equity", "/api/v1/depth", "SBIN", "NSE", {QUOTE: quote()}),
    one("depth_future", "/api/v1/depth", "NIFTY25NOV26FUT", "NFO", {QUOTE: FQ}),
    one("depth_index", "/api/v1/depth", "NIFTY", "NSE_INDEX", {QUOTE: quote(24100.25, depth=None)}),
    one("depth_short_book_is_padded", "/api/v1/depth", "SBIN", "NSE", {QUOTE: quote(depth={"buy": [{"price": 805.4, "quantity": 100}], "sell": []})}),
    one("depth_long_book_is_cut_to_five", "/api/v1/depth", "SBIN", "NSE", {QUOTE: quote(depth={"buy": [{"price": 805 - i, "quantity": 10} for i in range(7)], "sell": [{"price": 806 + i, "quantity": 10} for i in range(7)]})}),
    one("depth_levels_with_no_price_are_kept", "/api/v1/depth", "SBIN", "NSE", {QUOTE: quote(depth={"buy": [{"quantity": 100}, {"price": None, "quantity": None}], "sell": []})}),
    one("depth_with_no_depth_key", "/api/v1/depth", "SBIN", "NSE", {QUOTE: s.ok({"last_price": 805.45, "ohlc": {"close": 800}})}),
    one("depth_ohlc_as_text", "/api/v1/depth", "SBIN", "NSE", {QUOTE: quote(ohlc="{open: 1,high: 2,low: 0.5,close: 1.5}")}),
    one("depth_ohlc_missing", "/api/v1/depth", "SBIN", "NSE", {QUOTE: s.ok({"last_price": 805.45})}),
    one("depth_failure_status", "/api/v1/depth", "SBIN", "NSE", {QUOTE: {"body": {"status": "FAILURE", "error": {"message": "x"}}}}),
    one("depth_refused", "/api/v1/depth", "SBIN", "NSE", {QUOTE: s.fail(400, "Invalid trading symbol", "GA050")}),
    one("depth_server_error", "/api/v1/depth", "SBIN", "NSE", {QUOTE: {"status": 500, "body": "oops"}}),
    one("depth_mcx_is_refused", "/api/v1/depth", "GOLDM25NOV26FUT", "MCX", {}),
    one("depth_future_with_no_broker_symbol_is_converted", "/api/v1/depth", "SBIN30SEP25FUT", "NFO", {QUOTE: FQ}),
    many("multiquotes_cash", [("SBIN", "NSE"), ("INFY", "NSE"), ("TATAMOTORS", "BSE")], {OHLC: s.ok(OH), LTP: [s.ok({"NSE_SBIN": 805.45, "NSE_INFY": 1499.5}), s.ok({"BSE_TATAMOTORS": 501.25})]}),
    many("multiquotes_indexes", [("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")], {OHLC: s.ok({"NSE_NIFTY": {"open": 24000.0, "high": 24100.0, "low": 23950.0, "close": 24005.0}, "BSE_SENSEX": {"open": 80000.0, "high": 80500.0, "low": 79900.0, "close": 80100.0}}), LTP: s.ok({"NSE_NIFTY": 24100.25, "BSE_SENSEX": 80200.5})}),
    many("multiquotes_f_and_o_adds_the_full_quote", [("NIFTY25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO")], {OHLC: s.ok(FOH), LTP: s.ok(FLT), QUOTE: [FQ, FQ]}),
    many("multiquotes_cash_and_f_and_o", [("SBIN", "NSE"), ("NIFTY25NOV26FUT", "NFO")], {OHLC: [s.ok({"NSE_SBIN": OH["NSE_SBIN"]}), s.ok({"NSE_NIFTY26NOVFUT": FOH["NSE_NIFTY26NOVFUT"]})], LTP: [s.ok({"NSE_SBIN": 805.45}), s.ok({"NSE_NIFTY26NOVFUT": 24010.5})], QUOTE: FQ}),
    many("multiquotes_a_symbol_with_no_price", [("SBIN", "NSE"), ("INFY", "NSE")], {OHLC: s.ok({"NSE_SBIN": OH["NSE_SBIN"]}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_a_price_with_no_ohlc", [("SBIN", "NSE")], {OHLC: s.ok({}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_ohlc_text_that_does_not_parse", [("SBIN", "NSE")], {OHLC: s.ok({"NSE_SBIN": "{open: x}"}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_ohlc_values_missing_are_zero", [("SBIN", "NSE")], {OHLC: s.ok({"NSE_SBIN": {"close": 800}}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_the_price_read_refused", [("SBIN", "NSE")], {OHLC: s.ok(OH), LTP: s.fail(500, "Down", "GA500")}),
    many("multiquotes_the_price_read_not_json", [("SBIN", "NSE")], {OHLC: s.ok(OH), LTP: s.NOT_JSON}),
    many("multiquotes_the_ohlc_read_refused", [("SBIN", "NSE"), ("INFY", "NSE")], {OHLC: s.fail(500, "Down", "GA500")}),
    many("multiquotes_the_ohlc_read_unauthorised", [("SBIN", "NSE")], {OHLC: s.fail(401, "Invalid token", "GA002")}),
    many("multiquotes_the_ohlc_answer_has_no_success", [("SBIN", "NSE")], {OHLC: {"body": {"status": "FAILURE", "error": {"message": "x"}}}, LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_a_symbol_groww_does_not_know_is_left_out_and_the_rest_retried", [("SBIN", "NSE"), ("INFY", "NSE")], {OHLC: [{"status": 400, "body": {"status": "FAILURE", "error": {"code": "GA060", "message": "Invalid trading symbol: INFY"}}}, s.ok({"NSE_SBIN": OH["NSE_SBIN"]})], LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_an_invalid_symbol_that_is_the_only_one", [("SBIN", "NSE")], {OHLC: {"status": 400, "body": {"status": "FAILURE", "error": {"code": "GA060", "message": "Invalid trading symbol: SBIN"}}}}),
    many("multiquotes_mcx_and_the_rest", [("SBIN", "NSE"), ("GOLDM25NOV26FUT", "MCX")], {OHLC: s.ok({"NSE_SBIN": OH["NSE_SBIN"]}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_symbol_with_no_broker_symbol", [("NOBRSYMBOL", "NSE"), ("SBIN", "NSE")], {OHLC: s.ok({"NSE_SBIN": OH["NSE_SBIN"]}), LTP: s.ok({"NSE_SBIN": 805.45})}),
    many("multiquotes_future_with_no_broker_symbol_is_converted", [("SBIN30SEP25FUT", "NFO")], {OHLC: s.ok({"NSE_SBIN25SEPFUT": FOH["NSE_NIFTY26NOVFUT"]}), LTP: s.ok({"NSE_SBIN25SEPFUT": 24010.5}), QUOTE: FQ}),
    many("multiquotes_only_symbols_that_cannot_be_asked", [("GOLDM25NOV26FUT", "MCX")], {}),
    many("multiquotes_f_and_o_overlay_refused_keeps_the_baseline", [("NIFTY25NOV26FUT", "NFO")], {OHLC: s.ok(FOH), LTP: s.ok(FLT), QUOTE: s.fail(500, "Down", "GA500")}),
    many("multiquotes_f_and_o_overlay_stops_after_four_throttles", [(f"STK{i:02d}", "NFO") for i in range(0)] + [("NIFTY25NOV26FUT", "NFO"), ("SENSEX26NOVFUT", "BFO"), ("SBIN30SEP25FUT", "NFO"), ("SBIN30SEP25800CE", "NFO")], {OHLC: [s.ok({"NSE_NIFTY26NOVFUT": FOH["NSE_NIFTY26NOVFUT"], "NSE_SBIN25SEPFUT": FOH["NSE_NIFTY26NOVFUT"], "NSE_SBIN25SEP800CE": FOH["NSE_NIFTY26NOVFUT"], "BSE_SENSEX26NOVFUT": FOH["BSE_SENSEX26NOVFUT"]})], LTP: s.ok({"NSE_NIFTY26NOVFUT": 24010.5, "NSE_SBIN25SEPFUT": 1.5, "NSE_SBIN25SEP800CE": 2.5, "BSE_SENSEX26NOVFUT": 80120.0}), QUOTE: s.THROTTLED}),
    many("multiquotes_more_than_fifty_symbols_are_two_batches", [(f"STK{i:02d}", "NSE") for i in range(1, 54)], {OHLC: s.ok({f"NSE_STK{i:02d}": {"open": 1.0, "high": 2.0, "low": 0.5, "close": 1.5} for i in range(1, 54)}), LTP: s.ok({f"NSE_STK{i:02d}": 1.75 for i in range(1, 54)})}),
]


def before_each(sc):
    s.before_each(sc)
