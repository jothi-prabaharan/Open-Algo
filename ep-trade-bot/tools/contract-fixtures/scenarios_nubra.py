"""Nubra (api.nubra.io, V3): orders, books, funds. The session token is a bearer token and every call carries x-device-id OPENALGO. Prices are in paise. The order book is one read in buckets (open, executed, cancelled, rejected, expired, gtt) and the trade book is the orders that
filled; positions and holdings sit under portfolio. A stop order is a market or limit order with an LTP entry trigger. HTTP 440 means the session expired. Product is IDAY or CNC (NRML is CNC)."""

AUTH_TOKEN = "nubra-session-token"
ENV = {"BROKER_API_KEY": "9876543210", "BROKER_API_SECRET": "1234"}
SEED_API_KEY_ROW = True
READ_POSTS = []

CREATE, MODIFY, CANCEL = "POST /sentinel/orders/create", "POST /sentinel/orders/modify", "POST /sentinel/orders/cancel"
OB, PB, HB, FB = "GET /sentinel/orders", "GET /sentinel/portfolio/positions", "GET /sentinel/portfolio/holdings", "GET /sentinel/portfolio/user_funds_and_margin"


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS", "BSE", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", "NSE", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY26NOV24000CE", "NFO", "NSE", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", "BSE", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "NIFTY", 1, "INDEX"),
]

PLACE = {"strategy": "My Strategy_v2", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"orders": [{"intentOrderId": 7001001}]}}
EXPIRED = {"status": 440, "body": {}}
NOT_JSON = {"status": 200, "body": "<html>"}


def place(name, request, reply=None):
    return req(name, "/api/v1/placeorder", request, {CREATE: reply or OK_ORDER})


def order(oid, ref, side, qty, ptype="LIMIT", delivery="IDAY", exchange="NSE", derivative="", price=0, filled=0, filled_price=0, name="SBIN", **extra):
    o = {"intentOrderId": oid, "refId": ref, "refData": {"stockName": name, "exchange": exchange, "derivativeType": derivative, "displayName": name}, "side": side, "deliveryType": delivery, "priceType": ptype, "orderQty": qty, "filledQty": filled,
         "orderPrice": price, "filledPrice": filled_price, "stratTags": ["my-strategy"], "timestamps": {"intentCreatedAt": "2026-11-03T04:50:01.123456789Z", "lastUpdatedAt": "2026-11-03T04:51:02.5Z"}, "exchangeOrderIds": {str(ref): [2011]}}
    o.update(extra)
    return o


def trigger(value, bound="atOrAbove"):
    return {"triggers": {"ltp": {bound: {"value": value}}}}


BUCKETS = {"orders": {
    "open": [order(1, 3045, "BUY", 10, price=80050), order(2, 49500, "BUY", 75, "LIMIT", "CNC", "NSE", "FUT", price=2401000, name="NIFTY26NOVFUT", entryConfig=trigger(2400000)), order(3, 3045, "SELL", 6, "MARKET", filled=2, filled_price=80560)],
    "executed": [order(4, 1594, "SELL", 5, "LIMIT", "CNC", price=149500, filled=5, filled_price=149995, name="INFY"), order(5, 3045, "BUY", 4, "MARKET", filled=4, filled_price=80510)],
    "cancelled": [order(6, 500570, "BUY", 2, "LIMIT", "CNC", "BSE", price=48900, name="TATAMOTORS")],
    "rejected": [order(7, 3045, "BUY", 1, "MARKET")],
    "expired": [order(8, 3045, "SELL", 3, "LIMIT", price=81000)],
    "gtt": [order(9, 3045, "BUY", 1, "LIMIT", price=79000, entryConfig=trigger(79500, "atOrBelow"))],
}}
ORDERS = {"body": BUCKETS}
WORKING = {"body": {"orders": {"open": [order(11, 3045, "BUY", 10, price=80050), order(12, 3045, "SELL", 5, price=81000)], "gtt": [order(13, 3045, "BUY", 1, price=79000, entryConfig=trigger(79500, "atOrBelow"))], "executed": [order(14, 1594, "SELL", 5, filled=5, filled_price=149995)]}}}
EMPTY_BOOK = {"body": {"orders": {"open": [], "executed": []}}}
NO_ORDERS = {"body": {}}


