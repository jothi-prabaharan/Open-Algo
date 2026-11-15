"""Pocketful (trade.pocketful.in): orders, books, funds. The access token is a bearer token. Almost every call needs the client id, which OpenAlgo reads from the sign-in it stored and otherwise asks /api/v1/user/trading_info for, so a recording starts there.
A reply is {status: success, data} or {status: error, message}; an HTTP error is turned into text by the client that OpenAlgo shows. The order book is two reads (completed and pending) joined; the trade book, positions and holdings are one each."""

AUTH_TOKEN = "pocketful-access-token"
ENV = {"BROKER_API_KEY": "pf-client-key", "BROKER_API_SECRET": "pf-client-secret"}
CLIENT_ID = "PF100"
SEED_API_KEY_ROW = True
READ_POSTS = []

TI = "GET /api/v1/user/trading_info"
ORD, ORDS = "POST /api/v1/orders", "GET /api/v1/orders"
MOD, CAN = "PUT /api/v1/orders", "DELETE /api/v1/orders/{id}"
TRADES, POSITIONS_EP, HOLD, FUNDS_EP = "GET /api/v1/trades", "GET /api/v1/positions", "GET /api/v1/holdings", "GET /api/v2/funds/view"


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=0.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", 0.0, 0.1),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", 0.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", 0.0, 1.0),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
CLIENT = {"body": {"status": "success", "data": {"client_id": "PF100"}}}


def req(name, path, request, broker=None, client=True, **extra):
    replies = {TI: CLIENT} if client else {}
    replies.update(broker or {})
    return {"name": name, "path": path, "request": request, "broker": replies, **extra}


def ok(data=None, **extra):
    return {"body": {"status": "success", "data": data if data is not None else {}, **extra}}


OK_ORDER = ok({"oms_order_id": "2611030001"})
ERR = {"body": {"status": "error", "message": "RMS: insufficient funds"}}
NOT_JSON = {"status": 200, "body": "<html>"}


def place(name, request, reply=None, **extra):
    return req(name, "/api/v1/placeorder", request, {ORD: reply or OK_ORDER}, **extra)


def order(oms, symbol, side, qty, otype, product, status, exchange="NSE", **extra):
    o = {"oms_order_id": oms, "trading_symbol": symbol, "exchange": exchange, "order_side": side, "quantity": qty, "price": 0.0, "trigger_price": 0.0, "order_type": otype, "product": product, "order_status": status, "mode": "",
         "order_entry_time": "2026-11-03 10:20:01", "filled_quantity": 0, "remaining_quantity": qty, "average_price": 0.0}
    o.update(extra)
    return o


COMPLETED = ok({"orders": [
    order("C1", "INFY-EQ", "SELL", 5, "LIMIT", "CNC", "COMPLETE", price=1495.0, filled_quantity=5, remaining_quantity=0, average_price=1495.0),
    order("C2", "SBIN-EQ", "BUY", 1, "MARKET", "MIS", "REJECTED"),
    order("C3", "TATAMOTORS", "BUY", 2, "SL", "CNC", "CANCEL_CONFIRMED", exchange="BSE", trigger_price=490.0, price=489.0),
    order("C4", "SBIN-EQ", "SELL", 3, "LIMIT", "MIS", "CANCELLED", price=810.0),
]})
PENDING = ok({"orders": [
    order("P1", "SBIN-EQ", "BUY", 10, "LIMIT", "MIS", "OPEN", price=800.5),
    order("P2", "NIFTY26NOVFUT", "BUY", 75, "SL", "NRML", "TRIGGER PENDING", exchange="NFO", price=24010.0, trigger_price=24000.0),
    order("P3", "SBIN-EQ", "BUY", 4, "LIMIT", "MIS", "PENDING", price=790.0),
    order("P4", "SBIN-EQ", "BUY", 4, "LIMIT", "MIS", "AMO_SUBMIT", price=790.0),
    order("P5", "SBIN-EQ", "BUY", 4, "LIMIT", "MIS", "MODIFY_PENDING", price=790.0),
    order("P6", "SBIN-EQ", "BUY", 4, "LIMIT", "MIS", "VALIDATED", mode="NEW"),
    order("P7", "SBIN-EQ", "BUY", 4, "SLM", "MIS", "SOMETHING ELSE"),
    order("P8", "NOSUCH-EQ", "SELL", 1, "LIMIT", "CNC", "OPEN"),
    order("P9", "SBIN-EQ", "buy", 2, "LIMIT", "MIS", "open", filled_quantity=1, remaining_quantity=1, average_price=0.0, average_trade_price=800.25),
]})
def can(oms, status, **extra):
    """A pending order as the order book and the cancel-all both read it: the status is in both fields."""
    o = order(oms, "SBIN-EQ", "BUY", 1, "LIMIT", "MIS", status, **extra)
    o["status"] = status
    return o


