"""Firstock (api.firstock.in/V1): orders, books and funds. Every call is a POST with the session key and the user id in the body; the user id is the broker key without its last four characters. A market order and a stop-market are limit orders a slab beyond the last price."""

AUTH_TOKEN = "firstock-session-token"
ENV = {"BROKER_API_KEY": "AB1234_API", "BROKER_API_SECRET": "firstock-api-key"}
SEED_API_KEY_ROW = True
IGNORE = []
V = "/V1"


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1594, "INFOSYS"),
    _sym("M&M", "M&M-EQ", "NSE", 2031, "MAHINDRA"),
    _sym("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26F", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NOTICK", "NOTICK-EQ", "NSE", 4242, "NO TICK", 1, "EQ", "", -1.0, 0.0),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, MOD_EP, CAN_EP = f"POST {V}/placeOrder", f"POST {V}/modifyOrder", f"POST {V}/cancelOrder"
OB, TB, PB, HB, LB, LTP = f"POST {V}/orderBook", f"POST {V}/tradeBook", f"POST {V}/positionBook", f"POST {V}/holdings", f"POST {V}/limit", f"POST {V}/getLtp"
QB = f"POST {V}/getQuote"
READ_POSTS = [f"{V}/orderBook", f"{V}/tradeBook", f"{V}/positionBook", f"{V}/holdings", f"{V}/limit", f"{V}/getLtp", f"{V}/getQuote", f"{V}/getMultiQuotes", f"{V}/basketMargin", f"{V}/timePriceSeries"]


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def quote(ltp="805.45", **extra):
    d = {"lastTradedPrice": ltp, "dayOpenPrice": "801.00", "dayHighPrice": "809.50", "dayLowPrice": "799.00", "dayClosePrice": "800.00", "volume": "1234567", "openInterest": "0", "lastTradedQuantity": "25",
         "totalBuyQuantity": "50000", "totalSellQuantity": "61000"}
    for i in range(1, 6):
        d[f"bestBuyPrice{i}"] = f"{805.4 - 0.05 * (i - 1):.2f}"
        d[f"bestBuyQuantity{i}"] = str(100 * i)
        d[f"bestSellPrice{i}"] = f"{805.5 + 0.05 * (i - 1):.2f}"
        d[f"bestSellQuantity{i}"] = str(110 * i)
    d.update(extra)
    return {"body": {"status": "success", "data": d}}


SBIN_Q = quote()
OK_ORDER = {"body": {"status": "success", "data": {"requestTime": "10:20:01 03-11-2026", "orderNumber": "FS0001"}}}
REFUSED = {"status": 400, "body": {"status": "failed", "code": "400", "name": "INSUFFICIENT_FUNDS", "error": {"message": "Insufficient funds", "field": "quantity"}}}


def place(name, request, reply=None, extra=None):
    broker = {PLACE_EP: reply or OK_ORDER, **(extra or {})}
    return req(name, "/api/v1/placeorder", request, broker, ignore=IGNORE)


def order(number, token, exch, symbol, side, qty, price, trigger, ptype, product, status, **extra):
    o = {"orderNumber": number, "token": str(token), "exchange": exch, "tradingSymbol": symbol, "transactionType": side, "quantity": str(qty), "price": price, "triggerPrice": trigger, "priceType": ptype,
         "product": product, "status": status, "orderTime": "10:20:01 03-11-2026"}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "success", "data": [
    order("FS1", 3045, "NSE", "SBIN-EQ", "B", 10, "800.50", "0.00", "LMT", "I", "OPEN"),
    order("FS2", 49500, "NFO", "NIFTY25NOV26F", "B", 75, "24010.00", "24000.00", "SL-LMT", "M", "TRIGGER_PENDING"),
    order("FS3", 1594, "NSE", "INFY-EQ", "S", 5, "1495.00", "0.00", "LMT", "C", "COMPLETE"),
    order("FS4", 3045, "NSE", "SBIN-EQ", "S", 1, "0.00", "790.50", "SL-MKT", "I", "REJECTED"),
    order("FS5", 500570, "BSE", "TATAMOTORS-EQ", "B", 2, "0.00", "0.00", "MKT", "C", "CANCELED"),
    order("FS6", 90001, "CDS", "USDINR25NOV26FUT", "B", 1000, "83.50", "", "LIMIT", "M", "PENDING"),
    order("FS7", 80001, "MCX", "GOLDM25NOV26FUT", "S", 10, "75000", "0.00", "SL_LIMIT", "M", "TRIGGER PENDING"),
    order("FS8", 99999, "NSE", "NOSUCH-EQ", "B", 3, "5", "0.00", "XX", "Z", "Something Else"),
    order("FS9", 3045, "NSE", "SBIN-EQ", "B", 4, "800", "0.00", "SL-MARKET", "I", "Cancelled"),
]}}
CANCEL_BOOK = {"body": {"status": "success", "data": [
    order("FS1", 3045, "NSE", "SBIN-EQ", "B", 10, "800.50", "0.00", "LMT", "I", "OPEN"),
    order("FS2", 49500, "NFO", "NIFTY25NOV26F", "B", 75, "24010.00", "24000.00", "SL-LMT", "M", "TRIGGER_PENDING"),
    order("FS3", 1594, "NSE", "INFY-EQ", "S", 5, "1495.00", "0.00", "LMT", "C", "COMPLETE"),
]}}
NO_ORDERS = {"body": {"status": "success", "data": []}}
FAILED_BOOK = {"body": {"status": "failed", "error": {"message": "Session expired"}}}
TRADES = {"body": {"status": "success", "data": [
    {"orderNumber": "FS3", "token": "1594", "exchange": "NSE", "tradingSymbol": "INFY-EQ", "transactionType": "S", "product": "C", "fillQuantity": "5", "fillPrice": "1499.95", "fillTime": "10:21:12 03-11-2026"},
    {"orderNumber": "FS2", "token": "49500", "exchange": "NFO", "tradingSymbol": "NIFTY25NOV26F", "transactionType": "B", "product": "M", "fillQuantity": "75", "fillPrice": "24005.50", "fillTime": "10:22:00 03-11-2026"},
    {"orderNumber": "FS99", "token": "99999", "exchange": "NSE", "tradingSymbol": "NOSUCH-EQ", "transactionType": "B", "product": "I", "fillQuantity": "2", "fillPrice": "2900", "fillTime": "10:23:00 03-11-2026"},
]}}


