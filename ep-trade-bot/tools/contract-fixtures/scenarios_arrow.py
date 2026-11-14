"""Arrow scenarios: orders, books and funds on edge.arrow.trade. Every request carries the appID and token headers; prices are in rupees; quantities and prices travel as text."""

AUTH_TOKEN = "arrow-access-token"
ENV = {"BROKER_API_KEY": "arrow-app-id", "BROKER_API_SECRET": "arrow-api-secret"}
SEED_API_KEY_ROW = True


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 1001, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1002, "INFOSYS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 50001, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "NIFTY"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
ORDER_OK = {"body": {"status": "success", "data": {"orderNo": "A0001"}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds"}}


def order(no, symbol, exch, side, qty, price, trigger, otype, product, status, when="2026-11-03 10:20:01", **extra):
    o = {"orderNo": no, "exchange": exch, "symbol": symbol, "transactionType": side, "quantity": qty, "price": price, "triggerPrice": trigger, "order": otype,
         "product": product, "orderStatus": status, "orderTime": when}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "success", "data": [
    order("A1", "SBIN-EQ", "NSE", "B", "10", "800.5", "0", "LMT", "I", "OPEN"),
    order("A2", "INFY-EQ", "NSE", "S", "5", "0", "0", "MKT", "C", "COMPLETE", "2026-11-03 10:21:11"),
    order("A3", "NIFTY25NOV26FUT", "NFO", "B", "75", "24010", "24000", "SL-LMT", "M", "TRIGGER_PENDING", "2026-11-03 10:22:00"),
    order("A4", "SBIN-EQ", "NSE", "S", "1", "0", "790.5", "SL-MKT", "I", "REJECTED", "2026-11-03 10:23:00"),
    order("A5", "SBIN-EQ", "NSE", "B", "2", "700", "0", "LMT", "I", "CANCELLED", "2026-11-03 10:24:00"),
    order("A6", "SBIN-EQ", "NSE", "B", "3", "700", "0", "LMT", "I", "PENDING", "2026-11-03 10:25:00"),
    order("A7", "UNKNOWN", "NSE", "B", "1", "5", "0", "XX", "Z", "SOMETHING", "2026-11-03 10:26:00"),
    {"id": "A8", "exchange": "NSE", "symbol": "SBIN-EQ", "transactionType": "B", "quantity": "1", "price": "1", "order": "LMT", "product": "I", "orderStatus": "OPEN", "requestTime": "2026-11-03 10:27:00"},
]}}
NO_ORDERS = {"body": {"status": "success", "data": []}}
NULL_ORDERS = {"body": {"status": "success", "data": None}}


def trade(no, symbol, exch, side, qty, price, product="I", when="2026-11-03 10:21:12", **extra):
    t = {"orderNo": no, "exchange": exch, "symbol": symbol, "transactionType": side, "fillQuantity": qty, "fillPrice": price, "product": product, "fillTime": when}
    t.update(extra)
    return t


TRADES = {"body": {"status": "success", "data": [
    trade("A2", "INFY-EQ", "NSE", "S", "5", "1499.95", "C"),
    trade("A3", "NIFTY25NOV26FUT", "NFO", "B", "75", "24005.5", "M"),
    {"id": "A9", "exchange": "NSE", "symbol": "SBIN-EQ", "transactionType": "B", "quantity": "2", "averagePrice": "801", "product": "I", "orderTime": "2026-11-03 10:30:00"},
    trade("A5", "SBIN-EQ", "NSE", "B", None, None),
]}}


def pos(symbol, exch, product, qty, avg, ltp, realised, unrealised):
    return {"symbol": symbol, "exchange": exch, "product": product, "qty": qty, "avgPrice": avg, "ltp": ltp, "realisedPnL": realised, "unrealisedMarkToMarket": unrealised}


POSITIONS = {"body": {"status": "success", "data": [
    pos("SBIN-EQ", "NSE", "I", "10", "801.1234", "805.45", "0", "43.1"),
    pos("NIFTY25NOV26FUT", "NFO", "M", "-75", "24005.5", "24000", "1200.555", "412.5"),
    pos("INFY-EQ", "NSE", "C", "0", "0", "0", "-5", "0"),
    pos("UNKNOWN", "NSE", "Q", "3", None, None, None, None),
]}}
NO_POSITIONS = {"body": {"status": "success", "data": []}}


def holding(symbol, exch, qty, avg, ltp, pnl):
    return {"qty": qty, "avgPrice": avg, "ltp": ltp, "pnl": pnl, "symbols": [{"tradingSymbol": symbol, "exchange": exch}]}


