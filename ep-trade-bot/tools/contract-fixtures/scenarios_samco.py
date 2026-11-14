"""Samco scenarios: orders, books and funds on tradeapi.samco.in. The session token goes in x-session-token; Samco's order APIs take only limit and stop-limit orders, so a market order is a limit order a slab beyond the last price (read from /quote/getQuote) and a stop-market a stop-limit beyond its trigger, each with the slab as marketProtection."""

AUTH_TOKEN = "samco-session-token"
ENV = {"BROKER_API_KEY": "samco-api-key", "BROKER_API_SECRET": "samco-api-secret"}
SEED_API_KEY_ROW = True
IGNORE = []


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05, brexchange=None):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN_EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY_EQ", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS_EQ", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY", "NIFTY 50", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0, "MFO"),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, OB, TB, HB, LB = "POST /order/placeOrder", "GET /order/orderBook", "GET /trade/tradeBook", "GET /holding/getHoldings", "GET /limit/getLimits"
PB, QB = "GET /position/getPositions", "GET /quote/getQuote"
ORDER_OK = {"body": {"status": "Success", "statusMessage": "Order placed", "orderNumber": "S0001"}}
SERVER_REFUSED = {"status": 500, "body": {"status": "Failure", "statusMessage": "Internal error"}}
ORDER_REFUSED = {"status": 400, "body": {"status": "Failure", "statusMessage": "Insufficient funds"}}
ORDER_NO_NUMBER = {"body": {"status": "Success", "statusMessage": "ok"}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def quote(ltp=805.45, **extra):
    d = {"lastTradedPrice": str(ltp), "openValue": "801.00", "highValue": "809.50", "lowValue": "799.00", "previousClose": "800.00", "totalTradedVolume": "1,234,567", "openInterest": "0",
         "bestBids": [{"price": "805.40", "quantity": "100"}], "bestAsks": [{"price": "805.50", "quantity": "110"}]}
    d.update(extra)
    return {"body": {"status": "Success", "quoteDetails": d}}


SBIN_Q = quote()


def place(name, request, reply=ORDER_OK, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply, **(extra or {})}, ignore=IGNORE)


def order(number, symbol, exch, side, qty, price, trigger, otype, product, status, **extra):
    o = {"orderNumber": number, "tradingSymbol": symbol, "exchange": exch, "transactionType": side, "totalQuanity": qty, "orderPrice": price, "triggerPrice": trigger, "orderType": otype,
         "productCode": product, "orderStatus": status, "orderTime": "03-Nov-2026 10:20:01", "filledQuantity": "0", "unfilledQuantity": str(qty), "averagePrice": "0", "rejectionReason": "--"}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "Success", "orderBookDetails": [
    order("S1", "SBIN_EQ", "NSE", "BUY", "10", "800.50", "0.00", "L", "MIS", "Open"),
    order("S2", "NIFTY25NOV26FUT", "NFO", "BUY", "75", "24010.00", "24000.00", "SL", "NRML", "Trigger Pending"),
    order("S3", "INFY_EQ", "NSE", "SELL", "5", "1495.00", "0.00", "L", "CNC", "Complete", marketProtection="2", filledQuantity="5", unfilledQuantity="0", averagePrice="1,499.95"),
    order("S4", "SBIN_EQ", "NSE", "SELL", "1", "0.00", "790.50", "SL-M", "MIS", "Rejected", rejectionReason="Price out of range"),
    order("S5", "TATAMOTORS_EQ", "BSE", "BUY", "2", "0.00", "0.00", "MKT", "CNC", "Cancelled"),
    order("S6", "USDINR25NOV26FUT", "CDS", "BUY", "1000", "83.50", "0", "L", "NRML", "Pending"),
    order("S7", "GOLDM25NOV26FUT", "MCX", "SELL", "10", "75000", "0", "L", "NRML", "After Market Order Req Received"),
    order("S8", "NOSUCH", "NSE", "BUY", "3", "5", "0", "XX", "ODD", "Something Else", pendingQuantity="2", unfilledQuantity=None),
    order("S9", "SBIN_EQ", "NSE", "BUY", "4", "800", "0", "L", "MIS", "Executed", fillPrice="801.25", averagePrice=""),
]}}
NO_ORDERS = {"body": {"status": "Success", "orderBookDetails": []}}
NULL_ORDERS = {"body": {"status": "Success", "orderBookDetails": None}}
ORDER_ERROR = {"body": {"status": "Failure", "statusMessage": "bad"}}

