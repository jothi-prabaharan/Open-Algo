"""Groww (api.groww.in): orders, books, positions, holdings and funds. The session token is a bearer token and every call carries X-API-VERSION 1.0. Prices are in rupees. A call answers {"status": "SUCCESS", "payload": ...} or {"status": "FAILURE", "error": {"code", "message"}}.
Orders and positions are read once for each segment (CASH, then FNO), the order list and the trades in pages. An order is placed with the broker symbol of the master contract, its exchange (NSE or BSE; NFO and BFO are NSE and BSE with segment FNO), a product, an order type (SL_M for SL-M) and a
generated reference id. The smart order reads positions strictly (a CASH read that fails is refused; an FNO one that fails refuses only an NFO or BFO symbol). The position book and funds add live prices from /v1/live-data/ltp."""

AUTH_TOKEN = "groww-access-token"
ENV = {"BROKER_API_KEY": "groww-api-key", "BROKER_API_SECRET": "groww-api-secret"}
SEED_API_KEY_ROW = True
READ_POSTS = []

CREATE, MODIFY, CANCEL = "POST /v1/order/create", "POST /v1/order/modify", "POST /v1/order/cancel"
OL, POS, HOLD, MARGIN, LTP = "GET /v1/order/list", "GET /v1/positions/user", "GET /v1/holdings/user", "GET /v1/margins/detail/user", "GET /v1/live-data/ltp"


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS", "BSE", "BSE", 500570, "TATA MOTORS"),
    _sym("BSEONLY", "BSEONLY", "BSE", "BSE", 500999, "BSE ONLY CO"),
    _sym("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", "NSE", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY26NOV24000CE", "NFO", "NSE", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", "BSE", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
]

