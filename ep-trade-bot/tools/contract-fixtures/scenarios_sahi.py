"""Sahi scenarios: orders, books and funds. Prices travel as integer paise, quantities as units; the API is on direct.sahi.com under /v1."""

AUTH_TOKEN = "sahi-access-token"
ENV = {"BROKER_API_KEY": "sahi-api-key", "BROKER_API_SECRET": "sahi-api-secret"}
SEED_API_KEY_ROW = True


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", 1001, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", 1002, "INFOSYS"),
    _sym("INFY", "INFY", "BSE", 2002, "INFOSYS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 50001, "NIFTY", 75, "FUTIDX", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY", "NIFTY", "NSE_INDEX", 26000, "NIFTY"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
ORDER_OK = {"status": 201, "body": {"order_ids": ["S0001"]}}
ORDER_OK_MANY = {"status": 201, "body": {"order_ids": ["S0001", "S0002"]}}


def failure(status, code, message):
    return {"status": status, "body": {"code": code, "message": message}}


def order(oid, symbol, seg, side, qty, filled, price, trigger, avg, otype, product, status, created=1793690000000, **extra):
    o = {"order_id": oid, "exchange_segment": seg, "symbol": symbol, "txn_type": side, "qty": qty, "filled_qty": filled, "price": price, "trigger_price": trigger, "avg_price": avg,
         "order_type": otype, "product": product, "status": status, "created_at": created}
    o.update(extra)
    return o


ORDERS = {"body": {"orders": [
    order("S1", "SBIN", "NSE", "B", 10, 0, 80050, 0, 0, "LMT", "I", "Open"),
    order("S2", "INFY", "NSE", "S", 5, 5, 0, 0, 149995, "MKT", "C", "Completed", 1793690100000),
    order("S3", "NIFTY25NOV26FUT", "NFO", "B", 75, 25, 2401000, 2400000, 2400500, "SL-LMT", "M", "OpenPartial", 1793690200000),
    order("S4", "SBIN", "NSE", "S", 1, 0, 0, 79050, 0, "SL-MKT", "I", "Rejected", 1793690300000, rejection_reason="Insufficient funds"),
    order("S5", "SBIN", "NSE", "B", 2, 0, 70000, 0, 0, "LMT", "I", "Cancelled", 1793690400000),
    order("S6", "SBIN", "NSE", "B", 4, 1, 70000, 0, 70000, "LMT", "F", "PartialFilled", 1793690500000),
    order("S7", "UNKNOWN", "NSE", "B", 1, 0, 500, 0, 0, "LMT", "I", "Something Else", 0),
]}}
NO_ORDERS = {"body": {"orders": []}}


def trade(oid, tid, symbol, seg, side, qty, price, product="C", at=1793690101000):
    return {"order_id": oid, "exchange_trade_id": tid, "exchange_segment": seg, "symbol": symbol, "txn_type": side, "qty": qty, "price": price, "product": product, "traded_at": at}


TRADES_S2 = {"body": {"trades": [trade("S2", "T1", "INFY", "NSE", "S", 3, 149990), trade("S2", "T2", "INFY", "NSE", "S", 2, 150002)]}}
TRADES_S3 = {"body": {"trades": [trade("S3", "T3", "NIFTY25NOV26FUT", "NFO", "B", 25, 2400500, "M", 1793690201000)]}}
TRADES_S6 = {"body": {"trades": [trade("S6", "T4", "SBIN", "NSE", "B", 1, 70000, "F", 0)]}}


def pos(symbol, seg, product, iid, net, avg, realised, buy=0, sell=0):
    return {"symbol": symbol, "exchange_segment": seg, "product": product, "instrument_id": iid, "net_qty": net, "net_avg_price": avg, "realised_pnl": realised, "total_buy_qty": buy, "total_sell_qty": sell}


POSITIONS = {"body": {"positions": [
    pos("SBIN", "NSE", "I", 1001, 10, 80123, 0, 10, 0),
    pos("NIFTY25NOV26FUT", "NFO", "M", 50001, -75, 2400550, 12345, 0, 75),
    pos("INFY", "NSE", "C", 1002, 0, 0, -500, 5, 5),
    pos("UNKNOWN", "NSE", "I", 9999, 3, 1000, 0, 3, 0),
]}}
POSITIONS_LISTED = {"body": {"positions": POSITIONS["body"]["positions"][:3]}}
NO_POSITIONS = {"body": {"positions": []}}
LTPS = {"body": {"data": {"1001": {"last_trade_price": 80545}, "50001": {"last_trade_price": 2400000}, "9999": {"last_trade_price": 1010}}}}
LTPS_PARTIAL = {"body": {"data": {"1001": {"last_trade_price": 80545}}}}


def holding(isin, ids, qty, avg):
    return {"isin": isin, "instrument_ids": ids, "holding_qty": qty, "holding_avg_price": avg}


HOLDINGS = {"body": {"holdings": [
    holding("INE001", [1002, 2002], 40, 140000),
    holding("INE002", [2002], 7, 5000),
    holding("INE003", [1001], 0, 80000),
    holding("INE004", [7777], 3, 1000),
    holding("INE005", [1001], 5, 0),
]}}
LTPS_HOLD = {"body": {"data": {"1002": {"last_trade_price": 151000}, "2002": {"last_trade_price": 150900}, "1001": {"last_trade_price": 80500}}}}
NO_HOLDINGS = {"body": {"holdings": []}}
FUNDS = {"body": {"available": {"intraday_and_fno_selling": 10122580}, "balance": {"collateral": 150000}, "utilised": {"span": 10000, "exposure": 5000, "delivery_margin": 1000, "intraday_margin": 2000,
         "delivery_buy": 3000, "charges": 495, "mtm_loss": 0}, "option_premium": {"paid": 20000}}}
FUNDS_BARE = {"body": {"available": {}}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


PLACE_EP = "POST /v1/orders"
OB, PB, HB, FB, LB = "GET /v1/orders", "GET /v1/positions", "GET /v1/holdings", "GET /v1/funds", "GET /v1/marketdata/ltp"
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "S1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    req("placeorder_limit", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_OK}),
    req("placeorder_market", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_cnc_with_disclosed_quantity", "/api/v1/placeorder", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_limit", "/api/v1/placeorder", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_market", "/api/v1/placeorder", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_limit_carries_no_trigger", "/api/v1/placeorder", {**PLACE, "trigger_price": "799"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_price_is_rounded_half_up_to_paise", "/api/v1/placeorder", {**PLACE, "price": "800.505"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_futures_nrml", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_index_exchange_refused", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY", "exchange": "NSE_INDEX"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_several_ids_use_the_first", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_OK_MANY}),
    req("placeorder_ok_status_without_ids", "/api/v1/placeorder", PLACE, {PLACE_EP: {"status": 200, "body": {}}}),
    req("placeorder_ip_not_allowed", "/api/v1/placeorder", PLACE, {PLACE_EP: failure(403, "IP_NOT_ALLOWED", "x")}),
    req("placeorder_session_expired", "/api/v1/placeorder", PLACE, {PLACE_EP: failure(401, "UNAUTHORIZED", "x")}),
    req("placeorder_forbidden", "/api/v1/placeorder", PLACE, {PLACE_EP: failure(403, "FORBIDDEN", "x")}),
    req("placeorder_rate_limited", "/api/v1/placeorder", PLACE, {PLACE_EP: failure(429, "RATE_LIMITED", "x")}),
    req("placeorder_refused_with_a_message", "/api/v1/placeorder", PLACE, {PLACE_EP: failure(400, "INVALID", "Quantity is not a multiple of the lot size")}),
    req("placeorder_refused_without_a_message", "/api/v1/placeorder", PLACE, {PLACE_EP: {"status": 400, "body": {"x": 1}}}),
    req("placeorder_server_error_without_a_message", "/api/v1/placeorder", PLACE, {PLACE_EP: {"status": 503, "body": {"x": 1}}}),
    req("placeorder_unknown_symbol", "/api/v1/placeorder", {**PLACE, "symbol": "NOPE"}, {PLACE_EP: ORDER_OK}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/S1": {"body": {"order_id": "S1"}}}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {"PUT /v1/orders/S1": {"body": {"order_id": "S1"}}}),
    req("modifyorder_to_market_drops_the_price", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {"PUT /v1/orders/S1": {"body": {"order_id": "S1"}}}),
    req("modifyorder_answer_without_an_id_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/S1": {"body": {}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/S1": failure(400, "INVALID", "Order is not open")}),
    req("modifyorder_session_expired", "/api/v1/modifyorder", CHANGE, {"PUT /v1/orders/S1": failure(401, "UNAUTHORIZED", "x")}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S1"}, {"DELETE /v1/orders/S1": {"status": 204, "body": {}}}),
    req("cancelorder_with_an_ok_status", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S1"}, {"DELETE /v1/orders/S1": {"body": {}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {"DELETE /v1/orders/S2": failure(400, "INVALID", "Order already completed")}),
    req("cancelorder_server_error", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {"DELETE /v1/orders/S2": {"status": 502, "body": {"x": 1}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, "DELETE /v1/orders/S1": {"status": 204, "body": {}}, "DELETE /v1/orders/S3": failure(400, "INVALID", "no")}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_session_expired", "/api/v1/orderbook", {}, {OB: failure(401, "UNAUTHORIZED", "x")}),
    req("orderbook_refused_with_a_message", "/api/v1/orderbook", {}, {OB: failure(400, "INVALID", "Bad request")}),
    req("orderbook_without_the_orders_key", "/api/v1/orderbook", {}, {OB: {"body": {"x": 1}}}),
    req("tradebook", "/api/v1/tradebook", {}, {OB: ORDERS, "GET /v1/orders/S2/trades": TRADES_S2, "GET /v1/orders/S3/trades": TRADES_S3, "GET /v1/orders/S6/trades": TRADES_S6}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {OB: NO_ORDERS}),
    req("tradebook_one_order_unreadable", "/api/v1/tradebook", {}, {OB: ORDERS, "GET /v1/orders/S2/trades": failure(500, "ERR", "down"), "GET /v1/orders/S3/trades": TRADES_S3, "GET /v1/orders/S6/trades": TRADES_S6}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: LTPS}),
    req("positionbook_some_prices_missing", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: LTPS_PARTIAL}),
    req("positionbook_prices_refused", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: failure(500, "ERR", "down")}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_refused", "/api/v1/positionbook", {}, {PB: failure(401, "UNAUTHORIZED", "x")}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS, LB: LTPS_HOLD}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_prices_refused", "/api/v1/holdings", {}, {HB: HOLDINGS, LB: failure(500, "ERR", "down")}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS, PB: POSITIONS, LB: LTPS}),
    req("funds_bare_answer", "/api/v1/funds", {}, {FB: FUNDS_BARE, PB: NO_POSITIONS}),
    req("funds_positions_unreadable", "/api/v1/funds", {}, {FB: FUNDS, PB: failure(500, "ERR", "down")}),
    req("funds_refused", "/api/v1/funds", {}, {FB: failure(401, "UNAUTHORIZED", "x")}),
    req("funds_without_the_available_block", "/api/v1/funds", {}, {FB: {"body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS, LB: LTPS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS_LISTED, PLACE_EP: ORDER_OK, LB: LTPS}),
    req("closeposition_unlisted_symbol_stays_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: ORDER_OK, LB: LTPS}),
    req("closeposition_an_order_is_refused", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS_LISTED, PLACE_EP: failure(400, "INVALID", "Insufficient funds"), LB: LTPS}),
    req("closeposition_mtf_position_stays_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"positions": [pos("SBIN", "NSE", "F", 1001, 4, 80000, 0, 4, 0)]}}, LB: LTPS}),
    req("placeorder_rate_limited_then_accepted", "/api/v1/placeorder", PLACE, {PLACE_EP: [failure(429, "RATE_LIMITED", "x"), ORDER_OK]}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ORDER_OK, LB: LTPS}),
]