TRADES = {"body": {"status": "Success", "tradeBookDetails": [
    {"orderNumber": "S3", "tradingSymbol": "INFY_EQ", "exchange": "NSE", "transactionType": "SELL", "filledQuantity": "5", "tradePrice": "1499.95", "orderValue": "7499.75", "productCode": "CNC", "tradeTime": "03-Nov-2026 10:21:12"},
    {"orderNumber": "S2", "tradingSymbol": "NIFTY25NOV26FUT", "exchange": "NFO", "transactionType": "BUY", "filledQuantity": "75", "tradePrice": "24005.50", "orderValue": "1800412.5", "productCode": "NRML", "tradeTime": "03-Nov-2026 10:22:00"},
    {"orderNumber": "S9", "tradingSymbol": "NOSUCH", "exchange": "NSE", "transactionType": "BUY", "filledQuantity": "2", "tradePrice": "2900", "orderValue": "5800", "productCode": "MIS", "tradeTime": "03-Nov-2026 10:23:00"},
]}}


def pos(symbol, exch, product, net, side, avg_buy, avg_sell, ltp_, realised=0, unrealised=0):
    return {"tradingSymbol": symbol, "exchange": exch, "productCode": product, "netQuantity": str(net), "transactionType": side, "averageBuyPrice": avg_buy, "averageSellPrice": avg_sell,
            "lastTradedPrice": ltp_, "realizedGainAndLoss": realised, "unrealizedGainAndLoss": unrealised}


DAY = {"body": {"status": "Success", "positionDetails": [
    pos("SBIN_EQ", "NSE", "MIS", 10, "BUY", "800.00", "0", "805.45", 12.5, 54.5),
    pos("NIFTY25NOV26FUT", "NFO", "NRML", 75, "SELL", "0", "24,000.00", "24,005.50", 0, "-412.5"),
    pos("INFY_EQ", "NSE", "CNC", 0, "BUY", "1500", "1510", "1505", 100, 0),
    pos("GOLDM25NOV26FUT", "MCX", "NRML", 10, "BUY", "75000", "0", "75010", 0, 100),
    pos("NOSUCH", "NSE", "MIS", 4, "BUY", "", "", "", None, None),
]}}
NET = {"body": {"status": "Success", "positionDetails": [
    pos("SBIN_EQ", "NSE", "MIS", 99, "BUY", "800.00", "0", "805.45"),
    pos("USDINR25NOV26FUT", "CDS", "NRML", 1000, "BUY", "83.5", "0", "83.6", 0, 100),
]}}
NO_POSITIONS = {"body": {"status": "Success", "positionDetails": []}}
FAILED_POSITIONS = {"body": {"status": "Failure", "statusMessage": "no"}}

HOLDINGS = {"body": {"status": "Success", "holdingDetails": [
    {"tradingSymbol": "INFY_EQ", "exchange": "NSE", "holdingsQuantity": "5", "totalGainAndLoss": "502.5", "holdingsValue": "7502.5"},
    {"tradingSymbol": "TATAMOTORS_EQ", "exchange": "BSE", "holdingsQuantity": "2", "totalGainAndLoss": "-10", "holdingsValue": "1000"},
    {"tradingSymbol": "NOSUCH", "exchange": "NSE", "holdingsQuantity": None, "totalGainAndLoss": None, "holdingsValue": "0"},
], "holdingSummary": {"portfolioValue": "8502.5", "totalGainAndLossAmount": "492.5"}}}
HOLDINGS_NO_SUMMARY = {"body": {"status": "Success", "holdingDetails": [{"tradingSymbol": "INFY_EQ", "exchange": "NSE", "holdingsQuantity": "5", "totalGainAndLoss": "0", "holdingsValue": "100"}]}}
NO_HOLDINGS = {"body": {"status": "Success", "holdingDetails": []}}