CAN1, CAN2, CAN3 = can("A1", "OPEN"), can("A2", "PENDING"), can("A3", "TRIGGER PENDING")
CAN4, CAN6, CAN7 = can("A4", "", mode="NEW"), can("A6", "PARTIALLY OPEN"), can("A7", "COMPLETE")
EMPTY = ok({"orders": []})
EMPTY_LIST = ok([])
FAILED = {"body": {"status": "error", "message": "Session expired"}}


def trade(num, oms, symbol, side, qty, price, exchange="NSE", product="MIS", **extra):
    t = {"trade_number": num, "oms_order_id": oms, "trading_symbol": symbol, "exchange": exchange, "order_side": side, "trade_quantity": qty, "trade_price": price, "trade_time": "2026-11-03 10:21:12", "product": product}
    t.update(extra)
    return t


TRADES_OK = ok({"trades": [trade("T0", "C9", "SBIN-EQ", "SELL", 3, 801.0, fill_quantity=2, avg_price=802.5, trade_id="TID", order_id="OID", fill_timestamp="2026-11-03 10:22:00", transaction_type="sell"), trade("T1", "C1", "INFY-EQ", "SELL", 5, 1499.95, product="CNC"), trade("T2", "P1", "SBIN-EQ", "BUY", 10, 800.25), trade("T3", "P2", "NIFTY26NOVFUT", "BUY", 75, 24005.5, "NFO", "NRML"), trade("T4", "X9", "NOSUCH-EQ", "SELL", 2, 7.5)]})


def pos(symbol, exchange, product, net, buy_avg=0.0, sell_avg=0.0, ltp=None, **extra):
    p = {"trading_symbol": symbol, "exchange": exchange, "product": product, "net_quantity": net, "average_buy_price": buy_avg, "average_sell_price": sell_avg, "buy_quantity": max(net, 0), "sell_quantity": max(-net, 0)}
    if ltp is not None:
        p["ltp"] = ltp
    p.update(extra)
    return p


POSITIONS = ok([
    pos("SBIN-EQ", "NSE", "MIS", 10, 800.0, 0.0, 805.5),
    pos("NIFTY26NOVFUT", "NFO", "NRML", -75, 0.0, 24000.0, 24100.25),
    pos("INFY-EQ", "NSE", "CNC", 0, 1500.0, 1500.0, 1500.0),
    pos("TATAMOTORS", "BSE", "CNC", 3, 500.0, 0.0, 510.0),
    pos("NOSUCH-EQ", "NSE", "MIS", 4, 10.0, 0.0, 11.0),
    pos("SBIN-EQ", "NSE", "CNC", -2, 0.0, 812.5, 810.0),
    pos("INFY-EQ", "NSE", "MIS", -1, 0.0, 0.0, 1500.0, average_price=1499.0),
    pos("INFY-EQ", "NSE", "NRML", 1, 1490.0, 1500.0, 1500.0),
])
FLAT = ok([])
HOLDINGS = ok({"holdings": [
    {"trading_symbol": "INFY-EQ", "exchange": "NSE", "quantity": 6, "average_price": 1450.5, "ltp": 1500.0, "close_price": 1490.0, "isin": "INE009A01021", "day_change": 10.0, "day_change_percentage": 0.67},
    {"trading_symbol": "TATAMOTORS", "exchange": "BSE", "quantity": 4, "average_price": 500, "ltp": 510, "previous_close": 505},
    {"trading_symbol": "NOSUCH-EQ", "exchange": "NSE", "quantity": 3, "average_price": 12.25, "ltp": 11.0},
    {"symbol": "SBIN", "quantity": "7", "buy_avg": "800.5", "current_price": "805.25", "pnl": "33.25", "pnl_percent": "0.59"},
    {"trading_symbol": "ZEROQ", "quantity": 0, "average_price": 0, "ltp": 0},
    {"trading_symbol": "NOAVG", "exchange": "NSE", "quantity": 5, "average_price": 0, "ltp": 10},
    {"trading_symbol": "NULLPNL", "exchange": "NSE", "quantity": 2, "average_price": 10, "ltp": 12.5, "pnl": None, "pnl_percent": None},
    {"trading_symbol": "TEXTPNL", "exchange": "NSE", "quantity": 2, "average_price": 10, "ltp": 12.5, "pnl": "x", "pnl_percent": "y"},
    {"trading_symbol": "ONLYPCT", "exchange": "NSE", "quantity": 3, "average_price": 10, "ltp": 11, "pnl_percentage": "2.3456"},
    {"trading_symbol": "ODDPCT", "exchange": "NSE", "quantity": 3, "average_price": 10.123, "ltp": 11.987, "pnl_percent": 1.23456, "pnl": 3.456},
    {"trading_symbol": "ZEROPCT", "exchange": "NSE", "quantity": 3, "average_price": 10, "ltp": 11, "pnl_percent": 0, "pnl_percentage": 4.5},
    {"trading_symbol": "ZEROQTY", "exchange": "NSE", "quantity": 0, "average_price": 10, "ltp": 12},
    {"trading_symbol": "NEGQTY", "exchange": "NSE", "quantity": -3, "average_price": 10, "ltp": 12},
    {"trading_symbol": "BIGONE", "exchange": "NSE", "quantity": 7, "average_price": 333.337, "ltp": 345.678, "close_price": 340.1, "day_change": 5.5555, "day_change_percentage": 1.6666},
    {"trading_symbol": "EMPTYID", "symbol": "", "exchange": "NSE", "quantity": 1, "average_price": 5, "ltp": 6},
    {"instrument_details": {"trading_symbol": "NESTED-EQ"}, "exchange": "NSE", "quantity": 1, "average_price": 5, "ltp": 6},
    {"instrument_details": {"symbol": "NESTED2"}, "exchange": "NSE", "quantity": 1, "average_price": 5, "ltp": 6},
]})
FUNDS = ok({"values": [["Available Margin", "95000.75"], ["Margin Used", "5000.25"], ["Total Pledge Collateral", "2500.5"], ["unrealized_mtm", "-120.5"], ["realized_mtm", "340.25"], ["DP Collateral Benefit", "1"]]})

CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "P1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}


def cancel(name, order_id, reply, **extra):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": order_id}, {CAN.replace("{id}", order_id): reply}, **extra)


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_is_native", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_is_slm", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_with_a_disclosed_quantity", {**PLACE, "disclosed_quantity": "3"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_reply_with_no_order_id", PLACE, ok({})),
    place("placeorder_reply_with_data_that_is_a_list", PLACE, ok([1])),
    place("placeorder_refused_with_error_and_a_message", PLACE, ERR),
    place("placeorder_refused_without_a_message", PLACE, {"body": {"status": "error"}}),
    place("placeorder_refused_with_http_400", PLACE, {"status": 400, "body": {"status": "error", "message": "Invalid quantity"}}),
    place("placeorder_refused_with_http_500", PLACE, {"status": 500, "body": {"x": 1}}),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MOD: ok({"oms_order_id": "P1"})}),
    req("modifyorder_stop", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "trigger_price": "799"}, {MOD: ok({"oms_order_id": "P1"})}),
    req("modifyorder_reply_without_an_order_id", "/api/v1/modifyorder", CHANGE, {MOD: ok({})}),
    req("modifyorder_refused_with_error", "/api/v1/modifyorder", CHANGE, {MOD: {"body": {"status": "error", "message": "Bad price"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MOD: {"body": {"status": "error"}}}),
    req("modifyorder_refused_with_http_400", "/api/v1/modifyorder", CHANGE, {MOD: {"status": 400, "body": {"status": "error", "message": "Invalid"}}}),
    req("modifyorder_not_json", "/api/v1/modifyorder", CHANGE, {MOD: NOT_JSON}),
    req("modifyorder_unknown_symbol", "/api/v1/modifyorder", {**CHANGE, "symbol": "NOPE"}, {MOD: ok({})}),
    cancel("cancelorder", "P1", ok({"oms_order_id": "P1"})),
    cancel("cancelorder_without_an_id_in_the_reply", "P1", ok({})),
    cancel("cancelorder_with_a_message", "P1", {"body": {"status": "success", "message": "Cancelled", "data": {"oms_order_id": "P1"}}}),
    cancel("cancelorder_refused_with_error", "P3", {"body": {"status": "error", "message": "Order already complete"}}),
    cancel("cancelorder_refused_without_a_message", "P3", {"body": {"status": "error"}}),
    cancel("cancelorder_refused_with_http_404", "P3", {"status": 404, "body": {"status": "error", "message": "Not found"}}),
    cancel("cancelorder_not_json", "P3", NOT_JSON),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: [ok({"orders": [CAN1, CAN2, CAN3, CAN4, CAN6, CAN7]}), EMPTY], **{f"DELETE /api/v1/orders/{i}": ok({}) for i in ("A1", "A2", "A3", "A4", "A6")}}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: [ok({"orders": [CAN1, CAN2, CAN3]}), EMPTY], "DELETE /api/v1/orders/A1": ok({}), "DELETE /api/v1/orders/A2": {"body": {"status": "error", "message": "Already done"}}, "DELETE /api/v1/orders/A3": ok({})}),
    req("cancelallorder_cancel_that_is_not_json", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: [ok({"orders": [CAN1]}), EMPTY], "DELETE /api/v1/orders/A1": NOT_JSON}),
    req("cancelallorder_cancel_with_http_error", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: [ok({"orders": [CAN1]}), EMPTY], "DELETE /api/v1/orders/A1": {"status": 500, "body": {"x": 1}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: EMPTY}),
    req("cancelallorder_nothing_matches", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: ok({"orders": [CAN7]})}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: FAILED}),
    req("cancelallorder_book_with_http_error", "/api/v1/cancelallorder", {"strategy": "t"}, {ORDS: {"status": 500, "body": {"x": 1}}}),
    req("orderbook", "/api/v1/orderbook", {}, {ORDS: [COMPLETED, PENDING]}),
    req("orderbook_only_pending", "/api/v1/orderbook", {}, {ORDS: [EMPTY, PENDING]}),
    req("orderbook_completed_failed", "/api/v1/orderbook", {}, {ORDS: [FAILED, PENDING]}),
    req("orderbook_pending_failed", "/api/v1/orderbook", {}, {ORDS: [COMPLETED, FAILED]}),
    req("orderbook_both_failed", "/api/v1/orderbook", {}, {ORDS: FAILED}),
    req("orderbook_orders_in_a_list", "/api/v1/orderbook", {}, {ORDS: [ok(COMPLETED["body"]["data"]["orders"]), EMPTY_LIST]}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {ORDS: [EMPTY, EMPTY]}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {ORDS: {"status": 500, "body": {"x": 1}}}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {ORDS: NOT_JSON}),
    req("tradebook", "/api/v1/tradebook", {}, {TRADES: TRADES_OK}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TRADES: ok({"trades": []})}),
    req("tradebook_data_is_a_list", "/api/v1/tradebook", {}, {TRADES: ok([trade("T1", "C1", "INFY-EQ", "SELL", 5, 1499.95)])}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TRADES: FAILED}),
    req("tradebook_http_error", "/api/v1/tradebook", {}, {TRADES: {"status": 500, "body": {"x": 1}}}),
    req("tradebook_not_json", "/api/v1/tradebook", {}, {TRADES: NOT_JSON}),
    req("positionbook", "/api/v1/positionbook", {}, {POSITIONS_EP: POSITIONS}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {POSITIONS_EP: FLAT}),
    req("positionbook_nested_positions", "/api/v1/positionbook", {}, {POSITIONS_EP: ok({"positions": [pos("SBIN-EQ", "NSE", "MIS", 5, 800.0)]})}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {POSITIONS_EP: FAILED}),
    req("positionbook_http_error", "/api/v1/positionbook", {}, {POSITIONS_EP: {"status": 500, "body": {"x": 1}}}),
    req("holdings", "/api/v1/holdings", {}, {HOLD: HOLDINGS}),
    req("holdings_in_a_list", "/api/v1/holdings", {}, {HOLD: ok([{"trading_symbol": "INFY-EQ", "exchange": "NSE", "quantity": 6, "average_price": 1450.5, "ltp": 1500.0}])}),
    req("holdings_empty", "/api/v1/holdings", {}, {HOLD: ok({"holdings": []})}),
    req("holdings_failed", "/api/v1/holdings", {}, {HOLD: FAILED}),
    req("holdings_html_answer", "/api/v1/holdings", {}, {HOLD: {"status": 200, "body": "<!doctype html><html></html>"}}),
    req("funds", "/api/v1/funds", {}, {FUNDS_EP: FUNDS}),
    req("funds_without_values", "/api/v1/funds", {}, {FUNDS_EP: ok({"values": []})}),
    req("funds_without_data", "/api/v1/funds", {}, {FUNDS_EP: ok({})}),
    req("funds_value_that_is_not_a_number", "/api/v1/funds", {}, {FUNDS_EP: ok({"values": [["Available Margin", "n/a"]]})}),
    req("funds_failed", "/api/v1/funds", {}, {FUNDS_EP: FAILED}),
    req("funds_http_error", "/api/v1/funds", {}, {FUNDS_EP: {"status": 500, "body": {"x": 1}}}),
    req("funds_not_json", "/api/v1/funds", {}, {FUNDS_EP: NOT_JSON}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {POSITIONS_EP: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {POSITIONS_EP: POSITIONS}),
    req("openposition_not_held_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "product": "NRML"}, {POSITIONS_EP: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {POSITIONS_EP: POSITIONS, ORD: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {POSITIONS_EP: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {POSITIONS_EP: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {POSITIONS_EP: POSITIONS, ORD: OK_ORDER}),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {POSITIONS_EP: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {POSITIONS_EP: FAILED}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's."""
    import importlib

    importlib.import_module("broker.pocketful.api.order_api")._POSITION_BOOK.invalidate("pocketful-access-token")
