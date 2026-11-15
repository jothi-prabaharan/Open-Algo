"""AliceBlue (a3.aliceblueonline.com open-api): orders, books and funds. The user session is the bearer token. Reads share a quota of 1800 a quarter hour (a sliding window); orders are never limited. A place goes as a one-item list and a per-item error with no broker order id is a refusal even when the envelope says Ok."""

AUTH_TOKEN = "ab-user-session"
ENV = {"BROKER_API_KEY": "ab-app-code", "BROKER_API_SECRET": "ab-secret"}
SEED_API_KEY_ROW = True
IGNORE = []
V = "/open-api/od/v1"
READ_POSTS = []


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("ODDTOKEN", "ODDTOKEN-EQ", "NSE", "777.0", "ODD"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, MOD_EP, CANCEL_EP = f"POST {V}/orders/placeorder", f"POST {V}/orders/modify", f"POST {V}/orders/cancel"
OB, TB, PB, HB, LB = f"GET {V}/orders/book", f"GET {V}/orders/trades", f"GET {V}/positions", f"GET {V}/holdings/CNC", f"GET {V}/limits/"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"status": "Ok", "message": "Order placed", "result": [{"status": "Ok", "brokerOrderId": "AB0001", "message": ""}]}}
REFUSED = {"status": 400, "body": {"status": "Not_Ok", "message": "EC912"}}


def place(name, request, reply=None, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply or OK_ORDER, **(extra or {})}, ignore=IGNORE)


