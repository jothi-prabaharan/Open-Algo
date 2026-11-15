"""Definedge Securities (integrate.definedgesecurities.com/dart/v1): orders, books and funds. The session key is the Authorization header. Every call is paced a tenth of a second apart and a 429 is retried with a growing wait (except for an order under the gthread worker)."""

AUTH_TOKEN = "dg-session-key:::dg-susertoken:::dg-api-token"
ENV = {"BROKER_API_KEY": "dg-api-token", "BROKER_API_SECRET": "dg-secret"}
SEED_API_KEY_ROW = True
IGNORE = []
V = "/dart/v1"
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
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, MOD_EP = f"POST {V}/placeorder", f"POST {V}/modify"
OB, TB, PB, HB, LB = f"GET {V}/orders", f"GET {V}/trades", f"GET {V}/positions", f"GET {V}/holdings", f"GET {V}/limits"


def cancel_ep(order_id):
    return f"GET {V}/cancel/{order_id}"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"status": "SUCCESS", "order_id": "DG0001", "request_time": "10:20:01 03-11-2026"}}
REFUSED = {"status": 400, "body": {"status": "ERROR", "message": "Insufficient funds"}}


def place(name, request, reply=None, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply or OK_ORDER, **(extra or {})}, ignore=IGNORE)


