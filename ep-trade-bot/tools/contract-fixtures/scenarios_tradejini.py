"""Tradejini (api.tradejini.com/v2): orders, books, funds. Every call carries Bearer <api key>:<access token>; writes are form-encoded and every answer is an envelope {s: ok | no-data | error, d, msg}. An order book row carries its instrument as a sym object (id, exchange, symbol, tradSymbol)."""

AUTH_TOKEN = "tj-access-token"
ENV = {"BROKER_API_KEY": "tj-app-api-key"}
SEED_API_KEY_ROW = True
READ_POSTS = []
V = "/v2/api/oms"


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05, brexchange=None):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "EQT_SBIN_EQ_NSE", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "EQT_INFY_EQ_NSE", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "EQT_TATAMOTORS_EQ_BSE", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "FUT_NIFTY_25NOV26_NFO", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("SENSEX26NOVFUT", "FUT_SENSEX_26NOV26_BFO", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "FUT_GOLDM_25NOV26_MCX", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "FUT_USDINR_25NOV26_CDS", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NIFTY", "IDX_NIFTY_NSE", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX", brexchange="NSE"),
    _sym("SENSEX", "IDX_SENSEX_BSE", "BSE_INDEX", 1, "SENSEX", 1, "INDEX", brexchange="BSE"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, CANCEL_EP = f"POST {V}/place-order", f"DELETE {V}/cancel-order"
MODIFY_EP = f"PUT {V}/modify-order"
OB, TB, PB, HB, FB = f"GET {V}/orders", f"GET {V}/trades", f"GET {V}/positions", f"GET {V}/holdings", f"GET {V}/limits"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"s": "ok", "d": {"orderId": "TJ0001", "msg": "Order placed"}}}
REFUSED = {"body": {"s": "error", "msg": "RMS: insufficient funds"}}
NOT_JSON = {"status": 200, "body": "<html>"}


def place(name, request, reply=None, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply or OK_ORDER, **(extra or {})})


def sym(brsymbol, exchange, symbol, trad, **extra):
    s = {"id": brsymbol, "exchange": exchange, "symbol": symbol, "tradSymbol": trad}
    s.update(extra)
    return s


SBIN = sym("EQT_SBIN_EQ_NSE", "NSE", "SBIN", "SBIN-EQ")
INFY = sym("EQT_INFY_EQ_NSE", "NSE", "INFY", "INFY-EQ")
NIFTYF = sym("FUT_NIFTY_25NOV26_NFO", "NFO", "NIFTY", "NIFTY25NOV26FUT")


def order(number, s, side, qty, otype, product, status, **extra):
    o = {"orderId": number, "sym": s, "symId": s["id"], "qty": qty, "side": side, "type": otype, "product": product, "orderTime": "2026-11-03 10:20:01", "status": status, "avgPrice": 0, "limitPrice": 0, "trigPrice": 0,
         "fillQty": 0, "pendingQty": qty, "validity": "day", "validTill": "", "reason": ""}
    o.update(extra)
    return o