HOLDINGS = {"body": {"status": "success", "data": [
    holding("INFY-EQ", "NSE", "40", "1400", "1499", "3960"),
    holding("OLD-EQ", "NSE", "10", "0", "12.5", "0"),
    {"qty": "7", "avgPrice": "50", "ltp": "55", "pnl": "35", "symbols": []},
    {"qty": 3, "avgPrice": None, "ltp": None, "pnl": None, "symbols": [{"symbol": "SBIN-EQ", "exchange": "NSE"}]},
]}}
NO_HOLDINGS = {"body": {"status": "success", "data": []}}
LIMITS = {"body": {"status": "success", "data": {"margin": {"allocated": "100000.5", "utilized": "1234.75", "unrealizedPnl": "55.5", "realizedPnl": "-12.25"},
                                                 "allocations": [{"nonCashCurrent": "1500"}, {"nonCashCurrent": "250.5"}, {}]}}}
LIMITS_BARE = {"body": {"status": "success", "data": {}}}
LIMITS_NO_STATUS = {"body": {"data": {"margin": {"allocated": "10", "utilized": "4"}}}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


PLACE_EP = "POST /order/regular"
OB, TB, PB, HB, LB = "GET /user/orders", "GET /user/trades", "GET /user/positions", "GET /user/holdings", "GET /user/limits"
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "A1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    req("placeorder_limit", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_OK}),
    req("placeorder_market_uses_market_price_protection", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_cnc_with_disclosed_quantity", "/api/v1/placeorder", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_limit", "/api/v1/placeorder", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_market", "/api/v1/placeorder", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_limit_carries_no_trigger", "/api/v1/placeorder", {**PLACE, "trigger_price": "799"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_futures_nrml", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_mcx", "/api/v1/placeorder", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_strategy_name_is_cut_to_sixteen", "/api/v1/placeorder", {**PLACE, "strategy": "s" * 30}, {PLACE_EP: ORDER_OK}),
    req("placeorder_broker_refuses", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_REFUSED}),
    req("placeorder_refused_with_an_ok_status", "/api/v1/placeorder", PLACE, {PLACE_EP: {"body": {"status": "error", "message": "Rejected"}}}),
    req("placeorder_success_without_an_order_number", "/api/v1/placeorder", PLACE, {PLACE_EP: {"body": {"status": "success", "data": {}}}}),
    req("placeorder_unknown_symbol", "/api/v1/placeorder", {**PLACE, "symbol": "NOPE"}, {PLACE_EP: ORDER_OK}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {"PATCH /order/regular/A1": ORDER_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {"PATCH /order/regular/A1": ORDER_OK}),
    req("modifyorder_to_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {"PATCH /order/regular/A1": ORDER_OK}),
    req("modifyorder_answer_without_an_order_number_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {"PATCH /order/regular/A1": {"body": {"status": "success", "data": {}}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {"PATCH /order/regular/A1": {"status": 400, "body": {"status": "error", "message": "Order is not open"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {"PATCH /order/regular/A1": {"status": 400, "body": {"status": "error"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "A1"}, {"DELETE /order/regular/A1": {"body": {"status": "success"}}}),
    req("cancelorder_refused_with_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "A2"}, {"DELETE /order/regular/A2": {"status": 400, "body": {"status": "error", "message": "Order already completed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "A2"}, {"DELETE /order/regular/A2": {"status": 500, "body": {"x": 1}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, "DELETE /order/regular/A1": {"body": {"status": "success"}}, "DELETE /order/regular/A3": {"status": 400, "body": {"message": "no"}},
                                                                    "DELETE /order/regular/A6": {"body": {"status": "success"}}, "DELETE /order/regular/A8": {"body": {"status": "success"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_data_is_null", "/api/v1/orderbook", {}, {OB: NULL_ORDERS}),
    req("orderbook_refused", "/api/v1/orderbook", {}, {OB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_ORDERS}),
    req("tradebook_refused", "/api/v1/tradebook", {}, {TB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_refused", "/api/v1/positionbook", {}, {PB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_refused", "/api/v1/holdings", {}, {HB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS}),
    req("funds_bare_answer", "/api/v1/funds", {}, {LB: LIMITS_BARE}),
    req("funds_answer_without_a_status", "/api/v1/funds", {}, {LB: LIMITS_NO_STATUS}),
    req("funds_error_status_in_an_ok_answer", "/api/v1/funds", {}, {LB: {"body": {"status": "error", "message": "x"}}}),
    req("funds_refused", "/api/v1/funds", {}, {LB: {"status": 401, "body": {"status": "error", "message": "bad token"}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"status": "success", "data": POSITIONS["body"]["data"][:3]}}, PLACE_EP: ORDER_OK}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ORDER_OK}),
]