LIMITS = {"body": {"status": "Success", "equityLimit": {"netAvailableMargin": "100000.5", "marginUsed": "1184.5", "collateralMarginAgainstShares": "2500.25"}, "commodityLimit": {"netAvailableMargin": "1"}}}
LIMITS_EMPTY = {"body": {"status": "Success"}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "S1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"status": "Success", "statusMessage": "ok", "orderNumber": "S1"}}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_with_disclosed_quantity", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}),
    place("placeorder_market_becomes_a_protected_limit", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: SBIN_Q}),
    place("placeorder_market_sell_is_protected_below", {**PLACE, "pricetype": "MARKET", "price": "0", "action": "SELL"}, extra={QB: SBIN_Q}),
    place("placeorder_market_future_uses_the_half_percent_slab", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={QB: quote(24005.5)}),
    place("placeorder_market_option_uses_the_option_slab", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={QB: quote(7.35)}),
    place("placeorder_market_mcx_asks_for_the_mfo_exchange", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={QB: quote(75000.0)}),
    place("placeorder_market_without_a_price_is_refused", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: quote(0)}),
    place("placeorder_market_when_the_quote_fails_is_refused", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: {"body": {"status": "Failure", "statusMessage": "no quote"}}}),
    place("placeorder_market_with_no_quote_details_is_refused", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: {"body": {"status": "Success", "quoteDetails": {}}}}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_a_protected_stop_limit", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_without_a_trigger_is_refused", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_index_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_broker_refuses", PLACE, ORDER_REFUSED),
    place("placeorder_refused_with_a_server_error", PLACE, SERVER_REFUSED),
    place("placeorder_success_without_an_order_number", PLACE, ORDER_NO_NUMBER),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {"PUT /order/modifyOrder/S1": MOD_OK}),
    req("modifyorder_lowercase_order_number_in_the_answer", "/api/v1/modifyorder", CHANGE, {"PUT /order/modifyOrder/S1": {"body": {"status": "Success", "ordernumber": "S2"}}}),
    req("modifyorder_answer_without_a_number_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {"PUT /order/modifyOrder/S1": {"body": {"status": "Success"}}}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {"PUT /order/modifyOrder/S1": MOD_OK}),
    req("modifyorder_to_market_becomes_a_protected_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {QB: SBIN_Q, "PUT /order/modifyOrder/S1": MOD_OK}),
    req("modifyorder_stop_market_becomes_a_protected_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {"PUT /order/modifyOrder/S1": MOD_OK}),
    req("modifyorder_market_with_a_failed_quote", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {QB: {"body": {"status": "Failure", "statusMessage": "no quote"}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {"PUT /order/modifyOrder/S1": {"status": 400, "body": {"status": "Failure", "statusMessage": "Order is not open"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {"PUT /order/modifyOrder/S1": {"status": 400, "body": {"status": "Failure"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S1"}, {"DELETE /order/cancelOrder": {"body": {"status": "Success"}}}),
    req("cancelorder_refused_with_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {"DELETE /order/cancelOrder": {"status": 400, "body": {"status": "Failure", "statusMessage": "Order already executed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {"DELETE /order/cancelOrder": {"status": 400, "body": {"status": "Failure"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, "DELETE /order/cancelOrder": [{"body": {"status": "Success"}}, {"status": 400, "body": {"status": "Failure"}}, {"body": {"status": "Success"}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_error", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDER_ERROR}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_status_spellings", "/api/v1/orderbook", {}, {OB: {"body": {"status": "Success", "orderBookDetails": [
        order("T1", "SBIN_EQ", "NSE", "BUY", "1", "800", "0", "L", "MIS", "Canceled"), order("T2", "SBIN_EQ", "NSE", "BUY", "1", "800", "0", "L", "MIS", "Completed"), order("T3", "SBIN_EQ", "NSE", "BUY", "1", "800", "0", "L", "MIS", "Filled"),
        order("T4", "SBIN_EQ", "NSE", "BUY", "1", "800", "0", "L", "MIS", "Ordered"), order("T5", "SBIN_EQ", "NSE", "SELL", "1", "800", "0", "L", "MIS", "Pending"), order("T6", "SBIN_EQ", "NSE", "SELL", "1", "800", "0", "L", "MIS", "EXECUTED"),
        order("T7", "SBIN_EQ", "NSE", "SELL", "1", "800", "0", "L", "MIS", "Trigger Pending")]}}}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_details_null", "/api/v1/orderbook", {}, {OB: NULL_ORDERS}),
    req("orderbook_refused", "/api/v1/orderbook", {}, {OB: ORDER_ERROR}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "Success", "tradeBookDetails": []}}}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: DAY}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED_POSITIONS}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_without_a_summary", "/api/v1/holdings", {}, {HB: HOLDINGS_NO_SUMMARY}),
    req("holdings_with_no_value", "/api/v1/holdings", {}, {HB: {"body": {"status": "Success", "holdingDetails": [{"tradingSymbol": "INFY_EQ", "exchange": "NSE", "holdingsQuantity": "5", "totalGainAndLoss": "-10", "holdingsValue": "0"}, {"tradingSymbol": "INFY_EQ", "exchange": "NSE", "holdingsQuantity": "5", "totalGainAndLoss": "100", "holdingsValue": "100"}]}}}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: {"body": {"status": "Failure", "statusMessage": "no"}}}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS}),
    req("funds_nothing_in_the_answer", "/api/v1/funds", {}, {LB: LIMITS_EMPTY}),
    req("funds_failed", "/api/v1/funds", {}, {LB: {"body": {"status": "Failure", "statusMessage": "Invalid session"}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: DAY}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: DAY}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: DAY}),
    req("closeposition_merges_the_day_and_net_books", "/api/v1/closeposition", {"strategy": "t"}, {PB: [DAY, NET], PLACE_EP: ORDER_OK, QB: [quote(805.45), quote(24005.5), quote(75010.0), quote(83.6)]}, ignore=IGNORE),
    req("closeposition_the_net_book_cannot_be_read", "/api/v1/closeposition", {"strategy": "t"}, {PB: [DAY, FAILED_POSITIONS]}),
    req("closeposition_the_day_book_cannot_be_read", "/api/v1/closeposition", {"strategy": "t"}, {PB: [FAILED_POSITIONS, NET]}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: [NO_POSITIONS, NO_POSITIONS]}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: DAY, PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: DAY}),
]


def patch():
    pass
