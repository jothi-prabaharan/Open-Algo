"""IIFL Capital scenarios: orders, books and funds on api.iiflcapital.com/v1. Bearer token; an order is a one-element list answered by a result list; a stop-market is sent as a stop-limit priced a slab beyond its trigger, snapped to the tick."""

AUTH_TOKEN = "iifl-user-session"
ENV = {"BROKER_API_KEY": "iifl-app-key", "BROKER_API_SECRET": "iifl-app-secret"}
SEED_API_KEY_ROW = True
IGNORE = []


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
B = "/orders"
PLACE_EP, OB, TB, PB, HB = "POST /v1/orders", "GET /v1/orders", "GET /v1/trades", "GET /v1/positions", "GET /v1/holdings"
LIM, LEQ, LFNO = "GET /v1/limits", "GET /v1/limits/equity", "GET /v1/limits/fno"
ORDER_OK = {"body": {"status": "Ok", "message": "Order placed", "result": [{"status": "Ok", "brokerOrderId": "I0001", "message": "ok"}]}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds"}}
ORDER_NO_ID = {"body": {"status": "Ok", "result": [{"status": "Ok"}]}}
ORDER_ROW_REJECTED = {"body": {"status": "Ok", "result": [{"status": "Rejected", "message": "Price out of range"}]}}
LIMITED = {"status": 429, "body": {"status": "error", "message": "Too many requests"}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def order(oid, symbol, exch, side, qty, price, trigger, otype, product, status, **extra):
    o = {"brokerOrderId": oid, "exchangeOrderId": "E" + oid, "tradingSymbol": symbol, "exchange": exch, "transactionType": side, "quantity": qty, "price": price, "slTriggerPrice": trigger,
         "orderType": otype, "product": product, "orderStatus": status, "exchangeTimestamp": "2026-11-03 10:20:01", "instrumentId": ""}
    o.update(extra)
    return o


ORDERS = {"body": [
    order("I1", "SBIN-EQ", "NSEEQ", "BUY", 10, 800.5, 0, "LIMIT", "INTRADAY", "OPEN", instrumentId="3045"),
    order("I2", "NIFTY25NOV26FUT", "NSEFO", "BUY", 75, 24010, 24000, "SL", "NORMAL", "TRIGGER_PENDING", instrumentId="49500"),
    order("I3", "INFY-EQ", "NSEEQ", "SELL", 5, 0, 0, "MARKET", "DELIVERY", "COMPLETE", instrumentId="1594"),
    order("I4", "SBIN-EQ", "NSEEQ", "SELL", 1, 0, 790.5, "SLM", "INTRADAY", "REJECTED", rejectionReason="Blocked", instrumentId="3045"),
    order("I5", "TATAMOTORS-EQ", "BSEEQ", "BUY", 2, 500, 0, "LIMIT", "BNPL", "CANCELLED", instrumentId="500570"),
    order("I6", "USDINR25NOV26FUT", "NSECURR", "BUY", 1000, 83.5, 0, "LIMIT", "NORMAL", "PARTIALLY_FILLED", instrumentId="90001"),
    order("I7", "GOLDM25NOV26FUT", "MCXCOMM", "SELL", 10, 75000, 0, "LIMIT", "NORMAL", "PUT ORDER REQ RECEIVED", instrumentId="80001"),
    order("I8", "X", "WEIRD", "buy", None, None, None, "XX", "ODD", "Something else", quantity=None, filledQuantity=3, pendingQuantity=2, cancelledQuantity=1, instrumentId=""),
    order("I9", "SBIN-EQ", "NSEEQ", "BUY", 4, 800, 0, "LIMIT", "INTRADAY", "NEW", instrumentId="3045", exchangeTimestamp=None, exchangeUpdateTime="2026-11-03 10:40:00"),
    {"status": "Ok"},
]}
ORDERS_WRAPPED = {"body": {"status": "Ok", "result": {"orders": ORDERS["body"][:2]}}}
NO_ORDERS = {"body": {"status": "Ok", "result": []}}
WRAPPER_ONLY = {"body": {"status": "Ok", "result": {"message": "no orders"}}}
ORDER_ERROR = {"status": 500, "body": {"status": "error", "message": "bad"}}
EXPIRED = {"status": 401, "body": {"status": "error", "message": "Invalid session"}}

TRADES = {"body": [
    {"brokerOrderId": "I3", "tradingSymbol": "INFY-EQ", "exchange": "NSEEQ", "transactionType": "SELL", "filledQuantity": 5, "tradedPrice": 1499.95, "product": "DELIVERY", "instrumentId": "1594", "fillTimestamp": "2026-11-03 10:21:12"},
    {"brokerOrderId": "I2", "tradingSymbol": "NIFTY25NOV26FUT", "exchange": "NSEFO", "transactionType": "BUY", "quantity": 75, "averageTradedPrice": 24005.5, "product": "NORMAL", "instrumentId": "49500", "exchangeTimestamp": "2026-11-03 10:22:00"},
    {"exchangeOrderId": "E9", "tradingSymbol": "NOSUCH", "exchange": "NSEEQ", "transactionType": "BUY", "filledQty": 2, "price": 2900, "product": "INTRADAY", "brokerUpdateTime": "2026-11-03 10:23:00"},
]}


def pos(symbol, exch, product, net, avg, ltp_, token="", **extra):
    p = {"tradingSymbol": symbol, "exchange": exch, "product": product, "netQuantity": net, "netAveragePrice": avg, "realizedPnl": 12.5, "instrumentId": token}
    if ltp_ is not None:
        p["ltp"] = ltp_
    p.update(extra)
    return p


POSITIONS = {"body": {"status": "Ok", "result": [
    pos("SBIN-EQ", "NSEEQ", "INTRADAY", 10, 800.0, 805.45, "3045"),
    pos("NIFTY25NOV26FUT", "NSEFO", "NORMAL", -75, 24000.0, 24005.5, "49500"),
    pos("INFY-EQ", "NSEEQ", "DELIVERY", 0, 0, 1505.0, "1594"),
    pos("GOLDM25NOV26FUT", "MCXCOMM", "NORMAL", 10, 75000.0, None, "80001", previousDayClose=74900.0),
    pos("USDINR25NOV26FUT", "NSECURR", "NORMAL", 1000, 83.5, None, "90001"),
]}}
NO_POSITIONS = {"body": {"status": "Ok", "result": []}}


def holding(nse, qty, avg, last, **extra):
    h = {"nseTradingSymbol": nse, "dpQuantity": qty, "averageTradedPrice": avg, "ltp": last, "product": "DELIVERY"}
    h.update(extra)
    return h


HOLDINGS = {"body": {"status": "Ok", "result": [
    holding("INFY-EQ", 5, 1400.0, 1500.5), holding("", 3, 500.25, 499.5, bseTradingSymbol="TATAMOTORS-EQ"), holding("SBIN-EQ", 0, 800, 805, totalQuantity=4),
    holding("ZERO-EQ", 0, 0, 0), holding("CASH-EQ", 0, 100, None, t1Quantity=2, previousDayClose=101.0),
]}}
NO_HOLDINGS = {"body": {"status": "Ok", "result": []}}

POOLED = {"body": {"status": "Ok", "result": {"tradingLimit": "100000.5", "openingCashLimit": 90000, "collateralMargin": 2500.25, "utilizedMargin": 1184.5, "creditForSell": 5, "adhocMargin": 1, "utilizedSpanMargin": 700, "utilizedExposureMargin": 300}}}
POOLED_ZERO = {"body": {"status": "Ok", "result": {"tradingLimit": 0, "openingCashLimit": 0}}}
SEG_EQ = {"body": {"status": "Ok", "result": {"tradingLimit": 50000, "openingCashLimit": 40000, "collateralMargin": 100, "utilizedMargin": 10}}}
SEG_FNO = {"body": {"status": "Ok", "result": [{"tradingLimit": "-", "openingCashLimit": 20000, "collateralMargin": 50, "utilizedMargin": 5}]}}
EMPTY_RESULT = {"body": {"status": "Ok", "result": {}}}


def place(name, request, reply=ORDER_OK, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply, **(extra or {})}, ignore=IGNORE)


CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "I1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
OK_ACTION = {"body": {"status": "Ok", "message": "done"}}
SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_market", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_cnc_with_disclosed_quantity", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}),
    place("placeorder_nrml_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sell_is_priced_below_the_trigger", {**PLACE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_buy_is_priced_above_the_trigger", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_option_uses_the_option_slab", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "SL-M", "price": "0", "trigger_price": "7.35"}),
    place("placeorder_stop_market_on_a_fine_tick_instrument", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "pricetype": "SL-M", "price": "0", "trigger_price": "83.5"}),
    place("placeorder_a_long_strategy_tag_is_cut", {**PLACE, "strategy": "a-strategy-name-that-is-much-longer-than-fifty-characters-in-total"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_broker_refuses", PLACE, ORDER_REFUSED),
    place("placeorder_success_without_an_order_id", PLACE, ORDER_NO_ID),
    place("placeorder_the_row_is_rejected", PLACE, ORDER_ROW_REJECTED),
    place("placeorder_a_rate_limited_write_is_sent_again", PLACE, [LIMITED, ORDER_OK]),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/I1": OK_ACTION}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {"PUT /v1/orders/I1": OK_ACTION}),
    req("modifyorder_stop_market_becomes_a_protected_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {"PUT /v1/orders/I1": OK_ACTION}),
    req("modifyorder_to_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {"PUT /v1/orders/I1": OK_ACTION}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/I1": {"status": 400, "body": {"status": "error", "message": "Order is not open"}}}),
    req("modifyorder_refused_with_an_ok_status", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/I1": {"body": {"status": "error", "message": "Rejected"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/I1": {"status": 400, "body": {"status": "error"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "I1"}, {"DELETE /v1/orders/I1": OK_ACTION}),
    req("cancelorder_refused_with_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "I2"}, {"DELETE /v1/orders/I2": {"status": 400, "body": {"status": "error", "message": "Order already executed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "I2"}, {"DELETE /v1/orders/I2": {"status": 400, "body": {"status": "error"}}}),
    req("cancelorder_an_id_that_is_not_safe_for_a_path", "/api/v1/cancelorder", {"strategy": "t", "orderid": "../x"}, {}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, **{f"DELETE /v1/orders/{i}": OK_ACTION for i in ("I1", "I2", "I6", "I7", "I9")}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_error", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDER_ERROR}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_wrapped_in_a_result", "/api/v1/orderbook", {}, {OB: ORDERS_WRAPPED}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_a_wrapper_is_not_a_row", "/api/v1/orderbook", {}, {OB: WRAPPER_ONLY}),
    req("orderbook_refused", "/api/v1/orderbook", {}, {OB: ORDER_ERROR}),
    req("orderbook_expired_session", "/api/v1/orderbook", {}, {OB: EXPIRED}),
    req("orderbook_a_rate_limited_read_is_tried_again", "/api/v1/orderbook", {}, {OB: [LIMITED, ORDERS]}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_ORDERS}),
    req("tradebook_expired_session", "/api/v1/tradebook", {}, {TB: EXPIRED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_expired_session", "/api/v1/positionbook", {}, {PB: EXPIRED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_expired_session", "/api/v1/holdings", {}, {HB: EXPIRED}),
    req("funds_pooled_limits", "/api/v1/funds", {}, {LIM: POOLED, LEQ: SEG_EQ, LFNO: SEG_FNO}),
    req("funds_pooled_zero_uses_the_segments_summed", "/api/v1/funds", {}, {LIM: POOLED_ZERO, LEQ: SEG_EQ, LFNO: SEG_FNO}),
    req("funds_only_one_segment_answers", "/api/v1/funds", {}, {LIM: EXPIRED, LEQ: SEG_EQ, LFNO: EXPIRED}),
    req("funds_pooled_without_segments", "/api/v1/funds", {}, {LIM: POOLED, LEQ: EXPIRED, LFNO: EXPIRED}),
    req("funds_pooled_is_all_zero_and_nothing_else", "/api/v1/funds", {}, {LIM: POOLED_ZERO, LEQ: EMPTY_RESULT, LFNO: EMPTY_RESULT}),
    req("funds_nothing_answers", "/api/v1/funds", {}, {LIM: EXPIRED, LEQ: EXPIRED, LFNO: EXPIRED}),
    req("funds_a_rate_limited_read_is_tried_again", "/api/v1/funds", {}, {LIM: [LIMITED, POOLED], LEQ: SEG_EQ, LFNO: SEG_FNO}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("closeposition_an_order_is_refused", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: ORDER_REFUSED}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
]


def patch():
    pass