def order(number, symbol, exch, side, qty, price, otype, product, status, **extra):
    o = {"brokerOrderId": number, "exchange": exch, "tradingSymbol": symbol, "formattedInstrumentName": symbol, "transactionType": side, "quantity": qty, "filledQuantity": 0, "cancelledQuantity": 0,
         "pendingQuantity": qty, "price": price, "slTriggerPrice": 0, "averageTradedPrice": 0, "orderType": otype, "product": product, "orderStatus": status, "orderTime": "03-11-2026 10:20:01",
         "rejectionReason": "", "orderTag": "openalgo", "orderComplexity": "REGULAR", "validity": "DAY", "exchangeOrderId": "", "instrumentId": "3045"}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "Ok", "result": [
    order("AB1", "SBIN-EQ", "NSE", "BUY", 10, 800.5, "LIMIT", "INTRADAY", "OPEN"),
    order("AB2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, 24010, "SL", "NRML", "TRIGGER PENDING", slTriggerPrice=24000),
    order("AB3", "INFY-EQ", "NSE", "SELL", 5, 1495, "LIMIT", "LONGTERM", "COMPLETE", filledQuantity=5, pendingQuantity=0, averageTradedPrice=1499.95),
    order("AB4", "SBIN-EQ", "NSE", "SELL", 1, 0, "SLM", "INTRADAY", "REJECTED", slTriggerPrice=790.5, rejectionReason="RMS: insufficient funds"),
    order("AB5", "TATAMOTORS-EQ", "BSE", "BUY", 2, 0, "MARKET", "NRML", "CANCELLED", cancelledQuantity=2),
    order("AB6", "USDINR25NOV26FUT", "CDS", "BUY", 1000, 83.5, "LIMIT", "MTF", "PENDING"),
    order("AB7", "GOLDM25NOV26FUT", "MCX", "SELL", 10, 75000, "STOP", "DELIVERY", "Something Else"),
    order("AB8", "NOSUCH-EQ", "NSE", "BUY", 3, 5, "XX", "ZZ", "OPEN"),
    order("AB9", "SBIN-EQ", "NSE", "BUY", 4, 800, "LIMIT", "INTRADAY", "OPEN", tradingSymbol="SBIN-EQ", formattedInstrumentName=""),
]}}
CANCEL_BOOK = {"body": {"status": "Ok", "result": [
    order("AB1", "SBIN-EQ", "NSE", "BUY", 10, 800.5, "LIMIT", "INTRADAY", "OPEN"),
    order("AB2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, 24010, "SL", "NRML", "TRIGGER PENDING"),
    order("AB3", "INFY-EQ", "NSE", "SELL", 5, 1495, "LIMIT", "LONGTERM", "COMPLETE"),
    order("AB4", "SBIN-EQ", "NSE", "SELL", 1, 0, "SLM", "INTRADAY", "trigger pending"),
]}}
EMPTY = {"body": {"status": "Ok", "result": []}}
NO_ORDERS = {"body": {"status": "Not_Ok", "message": "Failed to retrieve the order book"}}
FAILED = {"body": {"status": "Not_Ok", "message": "EC915"}}
TRADES = {"body": {"status": "Ok", "result": [
    {"brokerOrderId": "AB3", "exchange": "NSE", "tradingSymbol": "INFY-EQ", "formattedInstrumentName": "INFY-EQ", "transactionType": "SELL", "filledQuantity": 5, "tradedPrice": 1499.95, "product": "LONGTERM", "orderType": "LIMIT",
     "fillTimestamp": "03-11-2026 10:21:12", "orderTag": "openalgo", "exchangeOrderId": "E1", "exchangeTradeId": "T1"},
    {"brokerOrderId": "AB2", "exchange": "NFO", "tradingSymbol": "NIFTY25NOV26FUT", "transactionType": "BUY", "filledQuantity": 75, "tradedPrice": 24005.5, "product": "NRML", "orderType": "SL", "fillTimestamp": "10:22:00"},
    {"brokerOrderId": "AB99", "exchange": "NSE", "tradingSymbol": "NOSUCH-EQ", "transactionType": "BUY", "filledQuantity": 2, "tradedPrice": 2900, "product": "INTRADAY", "orderType": "MARKET", "orderTime": "garbled"},
    {"brokerOrderId": "AB98", "exchange": "NSE", "tradingSymbol": "SBIN-EQ", "transactionType": "BUY", "filledQuantity": 1, "tradedPrice": 0, "product": "INTRADAY", "orderType": "MARKET"},
]}}


def pos(symbol, exch, product, net, **extra):
    p = {"tradingSymbol": symbol, "exchange": exch, "product": product, "netQuantity": net, "buyQuantity": max(net, 0), "sellQuantity": max(-net, 0), "dayBuyPrice": 0, "daySellPrice": 0, "ltp": 0, "mtm": 0,
         "unrealisedPnl": 0, "realizedPnl": 0, "instrumentId": "3045"}
    p.update(extra)
    return p


POSITIONS = {"body": {"status": "Ok", "result": [
    pos("SBIN-EQ", "NSE", "INTRADAY", 10, dayBuyPrice=800.0, ltp=805.45, mtm=54.5, unrealisedPnl=54.5, realizedPnl=12.5),
    pos("NIFTY25NOV26FUT", "NFO", "NRML", -75, daySellPrice=24000.0, ltp=24005.5, mtm=-412.5, unrealisedPnl=0, realizedPnl=0),
    pos("INFY-EQ", "NSE", "LONGTERM", 0, dayBuyPrice=1500.0, ltp=1510, realizedPnl=100),
    pos("GOLDM25NOV26FUT", "MCX", "NRML", 10, dayBuyPrice=75000.0, ltp=75010),
    pos("NOSUCH-EQ", "NSE", "INTRADAY", 4),
    {"formattedInstrumentName": "USDINR25NOV26FUT", "exchange": "CDS", "product": "NRML", "netQuantity": 2, "netAveragePrice": 83.5, "previousDayClose": 83.4, "markToMarket": 3.0, "unrealisedProfitLoss": 3.0},
]}}
FLAT = {"body": {"status": "Ok", "result": []}}
NO_POSITIONS = {"body": {"status": "Not_Ok", "message": "No positions found for this user"}}
FAILED_POSITIONS = {"body": {"status": "Not_Ok", "message": "EC919"}}
HOLDINGS = {"body": {"status": "Ok", "result": [
    {"nseTradingSymbol": "INFY-EQ", "bseTradingSymbol": "INFY", "dpQuantity": 6, "t1Quantity": 4, "ltp": 1510, "averageTradedPrice": 1450.5, "isin": "INE009A01021", "sellableQty": 6, "nseInstrumentId": "1594"},
    {"nseTradingSymbol": "", "bseTradingSymbol": "TATAMOTORS-EQ", "dpQuantity": 0, "t1Quantity": 4, "ltp": 510, "investedPrice": 500, "isin": "INE155A01022", "sellableQty": 0, "bseInstrumentId": "500570"},
    {"nseTradingSymbol": "SBIN-EQ", "bseTradingSymbol": "", "totalQuantity": 3, "ltp": 805, "averageTradedPrice": 800, "isin": "INE062A01020"},
    {"nseTradingSymbol": "NOSUCH-EQ", "dpQuantity": 2, "ltp": 11, "averageTradedPrice": 10},
    {"nseTradingSymbol": "SBIN-EQ", "dpQuantity": 0, "t1Quantity": 0, "ltp": 805, "averageTradedPrice": 800},
]}}
LIMITS = {"body": {"status": "Ok", "result": [{"tradingLimit": 100000.5, "collateralMargin": 2500.25, "utilizedMargin": 1184.5}]}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "AB1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"status": "Ok", "result": [{"brokerOrderId": "AB1", "status": "Ok"}]}}
CANCEL_OK = {"body": {"status": "Ok", "result": [{"brokerOrderId": "AB1", "status": "Ok"}]}}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_delivery", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_product", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_disclosed_quantity", {**PLACE, "disclosed_quantity": "5"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_token_with_a_decimal_point", {**PLACE, "symbol": "ODDTOKEN"}),
    place("placeorder_integer_price_is_sent_as_given", {**PLACE, "price": "800"}),
    place("placeorder_refused", PLACE, REFUSED),
    place("placeorder_refused_with_prose", PLACE, {"status": 400, "body": {"status": "Not_Ok", "message": "Margin shortfall"}}),
    place("placeorder_refused_without_a_message", PLACE, {"status": 400, "body": {"status": "Not_Ok"}}),
    place("placeorder_refused_with_status_200", PLACE, {"body": {"status": "Not_Ok", "message": "Rejected by RMS"}}),
    place("placeorder_item_error_without_an_order_id", PLACE, {"body": {"status": "Ok", "result": [{"status": "Not_Ok", "brokerOrderId": "", "message": "Quantity must be a multiple of the lot size"}]}}),
    place("placeorder_item_error_with_an_order_id", PLACE, {"body": {"status": "Ok", "result": [{"status": "Not_Ok", "brokerOrderId": "AB5", "message": "Partly accepted"}]}}),
    place("placeorder_item_without_a_status", PLACE, {"body": {"status": "Ok", "result": [{"brokerOrderId": "AB6"}]}}),
    place("placeorder_ok_with_no_result", PLACE, {"body": {"status": "Ok", "result": []}}),
    place("placeorder_ok_without_an_order_number", PLACE, {"body": {"status": "Ok", "result": [{"status": "Ok"}]}}),
    place("placeorder_not_json", PLACE, {"status": 502, "body": "Bad gateway"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {MOD_EP: MOD_OK}),
    req("modifyorder_disclosed", "/api/v1/modifyorder", {**CHANGE, "disclosed_quantity": "3"}, {MOD_EP: MOD_OK}),
    req("modifyorder_reply_without_an_order_id", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"body": {"status": "Ok", "result": [{"status": "Ok"}]}}}),
    req("modifyorder_reply_with_no_result", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"body": {"status": "Ok", "result": []}}}),
    req("modifyorder_reply_with_another_order_id", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"body": {"status": "Ok", "result": [{"brokerOrderId": "AB77"}]}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "Not_Ok", "message": "Order is not open"}}}),
    req("modifyorder_refused_with_status_200", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"body": {"status": "Not_Ok", "message": "Too late"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "Not_Ok"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB1"}, {CANCEL_EP: CANCEL_OK}),
    req("cancelorder_reply_with_another_order_id", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB1"}, {CANCEL_EP: {"body": {"status": "Ok", "result": [{"brokerOrderId": "AB1X"}]}}}),
    req("cancelorder_reply_with_no_result", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB1"}, {CANCEL_EP: {"body": {"status": "Ok", "result": []}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB3"}, {CANCEL_EP: {"status": 404, "body": {"status": "Not_Ok", "message": "Order not found to cancel"}}}),
    req("cancelorder_refused_with_status_200", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB3"}, {CANCEL_EP: {"body": {"status": "Not_Ok", "message": "Already complete"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "AB3"}, {CANCEL_EP: {"status": 400, "body": {"status": "Not_Ok"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCEL_EP: [{"body": {"status": "Ok", "result": [{"brokerOrderId": "AB1"}]}}, {"status": 400, "body": {"status": "Not_Ok", "message": "No"}}, {"body": {"status": "Ok", "result": []}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY}),
    req("orderbook_none_found_is_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_no_orders_text_is_empty", "/api/v1/orderbook", {}, {OB: {"body": {"status": "Not_Ok", "message": "No Orders for today"}}}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": {"x": 1}}}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: {"status": 200, "body": "<html>"}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: EMPTY}),
    req("tradebook_none_found_is_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "Not_Ok", "message": "No trades found for this user"}}}),
    req("tradebook_not_found_text_is_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "Not_Ok", "message": "Trade not found"}}}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: {"body": {"status": "Not_Ok", "message": "EC927"}}}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_none_found_is_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_failed_to_retrieve_is_empty", "/api/v1/positionbook", {}, {PB: {"body": {"status": "Not_Ok", "message": "Failed to retrieve the position book."}}}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED_POSITIONS}),
    req("positionbook_http_error", "/api/v1/positionbook", {}, {PB: {"status": 404, "body": {"x": 1}}}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: EMPTY}),
    req("holdings_none_found_is_empty", "/api/v1/holdings", {}, {HB: {"body": {"status": "Not_Ok", "message": "No holdings found for this user"}}}),
    req("holdings_failed_to_retrieve_is_empty", "/api/v1/holdings", {}, {HB: {"body": {"status": "Not_Ok", "message": "Failed to retrieve holdings"}}}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: {"body": {"status": "Not_Ok", "message": "EC921"}}}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS, PB: POSITIONS}),
    req("funds_without_positions", "/api/v1/funds", {}, {LB: LIMITS, PB: FLAT}),
    req("funds_positions_refused_leaves_realised_zero", "/api/v1/funds", {}, {LB: LIMITS, PB: FAILED_POSITIONS}),
    req("funds_positions_http_error_leaves_realised_zero", "/api/v1/funds", {}, {LB: LIMITS, PB: {"status": 500, "body": {"x": 1}}}),
    req("funds_with_no_result", "/api/v1/funds", {}, {LB: EMPTY, PB: POSITIONS}),
    req("funds_text_that_is_not_a_number", "/api/v1/funds", {}, {LB: {"body": {"status": "Ok", "result": [{"tradingLimit": "n/a"}]}}, PB: FLAT}),
    req("funds_missing_fields_are_zero", "/api/v1/funds", {}, {LB: {"body": {"status": "Ok", "result": [{}]}}, PB: FLAT}),
    req("funds_failed", "/api/v1/funds", {}, {LB: {"body": {"status": "Not_Ok", "message": "EC924"}}, PB: POSITIONS}),
    req("funds_http_error", "/api/v1/funds", {}, {LB: {"status": 500, "body": {"x": 1}}, PB: POSITIONS}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: FAILED_POSITIONS}),
    req("placesmartorder_no_position_book_known_empty", "/api/v1/placesmartorder", {**PLACE, "quantity": "5", "position_size": "5"}, {PB: {"body": {"status": "Not_Ok", "message": "EC920"}}, PLACE_EP: OK_ORDER}, ignore=IGNORE),
]


def patch():
    import importlib

    limiter = importlib.import_module("broker.aliceblue.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
    limiter.reset()


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's."""
    import importlib

    importlib.import_module("broker.aliceblue.api.order_api")._position_cache.clear()