def pos(token, exch, symbol, product, net, avg="0.00", realized="0.00", unrealized="0.00"):
    return {"token": str(token), "exchange": exch, "tradingSymbol": symbol, "product": product, "netQuantity": str(net), "netAveragePrice": avg, "dayBuyQuantity": "10", "daySellQuantity": "0", "dayBuyAmount": "8000.00",
            "dayBuyAveragePrice": "800.00", "daySellAmount": "0.00", "daySellAveragePrice": "0.00", "unrealizedMTOM": unrealized, "RealizedPNL": realized}


POSITIONS = {"body": {"status": "success", "data": [
    pos(3045, "NSE", "SBIN-EQ", "I", 10, "800.00", "12.50", "54.50"),
    pos(49500, "NFO", "NIFTY25NOV26F", "M", -75, "24000.00", "0.00", "-412.50"),
    pos(1594, "NSE", "INFY-EQ", "C", 0, "1500.00"),
    pos(80001, "MCX", "GOLDM25NOV26FUT", "M", 10, "75000.00", "0.00", "100.00"),
    pos(99999, "NSE", "NOSUCH-EQ", "I", 4),
]}}
FLAT = {"body": {"status": "success", "data": []}}
ONE_POSITION = {"body": {"status": "success", "data": pos(3045, "NSE", "SBIN-EQ", "I", 10, "800.00")}}
HOLDINGS = {"body": {"status": "success", "data": [
    {"exchangeTradingSymbol": [{"exchange": "NSE", "token": "1594", "tradingSymbol": "INFY-EQ", "pricePrecision": "2", "tickSize": "0.05", "lotSize": "1"}, {"exchange": "BSE", "token": "500209", "tradingSymbol": "INFY", "pricePrecision": "2", "tickSize": "0.05", "lotSize": "1"}]},
]}}
SIMPLE_HOLDINGS = {"body": {"status": "success", "data": [
    {"exchange": "NSE", "token": "1594", "tradingSymbol": "INFY-EQ", "pricePrecision": "2", "tickSize": "0.05", "lotSize": "1"},
    {"exchange": "BSE", "token": "500570", "tradingSymbol": "TATAMOTORS-EQ", "pricePrecision": "3", "tickSize": "0.01", "lotSize": "2"},
    {"exchange": "NSE", "token": "", "tradingSymbol": "NOSUCH-EQ"},
]}}
LIMITS = {"body": {"status": "success", "data": {"cash": "100000.50", "payin": "500.25", "marginused": "1184.50", "brkcollamt": "2500.25"}}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "FS1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"status": "success", "data": {"requestTime": "10:25:00 03-11-2026", "orderNumber": "FS1"}}}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_delivery", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_product", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_becomes_a_protected_limit", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: SBIN_Q}),
    place("placeorder_market_sell_is_protected_below", {**PLACE, "pricetype": "MARKET", "price": "0", "action": "SELL"}, extra={QB: SBIN_Q}),
    place("placeorder_market_option", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={QB: quote("7.35")}),
    place("placeorder_market_without_a_price_goes_as_it_is", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: quote("0")}),
    place("placeorder_market_when_the_quote_fails_goes_as_it_is", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={QB: {"body": {"status": "failed", "error": {"message": "no quote"}}}}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_a_protected_stop_limit", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, extra={QB: SBIN_Q}),
    place("placeorder_stop_market_when_the_quote_fails_goes_as_it_is", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, extra={QB: {"body": {"status": "failed", "error": {"message": "no quote"}}}}),
    place("placeorder_the_ampersand_in_a_name_is_escaped", {**PLACE, "symbol": "M&M"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_refused", PLACE, REFUSED),
    place("placeorder_refused_without_a_message", PLACE, {"status": 400, "body": {"status": "failed"}}),
    place("placeorder_success_without_an_order_number", PLACE, {"body": {"status": "success", "data": {}}}),
    place("placeorder_not_json", PLACE, {"status": 502, "body": "Bad gateway"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_to_market_becomes_a_protected_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {QB: SBIN_Q, MOD_EP: MOD_OK}),
    req("modifyorder_stop_market_becomes_a_protected_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {QB: SBIN_Q, MOD_EP: MOD_OK}),
    req("modifyorder_market_with_a_failed_quote_asks_the_broker_to_protect_it", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {QB: {"body": {"status": "failed", "error": {"message": "no quote"}}}, MOD_EP: MOD_OK}),
    req("modifyorder_product_change", "/api/v1/modifyorder", {**CHANGE, "product": "CNC"}, {MOD_EP: MOD_OK}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "failed", "code": "400", "name": "NOT_OPEN", "error": {"message": "Order is not open"}}}}),
    req("modifyorder_refused_with_a_top_level_message", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "failed", "message": "Bad modify"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "failed"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "FS1"}, {CAN_EP: {"body": {"status": "success", "data": {"requestTime": "10:26:00 03-11-2026", "orderNumber": "FS1"}}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "FS3"}, {CAN_EP: {"status": 404, "body": {"status": "failed", "code": "404", "name": "ORDER_NOT_FOUND", "error": {"message": "Order not found to cancel", "field": "orderNumber"}}}}),
    req("cancelorder_refused_without_a_code", "/api/v1/cancelorder", {"strategy": "t", "orderid": "FS3"}, {CAN_EP: {"status": 400, "body": {"status": "failed", "error": {"message": "Nope"}}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CAN_EP: [{"body": {"status": "success", "data": {}}}, {"status": 400, "body": {"status": "failed", "code": "400", "error": {"message": "No"}}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED_BOOK}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED_BOOK}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_ORDERS}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: FAILED_BOOK}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_one_position_as_an_object", "/api/v1/positionbook", {}, {PB: ONE_POSITION}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED_BOOK}),
    req("holdings_with_a_list_of_exchanges", "/api/v1/holdings", {}, {HB: HOLDINGS, LTP: [{"body": {"status": "success", "data": {"ltp": "1510.5"}}}]}),
    req("holdings_simple_rows", "/api/v1/holdings", {}, {HB: SIMPLE_HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_ORDERS}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED_BOOK}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS}),
    req("funds_text_without_the_extras", "/api/v1/funds", {}, {LB: {"body": {"status": "success", "data": {"cash": "10"}}}}),
    req("funds_failed", "/api/v1/funds", {}, {LB: {"body": {"status": "failed", "error": {"message": "Session expired"}}}}),
    req("funds_http_error", "/api/v1/funds", {}, {LB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER, QB: [quote("805.45"), quote("24005.5"), quote("75010.0")]}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED_BOOK}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
]


def patch():
    pass