ORDERS = {"body": {"s": "ok", "d": [
    order("TJ1", SBIN, "buy", 10, "limit", "intraday", "Open", limitPrice=800.5),
    order("TJ2", NIFTYF, "buy", 75, "stoplimit", "normal", "Trigger Pending", limitPrice=24010, trigPrice=24000),
    order("TJ3", INFY, "sell", 5, "limit", "delivery", "Completed", limitPrice=1495, avgPrice=1499.95, fillQty=5, pendingQty=0),
    order("TJ4", SBIN, "sell", 1, "stopmarket", "intraday", "Rejected", trigPrice=790.5, reason="RMS reject"),
    order("TJ5", sym("EQT_TATAMOTORS_EQ_BSE", "BSE", "TATAMOTORS", "TATAMOTORS"), "buy", 2, "market", "delivery", "Cancelled"),
    order("TJ6", sym("FUT_GOLDM_25NOV26_MCX", "MCX", "GOLDM", "GOLDM25NOV26FUT"), "sell", 10, "market", "cover", "Pending"),
    order("TJ7", sym("EQT_NOSUCH_EQ_NSE", "NSE", "NOSUCH", "NOSUCH-EQ"), "buy", 3, "xx", "zz", "Traded"),
    order("TJ8", SBIN, "buy", 4, "limit", "bracket", "Canceled"),
    order("TJ9", SBIN, "buy", 4, "limit", "intraday", "Filled"),
    order("TJ10", SBIN, "buy", 4, "limit", "intraday", ""),
    {"orderId": "TJ11", "qty": 1, "side": "buy", "type": "limit", "product": "intraday", "status": "Open", "symId": "EQT_INFY_EQ_NSE"},
]}}
CANCEL_BOOK = {"body": {"s": "ok", "d": [
    order("TJ1", SBIN, "buy", 10, "limit", "intraday", "Open", limitPrice=800.5),
    order("TJ2", NIFTYF, "buy", 75, "stoplimit", "normal", "Trigger Pending"),
    order("TJ3", INFY, "sell", 5, "limit", "delivery", "Completed"),
    order("TJ4", SBIN, "sell", 1, "market", "intraday", "Pending"),
    order("TJ5", SBIN, "sell", 1, "market", "intraday", "Modified"),
]}}
NO_DATA = {"body": {"s": "no-data", "msg": "No Data Available"}}
FAILED = {"body": {"s": "error", "msg": "Session expired"}}


def trade(order_id, s, side, qty, price, value, **extra):
    t = {"orderId": order_id, "sym": s, "symId": s["id"], "side": side, "product": "intraday", "fillQty": qty, "fillPrice": price, "fillValue": value, "time": "2026-11-03 10:21:12", "exchOrderId": "X1"}
    t.update(extra)
    return t


TRADES = {"body": {"s": "ok", "d": [
    trade("TJ3", INFY, "sell", 5, 1499.95, 7499.75, product="delivery"),
    trade("TJ2", NIFTYF, "buy", 75, 24005.5, 1800412.5, product="normal", remarks="algo"),
    trade("TJ99", sym("EQT_NOSUCH_EQ_NSE", "NSE", "NOSUCH", "NOSUCH-EQ"), "buy", 2, 2900, 5800, product="zz"),
    {"orderId": "TJ97", "side": "sell", "fillQty": 1, "fillPrice": 5, "fillValue": 5, "symId": "EQT_SBIN_EQ_NSE", "sym": {}},
    trade("TJ96", SBIN, "buy", 1, 800, 800, product="margin"),
]}}


def pos(s, product, net, avg, **extra):
    p = {"sym": s, "symId": s["id"], "product": product, "netQty": net, "netAvgPrice": avg}
    p.update(extra)
    return p


POSITIONS = {"body": {"s": "ok", "d": [
    pos(SBIN, "intraday", 10, 800.0),
    pos(NIFTYF, "normal", -75, 24000),
    pos(INFY, "delivery", 0, 1500),
    pos(sym("FUT_GOLDM_25NOV26_MCX", "MCX", "GOLDM", "GOLDM25NOV26FUT"), "normal", 10, 75000),
    pos(sym("EQT_NOSUCH_EQ_NSE", "NSE", "NOSUCH", "NOSUCH-EQ"), "intraday", 4, 10),
    pos(sym("EQT_TATAMOTORS_EQ_BSE", "BSE", "TATAMOTORS", "TATAMOTORS"), "cover", 3, 500),
]}}
POSITIONS_ODD = {"body": {"s": "ok", "d": [
    pos(SBIN, "intraday", 10, "n/a"),
    pos(INFY, "intraday", 2, 1500),
]}}
ODD_ORDERS = {"body": {"s": "ok", "d": [order("TJ1", SBIN, "buy", 10, "limit", "intraday", "Open", limitPrice=800.5), order("TJ8", SBIN, "buy", 4, "limit", "intraday", "Open", limitPrice="bad")]}}
ODD_TRADES = {"body": {"s": "ok", "d": [trade("TJ3", INFY, "sell", 5, 1499.95, 7499.75), trade("TJ98", SBIN, "BUY", "x", "y", "z")]}}
FLAT = {"body": {"s": "no-data", "msg": "No Data Available"}}