def pos(ref, symbol, net, avg=0, ltp=0, derivative="", delivery="IDAY", exchange="NSE", **extra):
    p = {"refId": ref, "symbol": symbol, "exchange": exchange, "derivativeType": derivative, "deliveryType": delivery, "netQty": net, "avgPrice": avg, "avgBuyPrice": avg if net > 0 else 0, "avgSellPrice": avg if net < 0 else 0, "ltp": ltp, "pnl": (ltp - avg) * net, "pnlChg": 1.25}
    p.update(extra)
    return p


POSITIONS = {"body": {"portfolio": {"positions": [
    pos(3045, "SBIN", 10, 80000, 80550),
    pos(49500, "NIFTY26NOVFUT", -75, 2400000, 2410025, "FUT", "CNC"),
    pos(1594, "INFY", 0, 150000, 150000, delivery="CNC"),
    pos(500570, "TATAMOTORS", 3, 50000, 51000, delivery="CNC", exchange="BSE"),
    pos(999999, "NOSUCH", 4, 1000, 1100),
    pos(49599, "NIFTY26NOV24000CE", 75, 10050, 11000, "OPT", "IDAY"),
]}}}
FLAT = {"body": {"portfolio": {"positions": []}}}
HOLDINGS = {"body": {"portfolio": {"clientCode": "C1", "holdingStats": {"investedAmount": 1000050, "currentValue": 1100075, "totalPnl": 100025, "totalPnlChg": 10.0, "dayPnl": 5050, "dayPnlChg": 0.46}, "holdings": [
    {"refId": 1594, "symbol": "INFY", "exchange": "NSE", "quantity": 6, "avgPrice": 145050, "lastTradedPrice": 150000, "prevClose": 149000, "investedValue": 870300, "currentValue": 900000, "netPnl": 29700, "netPnlChg": 3.412, "dayPnl": 6000},
    {"refId": 500570, "symbol": "TATAMOTORS", "exchange": "BSE", "quantity": 4, "avgPrice": 50000, "lastTradedPrice": 51000, "investedValue": 200000, "currentValue": 204000},
    {"refId": 999999, "symbol": "NOSUCH", "quantity": 3, "avgPrice": 1225, "lastTradedPrice": 1100},
]}}}
FUNDS = {"body": {"portFundsAndMargin": {"netMarginAvailable": 9500075, "totalCollateral": 250050, "netDerivativePrem": 34025, "mtmEqIdayCnc": -5025, "mtmEqDelivery": 1000, "mtmDeriv": -8000, "totalMarginBlocked": 500025}}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "7001001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}


def cancel(name, order_id, reply, **extra):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": order_id}, {CANCEL: reply}, **extra)


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_is_cnc", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_is_ioc_without_a_price", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit_buy", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_limit_sell", {**PLACE, "action": "SELL", "pricetype": "SL", "price": "790", "trigger_price": "791.25"}),
    place("placeorder_stop_market_buy", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sell", {**PLACE, "action": "SELL", "pricetype": "SL-M", "price": "0", "trigger_price": "790"}),
    place("placeorder_stop_without_a_trigger_is_refused", {**PLACE, "pricetype": "SL", "trigger_price": "0"}),
    place("placeorder_stop_market_without_a_trigger_is_refused", {**PLACE, "pricetype": "SL-M", "trigger_price": "0"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_price_with_paise_rounding", {**PLACE, "price": "800.555"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_an_option", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "price": "100.5"}),
    place("placeorder_strategy_name_is_cleaned", {**PLACE, "strategy": "Ünïcode  Strat__X!"}),
    place("placeorder_strategy_name_of_symbols_only", {**PLACE, "strategy": "___"}),
    place("placeorder_reply_with_no_order_id", PLACE, {"body": {"orders": [{}]}}),
    place("placeorder_reply_with_no_orders", PLACE, {"body": {"orders": []}}),
    place("placeorder_reply_with_orders_that_is_not_a_list", PLACE, {"body": {"orders": "x"}}),
    place("placeorder_reply_with_an_empty_body", PLACE, {"body": {}}),
    place("placeorder_refused_with_an_error", PLACE, {"status": 400, "body": {"error": "Insufficient funds", "nubra_error_code": "E1"}}),
    place("placeorder_refused_with_a_message", PLACE, {"status": 400, "body": {"message": "Bad quantity"}}),
    place("placeorder_refused_with_no_reason", PLACE, {"status": 400, "body": {}}),
    place("placeorder_with_status_200_and_an_error", PLACE, {"body": {"error": "Order rejected", "orders": []}}),
    place("placeorder_session_expired", PLACE, EXPIRED),
    place("placeorder_throttled_then_placed", PLACE, [{"status": 429, "body": {}}, OK_ORDER]),
    place("placeorder_throttled_every_time", PLACE, {"status": 429, "body": {}}),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MODIFY: {"body": {"orders": [{"intentOrderId": 7001002}]}}}),
    req("modifyorder_reply_without_an_id_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {MODIFY: {"body": {"orders": [{}]}}}),
    req("modifyorder_empty_reply_is_success", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 204, "body": ""}}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "trigger_price": "799", "price": "798"}, {MODIFY: {"body": {"orders": [{"intentOrderId": 7001001}]}}}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {MODIFY: {"body": {"orders": [{"intentOrderId": 7001001}]}}}),
    req("modifyorder_an_id_that_is_not_a_number", "/api/v1/modifyorder", {**CHANGE, "orderid": "abc"}, {MODIFY: OK_ORDER}),
    req("modifyorder_refused_with_an_error", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 400, "body": {"error": "Order is not open"}}}),
    req("modifyorder_refused_with_a_message", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 400, "body": {"message": "Bad price"}}}),
    req("modifyorder_refused_with_no_reason", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 400, "body": {}}}),
    req("modifyorder_session_expired", "/api/v1/modifyorder", CHANGE, {MODIFY: EXPIRED}),
    req("modifyorder_throttled_every_time", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 429, "body": {}}}),
    req("modifyorder_not_json", "/api/v1/modifyorder", CHANGE, {MODIFY: NOT_JSON}),
    req("modifyorder_unknown_symbol", "/api/v1/modifyorder", {**CHANGE, "symbol": "NOPE"}, {MODIFY: OK_ORDER}),
    cancel("cancelorder", "7001001", {"body": {}}),
    cancel("cancelorder_empty_reply_is_success", "7001001", {"status": 204, "body": ""}),
    cancel("cancelorder_an_id_that_is_not_a_number", "abc", {"body": {}}),
    cancel("cancelorder_refused_with_an_error", "7001001", {"status": 400, "body": {"error": "Order already complete"}}),
    cancel("cancelorder_refused_with_a_message", "7001001", {"status": 400, "body": {"message": "No such order"}}),
    cancel("cancelorder_refused_with_no_reason", "7001001", {"status": 400, "body": {}}),
    cancel("cancelorder_empty_error_reply", "7001001", {"status": 500, "body": ""}),
    cancel("cancelorder_session_expired", "7001001", EXPIRED),
    cancel("cancelorder_throttled_every_time", "7001001", {"status": 429, "body": {}}),
    cancel("cancelorder_not_json", "7001001", NOT_JSON),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: WORKING, CANCEL: {"body": {}}}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: WORKING, CANCEL: [{"body": {}}, {"status": 400, "body": {"error": "Already done"}}, {"body": {}}]}),
    req("cancelallorder_nothing_working", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY_BOOK}),
    req("cancelallorder_no_orders_key", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"status": 400, "body": {"error": "Bad"}}}),
    req("cancelallorder_session_expired", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EXPIRED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY_BOOK}),
    req("orderbook_no_orders_key", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_error", "/api/v1/orderbook", {}, {OB: {"status": 400, "body": {"error": "Bad request"}}}),
    req("orderbook_session_expired", "/api/v1/orderbook", {}, {OB: EXPIRED}),
    req("orderbook_http_error_with_no_body", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": ""}}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: NOT_JSON}),
    req("orderbook_throttled_then_read", "/api/v1/orderbook", {}, {OB: [{"status": 429, "body": {}}, ORDERS]}),
    req("orderbook_throttled_every_time", "/api/v1/orderbook", {}, {OB: {"status": 429, "body": {}}}),
    req("tradebook", "/api/v1/tradebook", {}, {OB: ORDERS}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {OB: EMPTY_BOOK}),
    req("tradebook_error", "/api/v1/tradebook", {}, {OB: {"status": 400, "body": {"error": "Bad request"}}}),
    req("tradebook_session_expired", "/api/v1/tradebook", {}, {OB: EXPIRED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_no_portfolio", "/api/v1/positionbook", {}, {PB: {"body": {}}}),
    req("positionbook_error", "/api/v1/positionbook", {}, {PB: {"status": 400, "body": {"error": "Bad request"}}}),
    req("positionbook_session_expired", "/api/v1/positionbook", {}, {PB: EXPIRED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: {"body": {"portfolio": {"holdings": [], "holdingStats": {}}}}}),
    req("holdings_without_stats", "/api/v1/holdings", {}, {HB: {"body": {"portfolio": {"holdings": [{"refId": 1594, "symbol": "INFY", "exchange": "NSE", "quantity": 2, "avgPrice": 1000, "lastTradedPrice": 1100}]}}}}),
    req("holdings_no_portfolio", "/api/v1/holdings", {}, {HB: {"body": {}}}),
    req("holdings_error", "/api/v1/holdings", {}, {HB: {"status": 400, "body": {"error": "Bad request"}}}),
    req("holdings_session_expired", "/api/v1/holdings", {}, {HB: EXPIRED}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS}),
    req("funds_without_data", "/api/v1/funds", {}, {FB: {"body": {}}}),
    req("funds_with_an_empty_snapshot", "/api/v1/funds", {}, {FB: {"body": {"portFundsAndMargin": {}}}}),
    req("funds_values_as_text", "/api/v1/funds", {}, {FB: {"body": {"portFundsAndMargin": {"netMarginAvailable": "1000", "totalCollateral": "n/a", "mtmDeriv": None}}}}),
    req("funds_not_json", "/api/v1/funds", {}, {FB: NOT_JSON}),
    req("funds_session_expired", "/api/v1/funds", {}, {FB: EXPIRED}),
    req("funds_http_error", "/api/v1/funds", {}, {FB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "CNC"}, {PB: POSITIONS}),
    req("openposition_not_held_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "product": "NRML"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, CREATE: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"status": 400, "body": {"error": "Bad"}}}),
    req("closeposition_an_order_refused", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"portfolio": {"positions": [pos(3045, "SBIN", 10, 80000, 80550)]}}}, CREATE: {"status": 400, "body": {"error": "No margin"}}}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, CREATE: OK_ORDER}),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: {"status": 400, "body": {"error": "Bad"}}}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's. A throttled call waits, which a recording has no use for."""
    import importlib

    importlib.import_module("broker.nubra.api.order_api")._POSITION_BOOK.invalidate("nubra-session-token")
    importlib.import_module("broker.nubra.api.order_api").time.sleep = lambda *a, **k: None
