"""IndMoney (INDstocks, api.indstocks.com): orders, books, funds. The access token is the whole Authorization header. A reply is {status: success, data} or {status: failure, error: {msg}} or {status: error, message}. A stop order goes to the smart order endpoints as a TRIGGER order (NSE only).
The position book is four queries (derivative and equity, by product) whose rows the plugin joins, and the trade book is two (equity, derivative) enriched from the order book."""

AUTH_TOKEN = "ind-access-token"
ENV = {"BROKER_API_KEY": "ind-client-id"}
SEED_API_KEY_ROW = True
READ_POSTS = []


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY-25NOV2026-FUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("SENSEX26NOVFUT", "SENSEX-26NOV2026-FUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM-25NOV2026-FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("NIFTY", "NIFTY", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
ORDER_EP, SMART_EP = "POST /order", "POST /smart/order"
MODIFY_EP, SMART_MODIFY_EP = "POST /order/modify", "POST /smart/order/modify"
CANCEL_EP, SMART_CANCEL_EP = "POST /order/cancel", "POST /smart/order/cancel"
OB, TB, PB, HB, FB = "GET /order-book", "GET /trade-book", "GET /portfolio/positions", "GET /portfolio/holdings", "GET /funds"
LTP = "GET /market/quotes/ltp"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def ok(data=None, **extra):
    return {"body": {"status": "success", "data": data if data is not None else {}, **extra}}


OK_ORDER = ok({"order_id": "EQ-1001"})
FAIL = {"body": {"status": "failure", "error": {"code": "E1", "msg": "RMS: insufficient funds"}}}
NOT_JSON = {"status": 200, "body": "<html>"}


def place(name, request, reply=None, extra=None, endpoint=ORDER_EP):
    return req(name, "/api/v1/placeorder", request, {endpoint: reply or OK_ORDER, **(extra or {})})


def order(order_id, token, txn, qty, otype, product, status, exchange="NSE", segment="EQUITY", **extra):
    o = {"id": order_id, "security_id": str(token), "exchange": exchange, "segment": segment, "txn_type": txn, "requested_qty": qty, "requested_price": "", "sl_trigger_price": "", "order_type": otype, "product": product,
         "status": status, "created_at": "2026-11-03T10:20:01+05:30", "name": "NAME", "exch_order_id": "X" + order_id}
    o.update(extra)
    return o


ORDERS = ok([
    order("EQ-1", 3045, "BUY", 10, "LIMIT", "INTRADAY", "PENDING", requested_price="800.5"),
    order("DRV-2", 49500, "BUY", 75, "TRIGGER", "MARGIN", "SL-PENDING", segment="DERIVATIVE", sl_trigger_price="24000", requested_price="24010"),
    order("EQ-3", 1594, "SELL", 5, "LIMIT", "CNC", "SUCCESS", requested_price="1495"),
    order("EQ-4", 3045, "SELL", 1, "MARKET", "INTRADAY", "REJECTED"),
    order("EQ-5", 500570, "BUY", 2, "STOP_LOSS", "CNC", "CANCELLED", exchange="BSE", sl_trigger_price="490", sl_limit_price="489"),
    order("DRV-6", 60001, "BUY", 20, "LIMIT", "MARGIN", "MODIFIED", exchange="BSE", segment="DERIVATIVE", requested_price="80000"),
    order("EQ-7", 3045, "BUY", 3, "GTT_LIMIT", "INTRADAY", "QUEUED", tgt_limit_price="810"),
    order("EQ-8", 999999, "BUY", 3, "XX", "ZZ", "SOMETHING ELSE", name="NOSUCH"),
    order("EQ-9", 3045, "SELL", 4, "OCO", "CNC", "PARTIALLY FILLED - CANCELLED"),
    order("EQ-10", 3045, "BUY", 1, "LIMIT", "INTRADAY", "EXPIRED", requested_price="bad"),
    order("EQ-11", None, "BUY", 1, "LIMIT", "INTRADAY", "PENDING", security_id=None, name="NOTOKEN"),
    order("EQ-12", 3045, "SELL", 6, "STOP_LOSS_MARKET", "INTRADAY", "SL-PENDING", sl_trigger_price="790.5"),
])
CANCEL_BOOK = ok([
    order("EQ-1", 3045, "BUY", 10, "LIMIT", "INTRADAY", "PENDING"),
    order("DRV-2", 49500, "BUY", 75, "TRIGGER", "MARGIN", "SL-PENDING", segment="DERIVATIVE"),
    order("EQ-3", 1594, "SELL", 5, "LIMIT", "CNC", "SUCCESS"),
    order("GTT-4", 3045, "SELL", 1, "GTT_LIMIT", "INTRADAY", "QUEUED"),
    order("EQ-5", 3045, "SELL", 1, "MARKET", "INTRADAY", "PROCESSING"),
])
EMPTY = ok([])
NULL_DATA = {"body": {"status": "success", "data": None}}
FAILED = {"body": {"status": "failure", "error": {"msg": "Session expired"}}}
FAILED_MSG = {"body": {"status": "error", "message": "Token invalid"}}


def fill(fill_id, exch_order_id, token, qty, price, **extra):
    t = {"fill_id": fill_id, "exch_order_id": exch_order_id, "scrip_code": str(token), "quantity": qty, "price": price, "trade_date": "2026-11-03T10:21:12+05:30", "trade_serial_no": fill_id}
    t.update(extra)
    return t


EQ_TRADES = ok([fill("F1", "XEQ-3", 1594, 5, 1499.95), fill("F2", "XEQ-1", 3045, 10, 800.25), fill("F3", "NOORDER", 3045, 2, 801), fill("F4", "XEQ-8", 999999, 2, 7.5)])
DRV_TRADES = ok([fill("F5", "XDRV-2", 49500, 75, 24005.5)])
POS_DERIVATIVE_MARGIN = ok([{"security_id": "49500", "symbol": "NIFTY25NOV26FUT", "net_qty": -75, "average_price": 24000, "segment": "DERIVATIVE", "exchange": "NSE", "realized_profit": 0}])
POS_DERIVATIVE_INTRADAY = ok([])
POS_EQUITY_CNC = ok([{"security_id": "1594", "symbol": "INFY", "net_qty": 0, "average_price": 1500, "segment": "EQUITY", "exchange": "NSE"}, {"security_id": "500570", "symbol": "TATAMOTORS", "net_qty": 3, "average_price": 500, "segment": "EQUITY", "exchange": "BSE"}])
POS_EQUITY_INTRADAY = ok([{"security_id": "3045", "symbol": "SBIN", "net_qty": 10, "average_price": 800, "segment": "EQUITY", "exchange": "NSE", "realized_profit": 5.5}, {"security_id": "999999", "symbol": "NOSUCH", "net_qty": 4, "average_price": 10, "segment": "EQUITY", "exchange": "NSE"}])
POSITIONS = [POS_DERIVATIVE_MARGIN, POS_DERIVATIVE_INTRADAY, POS_EQUITY_CNC, POS_EQUITY_INTRADAY]
LTP_REPLY = ok({"NSE_3045": {"live_price": 805.5}, "NFO_49500": {"live_price": 24100.25}, "BSE_500570": {"live_price": 510}})
HOLDINGS = ok([
    {"security_id": "1594", "symbol": "INFY", "total_qty": 6, "avg_price": 1450.5, "isin": "INE009A01021"},
    {"security_id": "500570", "symbol": "TATAMOTORS", "total_qty": 4, "avg_price": 500},
    {"security_id": "999999", "symbol": "NOSUCH", "total_qty": 3, "avg_price": 12.25},
])
FUNDS = ok({"sod_balance": 100000, "withdrawal_balance": 90000.5, "pledge_received": 2500.25, "realized_pnl": 340.25, "unrealized_pnl": -120.5, "detailed_avl_balance": {"eq_cnc": 95000.75, "eq_mis": 80000}})
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "EQ-1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = ok({"order_id": "EQ-1"})


def cancel(name, order_id, replies, **extra):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": order_id}, replies, **extra)


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_is_native", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit_is_a_smart_trigger_order", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, endpoint=SMART_EP),
    place("placeorder_stop_limit_without_a_limit_price", {**PLACE, "pricetype": "SL", "price": "0", "trigger_price": "801.5"}, endpoint=SMART_EP),
    place("placeorder_stop_market_is_a_smart_trigger_order", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, endpoint=SMART_EP),
    place("placeorder_stop_sell", {**PLACE, "action": "SELL", "pricetype": "SL-M", "price": "0", "trigger_price": "790"}, endpoint=SMART_EP),
    place("placeorder_stop_without_a_trigger_is_refused", {**PLACE, "pricetype": "SL", "trigger_price": "0"}),
    place("placeorder_stop_on_bse_is_refused", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "pricetype": "SL", "price": "500", "trigger_price": "499"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_is_refused", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_reply_with_the_smart_order_shape", PLACE, ok({"order_data": [{"order_id": "EQ-2002", "child_order_details": {"order_id": "EQ-2003"}}]})),
    place("placeorder_reply_with_a_smart_order_without_a_child", PLACE, ok({"order_data": [{"order_id": "EQ-2004"}]})),
    place("placeorder_reply_with_no_order_id", PLACE, ok({})),
    place("placeorder_reply_with_data_that_is_not_an_object", PLACE, ok("fine")),
    place("placeorder_refused_with_failure_and_a_message", PLACE, FAIL),
    place("placeorder_refused_with_error_and_a_message", PLACE, {"body": {"status": "error", "message": "Insufficient margin"}}),
    place("placeorder_no_order_number_in_the_answer_is_read_as_placed", PLACE, {"body": {"status": "failure", "error": {"msg": "No order number in RS response"}}}),
    place("placeorder_refused_with_http_400", PLACE, {"status": 400, "body": {"status": "error", "message": "Invalid quantity"}}),
    place("placeorder_refused_with_http_429_is_not_retried", PLACE, {"status": 429, "body": {"status": "error", "message": "Too many requests"}}),
    place("placeorder_refused_with_http_500", PLACE, {"status": 500, "body": {"x": 1}}),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: MOD_OK, OB: ORDERS}),
    req("modifyorder_a_derivative_order", "/api/v1/modifyorder", {**CHANGE, "orderid": "DRV-2", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "price": "24010"}, {SMART_MODIFY_EP: ok({"order_id": "DRV-2"}), OB: ORDERS}),
    req("modifyorder_a_smart_order_by_its_id", "/api/v1/modifyorder", {**CHANGE, "orderid": "GTT-4", "pricetype": "SL", "trigger_price": "790"}, {SMART_MODIFY_EP: ok({"order_id": "GTT-4"})}),
    req("modifyorder_a_smart_order_found_in_the_book", "/api/v1/modifyorder", {**CHANGE, "orderid": "DRV-2", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "pricetype": "SL", "quantity": "75", "price": "24010", "trigger_price": "24000"}, {SMART_MODIFY_EP: ok({"order_id": "DRV-2"}), OB: ORDERS}),
    req("modifyorder_a_smart_order_without_a_trigger", "/api/v1/modifyorder", {**CHANGE, "orderid": "GTT-4"}, {SMART_MODIFY_EP: ok({"order_id": "GTT-4"})}),
    req("modifyorder_refused_with_failure", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"body": {"status": "failure", "error": {"msg": "Order is not open"}}}, OB: ORDERS}),
    req("modifyorder_refused_with_error", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"body": {"status": "error", "message": "Bad price"}}, OB: ORDERS}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"body": {"status": "error"}}, OB: ORDERS}),
    req("modifyorder_refused_with_http_400", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"status": 400, "body": {"status": "error", "message": "Invalid"}}, OB: ORDERS}),
    req("modifyorder_not_json", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: NOT_JSON, OB: ORDERS}),
    req("modifyorder_unknown_symbol", "/api/v1/modifyorder", {**CHANGE, "symbol": "NOPE"}, {MODIFY_EP: MOD_OK, OB: ORDERS}),
    cancel("cancelorder", "EQ-1", {CANCEL_EP: ok({"order_id": "EQ-1"}), OB: ORDERS}),
    cancel("cancelorder_a_derivative_order", "DRV-6", {CANCEL_EP: ok({"order_id": "DRV-6"}), OB: ORDERS}),
    cancel("cancelorder_a_smart_order_by_its_id", "GTT-4", {SMART_CANCEL_EP: ok({"order_id": "GTT-4"})}),
    cancel("cancelorder_a_smart_order_found_in_the_book", "EQ-7", {SMART_CANCEL_EP: ok({"order_id": "EQ-7"}), OB: ORDERS}),
    cancel("cancelorder_a_stop_loss_order_in_the_book_is_a_regular_one", "EQ-12", {CANCEL_EP: ok({"order_id": "EQ-12"}), OB: ORDERS}),
    cancel("cancelorder_an_oco_order_found_in_the_book", "EQ-9", {SMART_CANCEL_EP: ok({}), OB: ORDERS}),
    cancel("cancelorder_with_the_book_unreadable_goes_to_the_regular_endpoint", "EQ-77", {CANCEL_EP: ok({}), OB: FAILED}),
    cancel("cancelorder_refused_with_failure", "EQ-3", {CANCEL_EP: {"body": {"status": "failure", "error": {"msg": "Order already complete"}}}, OB: ORDERS}),
    cancel("cancelorder_refused_with_error", "EQ-3", {CANCEL_EP: {"body": {"status": "error", "message": "No such order"}}, OB: ORDERS}),
    cancel("cancelorder_refused_with_http_404", "EQ-3", {CANCEL_EP: {"status": 404, "body": {"status": "error", "message": "Not found"}}, OB: ORDERS}),
    cancel("cancelorder_not_json", "EQ-3", {CANCEL_EP: NOT_JSON, OB: ORDERS}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCEL_EP: ok({}), SMART_CANCEL_EP: ok({})}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCEL_EP: [ok({}), {"body": {"status": "error", "message": "Already done"}}, ok({})], SMART_CANCEL_EP: ok({})}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY}),
    req("cancelallorder_nothing_pending", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ok([order("EQ-3", 1594, "SELL", 5, "LIMIT", "CNC", "SUCCESS")])}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY}),
    req("orderbook_null_data", "/api/v1/orderbook", {}, {OB: NULL_DATA}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("orderbook_failed_with_a_message", "/api/v1/orderbook", {}, {OB: FAILED_MSG}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: NOT_JSON}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": {"x": 1}}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: [EQ_TRADES, DRV_TRADES], OB: ORDERS}),
    req("tradebook_only_equity", "/api/v1/tradebook", {}, {TB: [EQ_TRADES, EMPTY], OB: ORDERS}),
    req("tradebook_equity_failed_derivative_read", "/api/v1/tradebook", {}, {TB: [FAILED, DRV_TRADES], OB: ORDERS}),
    req("tradebook_with_the_order_book_failed", "/api/v1/tradebook", {}, {TB: [EQ_TRADES, DRV_TRADES], OB: FAILED}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: [EMPTY, EMPTY], OB: EMPTY}),
    req("tradebook_both_failed", "/api/v1/tradebook", {}, {TB: [FAILED, FAILED], OB: ORDERS}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS, LTP: LTP_REPLY}),
    req("positionbook_without_prices", "/api/v1/positionbook", {}, {PB: POSITIONS, LTP: FAILED}),
    req("positionbook_one_query_failed", "/api/v1/positionbook", {}, {PB: [POS_DERIVATIVE_MARGIN, FAILED, POS_EQUITY_CNC, POS_EQUITY_INTRADAY], LTP: LTP_REPLY}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: [EMPTY, EMPTY, EMPTY, EMPTY]}),
    req("positionbook_net_and_day_positions_in_a_dict", "/api/v1/positionbook", {}, {PB: [ok({"net_positions": [{"security_id": "3045", "symbol": "SBIN", "net_qty": 5, "average_price": 800, "exchange": "NSE", "segment": "EQUITY"}], "day_positions": []}), EMPTY, EMPTY, EMPTY], LTP: LTP_REPLY}),
    req("positionbook_all_failed", "/api/v1/positionbook", {}, {PB: FAILED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: EMPTY}),
    req("holdings_null_data", "/api/v1/holdings", {}, {HB: NULL_DATA}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS}),
    req("funds_without_the_breakdown_uses_the_withdrawal_balance", "/api/v1/funds", {}, {FB: ok({"sod_balance": 100000, "withdrawal_balance": 90000.5, "pledge_received": 0, "realized_pnl": 0, "unrealized_pnl": 0})}),
    req("funds_with_a_breakdown_of_other_keys", "/api/v1/funds", {}, {FB: ok({"sod_balance": 100, "withdrawal_balance": 60, "detailed_avl_balance": {"fno": 7}})}),
    req("funds_the_mis_balance_when_there_is_no_cnc", "/api/v1/funds", {}, {FB: ok({"sod_balance": 100, "withdrawal_balance": 60, "detailed_avl_balance": {"eq_mis": 70}})}),
    req("funds_text_values", "/api/v1/funds", {}, {FB: ok({"sod_balance": "1,000.50", "withdrawal_balance": "n/a", "pledge_received": None, "realized_pnl": "12", "unrealized_pnl": ""})}),
    req("funds_more_used_than_available_is_zero", "/api/v1/funds", {}, {FB: ok({"sod_balance": 10, "withdrawal_balance": 90, "detailed_avl_balance": {"eq_cnc": 95}})}),
    req("funds_without_data", "/api/v1/funds", {}, {FB: ok({})}),
    req("funds_failed", "/api/v1/funds", {}, {FB: FAILED}),
    req("funds_http_error", "/api/v1/funds", {}, {FB: {"status": 500, "body": {"x": 1}}}),
    req("funds_not_json", "/api/v1/funds", {}, {FB: NOT_JSON}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_not_held_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "product": "NRML"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, ORDER_EP: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: [EMPTY, EMPTY, EMPTY, EMPTY]}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, ORDER_EP: OK_ORDER}),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: FAILED}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's. The rate limiter paces and counts for the whole process, which a recording has no use for."""
    import importlib

    importlib.import_module("broker.indmoney.api.order_api")._POSITION_BOOK.invalidate("ind-access-token")
    rl = importlib.import_module("broker.indmoney.api.rate_limiter")
    rl.time.sleep = lambda *a, **k: None