def holding(s, qty, avg, **extra):
    h = {"sym": s, "symId": s["id"], "qty": qty, "avgPrice": avg, "realizedPnl": 0, "product": "delivery"}
    h.update(extra)
    return h


HOLDINGS = {"body": {"s": "ok", "d": {"holdings": [
    holding(sym(INFY["id"], "NSE", "INFY", "INFY-EQ", lastPrice=1510), 6, 1450.5, realizedPnl=12.5),
    holding(sym("EQT_TATAMOTORS_EQ_BSE", "BSE", "TATAMOTORS", "TATAMOTORS", ltp=510), 4, 500),
    holding(SBIN, 3, 800, product="intraday"),
    holding(sym("EQT_NOSUCH_EQ_NSE", "NSE", "NOSUCH", "NOSUCH-EQ"), "x", "y"),
    {"sym": {}, "qty": 1, "avgPrice": 5},
]}}}
LIMITS = {"body": {"s": "ok", "d": {"availMargin": 100000.5, "stockCollateral": 2500.25, "unrealizedPnL": -120.5, "realizedPnl": 340.25, "marginUsed": 1184.5}}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "TJ1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"s": "ok", "d": {"orderId": "TJ1"}}}


def cancel(name, order_id, reply, **extra):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": order_id}, {CANCEL_EP: reply}, **extra)


def modify(name, request, reply):
    return req(name, "/api/v1/modifyorder", request, {MODIFY_EP: reply})


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_delivery", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_product", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_sends_the_protection", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sends_the_protection", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_disclosed_quantity", {**PLACE, "disclosed_quantity": "5"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_reply_without_a_message", PLACE, {"body": {"s": "ok", "d": {"orderId": "TJ2"}}}),
    place("placeorder_numeric_order_id", PLACE, {"body": {"s": "ok", "d": {"orderId": 12345, "msg": "done"}}}),
    place("placeorder_refused_with_status_200", PLACE, REFUSED),
    place("placeorder_refused_with_status_400", PLACE, {"status": 400, "body": {"s": "error", "msg": "Invalid quantity"}}),
    place("placeorder_refused_with_status_401", PLACE, {"status": 401, "body": {"s": "error", "msg": "Unauthorized"}}),
    place("placeorder_refused_with_a_message_in_d", PLACE, {"body": {"s": "error", "d": {"msg": "Inner reason"}}}),
    place("placeorder_refused_without_a_message", PLACE, {"body": {"s": "error"}}),
    place("placeorder_refused_status_500_not_json", PLACE, {"status": 500, "body": "oops"}),
    place("placeorder_success_without_an_order_id", PLACE, {"body": {"s": "ok", "d": {}}}),
    place("placeorder_success_with_an_empty_order_id", PLACE, {"body": {"s": "ok", "d": {"orderId": ""}}}),
    place("placeorder_ok_without_d", PLACE, {"body": {"s": "ok"}}),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    modify("modifyorder_limit", CHANGE, {"body": {"s": "ok", "d": {"orderId": "TJ1"}}}) | {"broker": {MODIFY_EP: MOD_OK}},
    modify("modifyorder_stop_limit", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, MOD_OK),
    modify("modifyorder_stop_market", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, MOD_OK),
    modify("modifyorder_market", {**CHANGE, "pricetype": "MARKET", "price": "0"}, MOD_OK),
    modify("modifyorder_disclosed", {**CHANGE, "disclosed_quantity": "3"}, MOD_OK),
    modify("modifyorder_reply_without_an_order_id", CHANGE, {"body": {"s": "ok", "d": {}}}),
    modify("modifyorder_reply_without_d", CHANGE, {"body": {"s": "ok"}}),
    modify("modifyorder_refused_with_status_200", CHANGE, {"body": {"s": "error", "msg": "Order is not open"}}),
    modify("modifyorder_refused_with_status_400", CHANGE, {"status": 400, "body": {"s": "error", "msg": "Bad price"}}),
    modify("modifyorder_refused_without_a_message", CHANGE, {"body": {"s": "error"}}),
    modify("modifyorder_not_json", CHANGE, NOT_JSON),
    req("modifyorder_unknown_symbol", "/api/v1/modifyorder", {**CHANGE, "symbol": "NOPE"}),
    cancel("cancelorder", "TJ1", {"body": {"s": "ok", "d": {"orderId": "TJ1"}}}),
    cancel("cancelorder_reply_without_d", "TJ1", {"body": {"s": "ok"}}),
    cancel("cancelorder_refused_with_status_200", "TJ3", {"body": {"s": "error", "msg": "Order already complete"}}),
    cancel("cancelorder_refused_with_status_404", "TJ3", {"status": 404, "body": {"s": "error", "msg": "No such order"}}),
    cancel("cancelorder_refused_without_a_message", "TJ3", {"body": {"s": "error"}}),
    cancel("cancelorder_not_json", "TJ3", NOT_JSON),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCEL_EP: {"body": {"s": "ok", "d": {"orderId": "X"}}}}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCEL_EP: [{"body": {"s": "ok", "d": {"orderId": "X"}}}, {"body": {"s": "error", "msg": "Already done"}}, {"body": {"s": "ok"}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_DATA}),
    req("cancelallorder_nothing_pending", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"body": {"s": "ok", "d": [order("TJ3", INFY, "sell", 5, "limit", "delivery", "Completed")]}}}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_row_that_cannot_be_read", "/api/v1/orderbook", {}, {OB: ODD_ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_DATA}),
    req("orderbook_ok_without_d", "/api/v1/orderbook", {}, {OB: {"body": {"s": "ok"}}}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: NOT_JSON}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": {"s": "error", "msg": "down"}}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_row_that_cannot_be_read", "/api/v1/tradebook", {}, {TB: ODD_TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_DATA}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: FAILED}),
    req("tradebook_not_json", "/api/v1/tradebook", {}, {TB: NOT_JSON}),
    req("tradebook_http_error", "/api/v1/tradebook", {}, {TB: {"status": 500, "body": {"s": "error"}}}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_row_that_cannot_be_read", "/api/v1/positionbook", {}, {PB: POSITIONS_ODD}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED}),
    req("positionbook_not_json", "/api/v1/positionbook", {}, {PB: NOT_JSON}),
    req("positionbook_http_error", "/api/v1/positionbook", {}, {PB: {"status": 500, "body": {"s": "error"}}}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_DATA}),
    req("holdings_no_holdings_text", "/api/v1/holdings", {}, {HB: {"body": {"s": "ok", "d": "No Holdings"}}}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED}),
    req("holdings_http_error", "/api/v1/holdings", {}, {HB: {"status": 500, "body": {"s": "error"}}}),
    req("funds", "/api/v1/funds", {}, {FB: LIMITS}),
    req("funds_one_row_a_segment_are_summed", "/api/v1/funds", {}, {FB: {"body": {"s": "ok", "d": [
        {"segment": "EQ", "availMargin": 100, "stockCollateral": 10, "unrealizedPnL": 1.5, "realizedPnl": 2, "marginUsed": 3},
        {"segment": "FO", "availMargin": 50.25, "stockCollateral": 5, "unrealizedPnL": -0.5, "realizedPnl": 0, "marginUsed": 7, "flag": True},
        "junk"]}}}),
    req("funds_empty_list", "/api/v1/funds", {}, {FB: {"body": {"s": "ok", "d": []}}}),
    req("funds_missing_fields", "/api/v1/funds", {}, {FB: {"body": {"s": "ok", "d": {"availMargin": 5}}}}),
    req("funds_text_values", "/api/v1/funds", {}, {FB: {"body": {"s": "ok", "d": {"availMargin": "12.5", "stockCollateral": "x"}}}}),
    req("funds_d_that_is_text", "/api/v1/funds", {}, {FB: {"body": {"s": "ok", "d": "nothing"}}}),
    req("funds_failed", "/api/v1/funds", {}, {FB: FAILED}),
    req("funds_http_error", "/api/v1/funds", {}, {FB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: FAILED}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's."""
    import importlib

    importlib.import_module("broker.tradejini.api.order_api")._POSITION_BOOK.invalidate("tj-access-token")