def order(number, symbol, exch, side, qty, price, ptype, product, status, **extra):
    o = {"order_id": number, "tradingsymbol": symbol, "exchange": exch, "order_type": side, "quantity": str(qty), "price": price, "price_type": ptype, "product_type": product, "order_status": status,
         "order_entry_time": "10:20:01 03-11-2026", "trigger_price": "0.00"}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "SUCCESS", "orders": [
    order("DG1", "SBIN-EQ", "NSE", "BUY", 10, "800.50", "LIMIT", "INTRADAY", "OPEN"),
    order("DG2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, "24010.00", "SL-LIMIT", "NORMAL", "OPEN", trigger_price="24000.00"),
    order("DG3", "INFY-EQ", "NSE", "SELL", 5, "1495.00", "LIMIT", "CNC", "COMPLETE", exchange_time="10:21:12 03-11-2026"),
    order("DG4", "SBIN-EQ", "NSE", "SELL", 1, "0.00", "SL-MARKET", "INTRADAY", "REJECTED", trigger_price="790.50"),
    order("DG5", "TATAMOTORS-EQ", "BSE", "BUY", 2, "0.00", "MARKET", "NORMAL", "CANCELED"),
    order("DG6", "USDINR25NOV26FUT", "CDS", "BUY", 1000, "83.50", "LIMIT", "NORMAL", "NEW"),
    order("DG7", "GOLDM25NOV26FUT", "MCX", "SELL", 10, "75000", "LIMIT", "NORMAL", "REPLACED"),
    order("DG8", "NOSUCH-EQ", "NSE", "BUY", 3, "5", "XX", "ZZ", "Something Else"),
    order("DG9", "SBIN-EQ", "NSE", "BUY", 4, "800", "LIMIT", "INTRADAY", "TRIGGER_PENDING"),
]}}
CANCEL_BOOK = {"body": {"status": "SUCCESS", "orders": [
    order("DG1", "SBIN-EQ", "NSE", "BUY", 10, "800.50", "LIMIT", "INTRADAY", "OPEN"),
    order("DG2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, "24010.00", "SL-LIMIT", "NORMAL", "TRIGGER PENDING"),
    order("DG3", "INFY-EQ", "NSE", "SELL", 5, "1495.00", "LIMIT", "CNC", "COMPLETE"),
]}}
EMPTY = {"body": {"status": "SUCCESS", "orders": []}}
FAILED = {"body": {"status": "ERROR", "message": "Session expired"}}
TRADES = {"body": {"status": "SUCCESS", "trades": [
    {"order_id": "DG3", "tradingsymbol": "INFY-EQ", "exchange": "NSE", "order_type": "SELL", "product_type": "CNC", "filled_qty": "5", "fill_price": "1499.95", "fill_time": "10:21:12 03-11-2026"},
    {"order_id": "DG2", "tradingsymbol": "NIFTY25NOV26FUT", "exchange": "NFO", "order_type": "BUY", "product_type": "NORMAL", "quantity": "75", "average_traded_price": "24005.50", "exchange_time": "10:22:00 03-11-2026"},
    {"order_id": "DG99", "tradingsymbol": "NOSUCH-EQ", "exchange": "NSE", "order_type": "BUY", "product_type": "INTRADAY", "filled_qty": "2", "fill_price": "2900", "fill_time": "10:23:00 03-11-2026"},
]}}


def pos(symbol, exch, product, net, avg="0.00", realized="0.00", unrealized="0.00", ltp="0.00"):
    return {"tradingsymbol": symbol, "exchange": exch, "product_type": product, "net_quantity": str(net), "net_averageprice": avg, "realized_pnl": realized, "unrealized_pnl": unrealized, "lastPrice": ltp}


POSITIONS = {"body": {"status": "SUCCESS", "positions": [
    pos("SBIN-EQ", "NSE", "INTRADAY", 10, "800.00", "12.50", "54.50", "805.45"),
    pos("NIFTY25NOV26FUT", "NFO", "NORMAL", -75, "24000.00", "0.00", "-412.50", "24005.5"),
    pos("INFY-EQ", "NSE", "CNC", 0, "1500.00", "100.00", "0.00", "1510"),
    pos("GOLDM25NOV26FUT", "MCX", "NORMAL", 10, "75000.00", "0.00", "100.00", "75010"),
    pos("NOSUCH-EQ", "NSE", "INTRADAY", 4),
]}}
FLAT = {"body": {"status": "SUCCESS", "positions": []}}
HOLDINGS = {"body": {"status": "SUCCESS", "data": [
    {"tradingsymbol": [{"exchange": "NSE", "tradingsymbol": "INFY-EQ", "isin": "INE009A01021"}, {"exchange": "BSE", "tradingsymbol": "INFY", "isin": "INE009A01021"}], "dp_qty": "6", "t1_qty": "4", "avg_buy_price": "1450.50"},
    {"tradingsymbol": [{"exchange": "BSE", "tradingsymbol": "TATAMOTORS-EQ", "isin": "INE155A01022"}], "dp_qty": "0", "t1_qty": "4", "avg_buy_price": "500"},
    {"tradingsymbol": [{"exchange": "NSE", "tradingsymbol": "SBIN-EQ", "isin": "INE062A01020"}], "dp_qty": "0", "t1_qty": "0", "avg_buy_price": "800"},
    {"tradingsymbol": [{"exchange": "NSE", "tradingsymbol": "NOSUCH-EQ"}], "dp_qty": "2", "t1_qty": "0", "avg_buy_price": "10"},
]}}
LIMITS = {"body": {"status": "SUCCESS", "cash": "100000.50", "brokerCollateralAmount": "2500.25", "currentUnrealizedMtom": "-120.5", "currentRealizedPNL": "340.25", "marginUsed": "1184.50"}}
LIMITS_SPLIT = {"body": {"status": "SUCCESS", "cash": "10", "currentUnrealizedMtom": "0", "currentUnrealizedMTOMDerivativeIntraday": "1.5", "currentUnrealizedMTOMDerivativeMargin": "2", "currentUnrealizedMTOMEquityIntraday": "3",
                         "currentUnrealizedMTOMEquityMargin": "4", "currentUnrealizedMTOMCommodityIntraday": "5", "currentUnrealizedMTOMCommodityMargin": "6", "currentRealizedPNL": "0", "currentRealizedPNLEquityIntraday": "1",
                         "currentRealizedPNLDerivativeIntraday": "2", "currentRealizedPNLDerivativeMargin": "3", "currentRealizedPNLEquityMargin": "4", "currentRealizedPNLCommodityIntraday": "5", "currentRealizedPNLCommodityMargin": "6"}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "DG1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"status": "SUCCESS", "order_id": "DG1"}}

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
    place("placeorder_reply_in_the_other_shape", PLACE, {"body": {"stat": "Ok", "norenordno": "NR0001"}}),
    place("placeorder_refused", PLACE, REFUSED),
    place("placeorder_refused_with_an_emsg", PLACE, {"status": 400, "body": {"stat": "Not_Ok", "emsg": "Bad quantity"}}),
    place("placeorder_refused_without_a_message", PLACE, {"status": 400, "body": {"status": "ERROR"}}),
    place("placeorder_refused_with_status_200", PLACE, {"body": {"status": "ERROR", "message": "Rejected by RMS"}}),
    place("placeorder_success_without_an_order_number", PLACE, {"body": {"status": "SUCCESS"}}),
    place("placeorder_not_json", PLACE, {"status": 502, "body": "Bad gateway"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {MOD_EP: MOD_OK}),
    req("modifyorder_product_change", "/api/v1/modifyorder", {**CHANGE, "product": "CNC"}, {MOD_EP: MOD_OK}),
    req("modifyorder_disclosed", "/api/v1/modifyorder", {**CHANGE, "disclosed_quantity": "3"}, {MOD_EP: MOD_OK}),
    req("modifyorder_other_reply_shape", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"body": {"stat": "Ok", "norenordno": "NR9"}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "ERROR", "message": "Order is not open"}}}),
    req("modifyorder_refused_with_an_emsg", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"stat": "Not_Ok", "emsg": "Nope"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"status": "ERROR"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "DG1"}, {cancel_ep("DG1"): {"body": {"status": "SUCCESS", "order_id": "DG1", "request_time": "10:26:00 03-11-2026"}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "DG3"}, {cancel_ep("DG3"): {"status": 404, "body": {"status": "ERROR", "message": "Order not found to cancel"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "DG3"}, {cancel_ep("DG3"): {"status": 400, "body": {"status": "ERROR"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, cancel_ep("DG1"): {"body": {"status": "SUCCESS", "order_id": "DG1"}}, cancel_ep("DG2"): {"status": 400, "body": {"status": "ERROR", "message": "No"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "SUCCESS", "trades": []}}}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: FAILED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: {"body": {"status": "SUCCESS", "data": []}}}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS}),
    req("funds_pnl_from_the_split_fields", "/api/v1/funds", {}, {LB: LIMITS_SPLIT}),
    req("funds_text_that_is_not_a_number", "/api/v1/funds", {}, {LB: {"body": {"status": "SUCCESS", "cash": "n/a", "marginUsed": None}}}),
    req("funds_failed", "/api/v1/funds", {}, {LB: {"body": {"status": "ERROR", "message": "Session expired"}}}),
    req("funds_http_error", "/api/v1/funds", {}, {LB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
]


def patch():
    import importlib

    limiter = importlib.import_module("broker.definedge.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