PLACE = {"strategy": "My Strategy_v2", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def ok(payload):
    return {"body": {"status": "SUCCESS", "payload": payload}}


def fail(status=400, message="Bad request", code="GA001"):
    return {"status": status, "body": {"status": "FAILURE", "error": {"code": code, "message": message}}}


NOT_JSON = {"status": 200, "body": "<html>"}
THROTTLED = {"status": 429, "body": {}}
OK_ORDER = ok({"groww_order_id": "GMK39038RDS798", "order_status": "OPEN", "order_reference_id": "Ab-654321234-1628190", "remark": "Order placed successfully"})


def place(name, request, reply=None):
    return req(name, "/api/v1/placeorder", request, {CREATE: reply or OK_ORDER})


def order(oid, symbol, side, qty, otype="LIMIT", product="MIS", exchange="NSE", segment="CASH", status="OPEN", price=0, trigger=0, filled=0, **extra):
    o = {"groww_order_id": oid, "trading_symbol": symbol, "order_status": status, "remark": "", "quantity": qty, "price": price, "trigger_price": trigger, "filled_quantity": filled, "remaining_quantity": qty - filled,
         "average_fill_price": 0, "deliverable_quantity": 0, "amo_status": "NA", "validity": "DAY", "exchange": exchange, "order_type": otype, "transaction_type": side, "segment": segment, "product": product,
         "created_at": "2026-11-03T10:15:30", "exchange_time": "2026-11-03T10:15:31", "trade_date": "2026-11-03", "order_reference_id": "ref-" + oid}
    o.update(extra)
    return o


CASH_ORDERS = [order("GM1", "SBIN", "BUY", 10, price=800.5), order("GM2", "INFY", "SELL", 5, "MARKET", "CNC", status="EXECUTED", filled=5, average_fill_price=1499.95),
               order("GM3", "TATAMOTORS", "BUY", 2, "LIMIT", "CNC", "BSE", status="CANCELLED", price=489), order("GM4", "SBIN", "SELL", 1, "SL", status="TRIGGER_PENDING", price=790, trigger=791.25),
               order("GM5", "SBIN", "BUY", 3, "SL_M", status="REJECTED", trigger=805), order("GM6", "SBIN", "BUY", 6, status="NEW", price=799)]
FNO_ORDERS = [order("GF1", "NIFTY26NOVFUT", "BUY", 75, "LIMIT", "NRML", "NSE", "FNO", "ACKED", price=24005), order("GF2", "SENSEX26NOVFUT", "SELL", 20, "MARKET", "NRML", "BSE", "FNO", "EXECUTED", filled=20),
              order("GF3", "NIFTY26NOV24000CE", "BUY", 75, "LIMIT", "MIS", "NSE", "FNO", "FAILED", price=100.5), order("GF4", "NIFTY26NOVFUT", "BUY", 75, "LIMIT", "NRML", "NSE", "FNO", "CANCELLATION_REQUESTED", price=24000)]


def lst(rows):
    return ok({"order_list": rows})


BOOKS = {OL: [lst(CASH_ORDERS), lst(FNO_ORDERS)]}
WORKING = {OL: [lst([CASH_ORDERS[0], CASH_ORDERS[3], CASH_ORDERS[5], CASH_ORDERS[1]]), lst([FNO_ORDERS[0]])] * 8}
EMPTY = {OL: [lst([]), lst([])]}


def trade(tid, oid, symbol, side, qty, price, exchange="NSE", segment="CASH", product="MIS"):
    return {"groww_trade_id": tid, "groww_order_id": oid, "exchange_trade_id": "X" + tid, "exchange_order_id": "XO" + oid, "trading_symbol": symbol, "quantity": qty, "price": price, "trade_status": "EXECUTED", "exchange": exchange,
            "segment": segment, "product": product, "transaction_type": side, "created_at": "2026-11-03T10:21:12", "trade_date_time": "2026-11-03T10:21:12", "settlement_number": "2026215", "remark": None}


def trades(rows):
    return ok({"trade_list": rows})


def position(symbol, net, avg, exchange="NSE", segment="CASH", product="MIS", **extra):
    p = {"trading_symbol": symbol, "exchange": exchange, "segment": segment, "product": product, "quantity": net, "net_price": avg, "credit_quantity": max(net, 0) + 2, "debit_quantity": max(-net, 0) + 2, "carry_forward_credit_quantity": 0,
         "carry_forward_debit_quantity": 0, "credit_price": avg, "debit_price": avg + 1.5 if isinstance(avg, (int, float)) else avg, "realised_pnl": 12.5, "symbol_isin": "INE062A01020"}
    p.update(extra)
    return p


def positions(rows):
    return ok({"positions": rows})


CASH_POS = [position("SBIN", 10, 800.0), position("INFY", 0, 0, product="CNC"), position("TATAMOTORS", -4, 500.0, "BSE", product="CNC")]
FNO_POS = [position("NIFTY26NOVFUT", -75, 24000.0, "NSE", "FNO", "NRML"), position("SENSEX26NOVFUT", 20, 80000.0, "BSE", "FNO", "NRML")]
POSITIONS = {POS: [positions(CASH_POS), positions(FNO_POS)] * 6, LTP: [ok({"NSE_SBIN": 805.25, "BSE_TATAMOTORS": 498.1}), ok({"NSE_NIFTY26NOVFUT": 24010.5, "BSE_SENSEX26NOVFUT": 80100})]}
FLAT = {POS: [positions([position("SBIN", 0, 0)]), positions([])] * 6}
NO_POSITIONS = {POS: [{"status": 404, "body": {"status": "FAILURE", "error": {"code": "GA003", "message": "No positions found"}}}, positions([])] * 6}


def cancel(name, oid, reply, book=None):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": oid}, {**(book or BOOKS), CANCEL: reply})


CHANGE = {"strategy": "t", "orderid": "GM1", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "product": "MIS", "pricetype": "LIMIT", "price": "801.25", "quantity": "12", "trigger_price": "0", "disclosed_quantity": "0"}
OK_CANCEL = ok({"groww_order_id": "GM1", "order_status": "CANCELLATION_REQUESTED"})
OK_MODIFY = ok({"groww_order_id": "GM1", "order_status": "MODIFICATION_REQUESTED"})


def holding(symbol, qty, avg, **extra):
    h = {"isin": "INE062A01020", "trading_symbol": symbol, "quantity": qty, "average_price": avg, "pledge_quantity": 0, "demat_locked_quantity": 0, "groww_locked_quantity": 0, "repledge_quantity": 0, "t1_quantity": 0, "demat_free_quantity": qty, "corporate_action_additional_quantity": 0, "active_demat_transfer_quantity": 0}
    h.update(extra)
    return h


HOLDINGS = {HOLD: ok({"holdings": [holding("SBIN", 10, 700.0, groww_locked_quantity=1, demat_locked_quantity=2, t1_quantity=3, pledge_quantity=4), holding("INFY", 5, 1500.0), holding("BSEONLY", 3, 90.0), holding("UNLISTED", 2, 10.0)]}),
            LTP: ok({"NSE_SBIN": 805.25, "NSE_INFY": 1499.5, "BSE_BSEONLY": 95.5})}
FUNDS_BODY = {"clear_cash": 125000.5, "net_margin_used": 15000.25, "brokerage_and_charges": 20, "collateral_used": 0, "collateral_available": 5000.75, "adhoc_margin": 0, "net_fno_margin_used": 0, "span_margin_used": 0, "exposure_margin_used": 0, "future_balance_available": 0,
              "option_buy_balance_available": 0, "option_sell_balance_available": 0, "equity_margin_details": {"cnc_margin_used": 0, "mis_margin_used": 0, "cnc_balance_available": 0, "mis_balance_available": 0}}
FUNDS = {MARGIN: ok(FUNDS_BODY), **POSITIONS}


def smart(name, size, broker, qty="0", **extra):
    return req(name, "/api/v1/placesmartorder", {**PLACE, "quantity": qty, "position_size": size, **extra}, broker)


SCENARIOS = [
    place("placeorder_limit_mis", PLACE),
    place("placeorder_cnc", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market_has_no_price", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_stop_limit_buy", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_limit_sell", {**PLACE, "action": "SELL", "pricetype": "SL", "price": "790", "trigger_price": "791.25"}),
    place("placeorder_stop_market", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_limit_with_no_trigger", {**PLACE, "pricetype": "SL", "trigger_price": "0"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_price_is_not_rounded", {**PLACE, "price": "800.555"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_an_option", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "price": "100.5"}),
    place("placeorder_mcx_is_refused", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    place("placeorder_quantity_is_not_a_number", {**PLACE, "quantity": "abc"}),
    place("placeorder_quantity_zero", {**PLACE, "quantity": "0"}),
    place("placeorder_price_is_not_a_number", {**PLACE, "price": "abc"}),
    place("placeorder_price_type_unknown", {**PLACE, "pricetype": "ICEBERG"}),
    place("placeorder_product_unknown", {**PLACE, "product": "BO"}),
    place("placeorder_action_unknown", {**PLACE, "action": "HOLD"}),
    place("placeorder_failed_straight_away", PLACE, ok({"groww_order_id": "GM9", "order_status": "FAILED", "remark": "Trigger price out of range"})),
    place("placeorder_rejected_straight_away_with_no_remark", PLACE, ok({"groww_order_id": "GM9", "order_status": "REJECTED"})),
    place("placeorder_reply_with_no_order_id", PLACE, ok({"order_status": "OPEN"})),
    place("placeorder_reply_with_no_payload", PLACE, {"body": {"status": "SUCCESS"}}),
    place("placeorder_reply_with_failure_status", PLACE, {"body": {"status": "FAILURE", "error": {"code": "GA999", "message": "Order rejected"}}}),
    place("placeorder_refused_with_an_error", PLACE, fail(400, "Insufficient funds", "GA005")),
    place("placeorder_refused_with_a_message", PLACE, {"status": 400, "body": {"message": "Bad quantity"}}),
    place("placeorder_refused_with_no_reason", PLACE, {"status": 400, "body": {}}),
    place("placeorder_http_error_with_no_body", PLACE, {"status": 500, "body": ""}),
    place("placeorder_unauthorised", PLACE, fail(401, "Invalid token", "GA002")),
    place("placeorder_throttled_then_placed", PLACE, [THROTTLED, OK_ORDER]),
    place("placeorder_throttled_every_time", PLACE, THROTTLED),
    place("placeorder_not_json", PLACE, NOT_JSON),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MODIFY: OK_MODIFY}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "798", "trigger_price": "799"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_stop_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "price": "0", "trigger_price": "799"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_on_a_future", "/api/v1/modifyorder", {**CHANGE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "price": "24010"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_quantity_zero", "/api/v1/modifyorder", {**CHANGE, "quantity": "0"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_quantity_is_not_a_number", "/api/v1/modifyorder", {**CHANGE, "quantity": "abc"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_price_is_not_a_number", "/api/v1/modifyorder", {**CHANGE, "price": "abc"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_price_type_unknown", "/api/v1/modifyorder", {**CHANGE, "pricetype": "ICEBERG"}, {MODIFY: OK_MODIFY}),
    req("modifyorder_refused_with_an_error", "/api/v1/modifyorder", CHANGE, {MODIFY: fail(400, "Order is not open", "GA010")}),
    req("modifyorder_refused_with_no_reason", "/api/v1/modifyorder", CHANGE, {MODIFY: {"status": 400, "body": {}}}),
    req("modifyorder_failure_status_in_a_200", "/api/v1/modifyorder", CHANGE, {MODIFY: {"body": {"status": "FAILURE", "error": {"message": "Not modifiable"}}}}),
    req("modifyorder_throttled_every_time", "/api/v1/modifyorder", CHANGE, {MODIFY: THROTTLED}),
    req("modifyorder_not_json", "/api/v1/modifyorder", CHANGE, {MODIFY: NOT_JSON}),
    cancel("cancelorder_cash", "GM1", OK_CANCEL),
    cancel("cancelorder_future", "GF1", OK_CANCEL),
    cancel("cancelorder_that_is_cancelled_at_once", "GM1", ok({"groww_order_id": "GM1", "order_status": "CANCELLED"})),
    cancel("cancelorder_not_in_the_book_tries_both_segments", "GZZ", [fail(400, "Order not found", "GA011"), OK_CANCEL]),
    cancel("cancelorder_not_in_the_book_and_refused_twice", "GZZ", fail(400, "Order not found", "GA011")),
    cancel("cancelorder_with_the_book_unreadable_tries_both", "GM1", [fail(400, "Wrong segment", "GA012"), OK_CANCEL], {OL: fail(500, "Down", "GA500")}),
    cancel("cancelorder_refused_with_an_error", "GM1", fail(400, "Order already complete", "GA013")),
    cancel("cancelorder_refused_with_no_reason", "GM1", {"status": 400, "body": {}}),
    cancel("cancelorder_http_error_with_no_body", "GM1", {"status": 500, "body": ""}),
    cancel("cancelorder_throttled_every_time", "GM1", THROTTLED),
    cancel("cancelorder_not_json", "GM1", NOT_JSON),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {**WORKING, CANCEL: OK_CANCEL}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {**WORKING, CANCEL: [OK_CANCEL, fail(400, "Already done", "GA014"), OK_CANCEL, OK_CANCEL, OK_CANCEL]}),
    req("cancelallorder_nothing_working", "/api/v1/cancelallorder", {"strategy": "t"}, {OL: [lst([CASH_ORDERS[1]]), lst([FNO_ORDERS[1]])]}),
    req("cancelallorder_empty_book", "/api/v1/cancelallorder", {"strategy": "t"}, EMPTY),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OL: fail(500, "Down", "GA500")}),
    req("orderbook", "/api/v1/orderbook", {}, BOOKS),
    req("orderbook_empty", "/api/v1/orderbook", {}, EMPTY),
    req("orderbook_second_page", "/api/v1/orderbook", {}, {OL: [lst([order(f"GP{i}", "SBIN", "BUY", 1) for i in range(100)]), lst([order("GP100", "SBIN", "BUY", 1)]), lst([])]}),
    req("orderbook_status_unknown", "/api/v1/orderbook", {}, {OL: [lst([order("GM7", "SBIN", "BUY", 1, status="WEIRD")]), lst([])]}),
    req("orderbook_order_type_unknown_and_no_symbol", "/api/v1/orderbook", {}, {OL: [lst([order("GM8", "UNLISTED", "BUY", 1, "ICEBERG"), {"groww_order_id": "GM9"}]), lst([])]}),
    req("orderbook_fno_failed_is_skipped", "/api/v1/orderbook", {}, {OL: [lst(CASH_ORDERS), fail(400, "No F&O account", "GA020")]}),
    req("orderbook_fno_with_no_positions_message_is_skipped", "/api/v1/orderbook", {}, {OL: [lst(CASH_ORDERS), fail(404, "No data found", "GA021")]}),
    req("orderbook_cash_failed", "/api/v1/orderbook", {}, {OL: [fail(500, "Down", "GA500"), lst(FNO_ORDERS)]}),
    req("orderbook_cash_has_no_data_message", "/api/v1/orderbook", {}, {OL: [fail(404, "No data found", "GA021"), lst(FNO_ORDERS)]}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OL: NOT_JSON}),
    req("orderbook_http_error_with_no_body", "/api/v1/orderbook", {}, {OL: {"status": 500, "body": ""}}),
    req("orderbook_throttled_then_read", "/api/v1/orderbook", {}, {OL: [THROTTLED, lst(CASH_ORDERS), lst(FNO_ORDERS)]}),
    req("orderbook_throttled_every_time", "/api/v1/orderbook", {}, {OL: THROTTLED}),
    req("tradebook", "/api/v1/tradebook", {}, {**BOOKS, "GET /v1/order/trades/GM2": trades([trade("T1", "GM2", "INFY", "SELL", 3, 1499.9, product="CNC"), trade("T2", "GM2", "INFY", "SELL", 2, 1500.05, product="CNC")]),
                                                              "GET /v1/order/trades/GF2": trades([trade("T3", "GF2", "SENSEX26NOVFUT", "SELL", 20, 80010.5, "BSE", "FNO", "NRML")])}),
    req("tradebook_no_fills", "/api/v1/tradebook", {}, {OL: [lst([CASH_ORDERS[0]]), lst([])]}),
    req("tradebook_a_trades_read_failed", "/api/v1/tradebook", {}, {**BOOKS, "GET /v1/order/trades/GM2": fail(500, "Down", "GA500"), "GET /v1/order/trades/GF2": trades([])}),
    req("tradebook_trades_with_a_second_page", "/api/v1/tradebook", {}, {OL: [lst([CASH_ORDERS[1]]), lst([])], "GET /v1/order/trades/GM2": [trades([trade(f"TP{i}", "GM2", "INFY", "SELL", 1, 1500, product="CNC") for i in range(50)]), trades([trade("TP50", "GM2", "INFY", "SELL", 1, 1500, product="CNC")])]}),
    req("tradebook_order_book_failed", "/api/v1/tradebook", {}, {OL: fail(500, "Down", "GA500")}),
    req("tradebook_trade_of_an_unlisted_symbol", "/api/v1/tradebook", {}, {OL: [lst([CASH_ORDERS[1]]), lst([])], "GET /v1/order/trades/GM2": trades([trade("T9", "GM2", "UNLISTED", "BUY", 1, 10.0, product="CNC")])}),
    req("positionbook", "/api/v1/positionbook", {}, POSITIONS),
    req("positionbook_flat", "/api/v1/positionbook", {}, FLAT),
    req("positionbook_ltp_refused", "/api/v1/positionbook", {}, {POS: [positions(CASH_POS), positions(FNO_POS)], LTP: fail(500, "Down", "GA500")}),
    req("positionbook_ltp_missing_for_one", "/api/v1/positionbook", {}, {POS: [positions(CASH_POS), positions(FNO_POS)], LTP: [ok({"NSE_SBIN": 805.25}), ok({})]}),
    req("positionbook_fno_failed_is_skipped", "/api/v1/positionbook", {}, {POS: [positions(CASH_POS), fail(400, "No F&O account", "GA020")], LTP: ok({"NSE_SBIN": 805.25, "BSE_TATAMOTORS": 498.1})}),
    req("positionbook_no_positions_message", "/api/v1/positionbook", {}, NO_POSITIONS),
    req("positionbook_prices_that_are_not_prices", "/api/v1/positionbook", {}, {POS: [positions([position("SBIN", 10, "abc", credit_price=None, debit_price="nan")]), positions([])], LTP: ok({"NSE_SBIN": 805.25})}),
    req("positionbook_carry_forward_quantities", "/api/v1/positionbook", {}, {POS: [positions([position("SBIN", 5, 800.0, carry_forward_credit_quantity=4, carry_forward_debit_quantity=1, debit_price=0)]), positions([])], LTP: ok({"NSE_SBIN": 805.25})}),
    req("positionbook_not_json", "/api/v1/positionbook", {}, {POS: NOT_JSON}),
    req("holdings", "/api/v1/holdings", {}, HOLDINGS),
    req("holdings_empty", "/api/v1/holdings", {}, {HOLD: ok({"holdings": []})}),
    req("holdings_ltp_refused", "/api/v1/holdings", {}, {HOLD: HOLDINGS[HOLD], LTP: fail(500, "Down", "GA500")}),
    req("holdings_no_payload", "/api/v1/holdings", {}, {HOLD: {"body": {"status": "SUCCESS"}}}),
    req("holdings_failure_status", "/api/v1/holdings", {}, {HOLD: {"body": {"status": "FAILURE", "error": {"message": "Bad"}}}}),
    req("holdings_http_error", "/api/v1/holdings", {}, {HOLD: fail(500, "Down", "GA500")}),
    req("holdings_not_json", "/api/v1/holdings", {}, {HOLD: NOT_JSON}),
    req("holdings_values_that_are_not_numbers", "/api/v1/holdings", {}, {HOLD: ok({"holdings": [holding("SBIN", "abc", "x", demat_locked_quantity=None)]}), LTP: ok({"NSE_SBIN": 805.25})}),
    req("holdings_zero_average_price", "/api/v1/holdings", {}, {HOLD: ok({"holdings": [holding("SBIN", 10, 0)]}), LTP: ok({"NSE_SBIN": 805.25})}),
    req("funds", "/api/v1/funds", {}, FUNDS),
    req("funds_positions_unreadable", "/api/v1/funds", {}, {MARGIN: ok(FUNDS_BODY), POS: fail(500, "Down", "GA500")}),
    req("funds_fno_unreadable", "/api/v1/funds", {}, {MARGIN: ok(FUNDS_BODY), POS: [positions(CASH_POS), fail(400, "No F&O account", "GA020")], LTP: ok({"NSE_SBIN": 805.25})}),
    req("funds_an_open_position_with_no_price", "/api/v1/funds", {}, {MARGIN: ok(FUNDS_BODY), POS: [positions(CASH_POS), positions([])], LTP: ok({})}),
    req("funds_empty_payload", "/api/v1/funds", {}, {MARGIN: {"body": {"status": "SUCCESS", "payload": {}}}}),
    req("funds_failure_status", "/api/v1/funds", {}, {MARGIN: {"body": {"status": "FAILURE", "error": {"message": "Bad"}}}}),
    req("funds_http_error", "/api/v1/funds", {}, {MARGIN: fail(500, "Down", "GA500")}),
    req("funds_not_json", "/api/v1/funds", {}, {MARGIN: NOT_JSON}),
    req("funds_missing_figures_are_zero", "/api/v1/funds", {}, {MARGIN: ok({"clear_cash": 10}), POS: [positions([]), positions([])]}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, POSITIONS),
    req("openposition_short_in_a_future", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, POSITIONS),
    req("openposition_not_held_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, POSITIONS),
    req("openposition_cash_book_failed", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {POS: fail(500, "Down", "GA500")}),
    req("openposition_fno_failed_for_a_cash_symbol", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {POS: [positions(CASH_POS), fail(400, "No F&O", "GA020")] * 6}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {**POSITIONS, CREATE: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, FLAT),
    req("closeposition_no_positions_message", "/api/v1/closeposition", {"strategy": "t"}, NO_POSITIONS),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {POS: fail(500, "Down", "GA500")}),
    req("closeposition_an_order_refused", "/api/v1/closeposition", {"strategy": "t"}, {POS: [positions([position("SBIN", 10, 800.0)]), positions([])], LTP: ok({}), CREATE: fail(400, "No margin", "GA030")}),
    req("closeposition_an_unlisted_symbol", "/api/v1/closeposition", {"strategy": "t"}, {POS: [positions([position("UNLISTED", 10, 10.0)]), positions([])], CREATE: OK_ORDER}),
    smart("placesmartorder_open_a_position", "5", {POS: FLAT[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_short_a_position", "-5", {POS: FLAT[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_reduce", "4", {POS: POSITIONS[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_increase", "15", {POS: POSITIONS[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_flip_from_long_to_short", "-3", {POS: POSITIONS[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_close_a_long", "0", {POS: POSITIONS[POS], CREATE: OK_ORDER}),
    smart("placesmartorder_close_a_short", "0", {POS: POSITIONS[POS], CREATE: OK_ORDER}, symbol="TATAMOTORS", exchange="BSE", product="CNC"),
    smart("placesmartorder_fresh_order_when_flat", "0", {POS: FLAT[POS], CREATE: OK_ORDER}, qty="10"),
    smart("placesmartorder_already_matched", "10", {POS: POSITIONS[POS]}),
    smart("placesmartorder_nothing_to_close", "0", {POS: FLAT[POS]}),
    smart("placesmartorder_a_future", "-75", {POS: POSITIONS[POS], CREATE: OK_ORDER}, symbol="NIFTY25NOV26FUT", exchange="NFO", product="NRML", price="24005"),
    smart("placesmartorder_cash_book_failed_is_refused", "4", {POS: fail(500, "Down", "GA500")}),
    smart("placesmartorder_fno_failed_still_trades_cash", "4", {POS: [positions(CASH_POS), fail(400, "No F&O", "GA020")] * 6, CREATE: OK_ORDER}),
    smart("placesmartorder_order_refused", "4", {POS: POSITIONS[POS], CREATE: fail(400, "No margin", "GA030")}),
    smart("placesmartorder_with_no_positions_message", "5", {**NO_POSITIONS, CREATE: OK_ORDER}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's. Groww paces its calls and waits after a 429, which a recording has no use for."""
    import importlib

    importlib.import_module("broker.groww.api.order_api")._position_cache.invalidate("groww-access-token")
    importlib.import_module("broker.groww.api.rate_limiter").time.sleep = lambda *a, **k: None
