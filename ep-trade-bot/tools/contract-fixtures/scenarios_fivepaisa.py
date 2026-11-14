"""5paisa (own REST API at Openapi.5paisa.com, not its XTS): orders, books and funds. The key packs the app key, user id and client code (key:::user:::client). Every call is a POST of {head: {key}, body}, the token as a bearer.
5paisa rejects an order that reads as at market, so a market order is a limit order a slab beyond the last price (from MarketSnapshot and MarketDepth) and a stop-market a stop-limit beyond its trigger."""

AUTH_TOKEN = "fivepaisa-access-token"
ENV = {"BROKER_API_KEY": "fp-app-key:::fp-user:::4001234", "BROKER_API_SECRET": "fp-secret"}
SEED_API_KEY_ROW = True
IGNORE = []
SVC = "/VendorsAPI/Service1.svc"


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN_EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY_EQ", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS_EQ", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY", "NIFTY", "NSE_INDEX", 999920000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "NIFTY 25 Nov 2026", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY 25 Nov 2026 CE 24000.00", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX 26 Nov 2026", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM 25 Nov 2026", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR 25 Nov 2026", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NOTICK", "NOTICK_EQ", "NSE", 4242, "NO TICK", 1, "EQ", "", -1.0, 0.0),
]

READ_POSTS = [f"{SVC}/V3/OrderBook", f"{SVC}/V1/TradeBook", f"{SVC}/V2/NetPositionNetWise", f"{SVC}/V3/Holding", f"{SVC}/V4/Margin", f"{SVC}/MarketSnapshot", f"{SVC}/V2/MarketDepth"]
PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, MOD_EP, CAN_EP = f"POST {SVC}/V1/PlaceOrderRequest", f"POST {SVC}/V1/ModifyOrderRequest", f"POST {SVC}/V1/CancelOrderRequest"
OB, TB, PB, HB, MB = f"POST {SVC}/V3/OrderBook", f"POST {SVC}/V1/TradeBook", f"POST {SVC}/V2/NetPositionNetWise", f"POST {SVC}/V3/Holding", f"POST {SVC}/V4/Margin"
SNAP, DEPTH = f"POST {SVC}/MarketSnapshot", f"POST {SVC}/V2/MarketDepth"


def _wrap(value):
    """A 5paisa answer carries its own head and body, so it goes in the harness's reply wrapper; a reply already shaped for the harness (a status and a body) goes as it is."""
    if isinstance(value, list):
        return [_wrap(v) for v in value]
    return {"body": value} if isinstance(value, dict) and "head" in value else value


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {k: _wrap(v) for k, v in (broker or {}).items()}, **extra}


def head(ok=True, text=None):
    return {"statusDescription": text or ("Success" if ok else "Failure"), "status": "0" if ok else "1"}


def snap(ltp=805.45, **extra):
    d = {"ScripCode": 3045, "LastTradedPrice": ltp, "Open": 801.0, "High": 809.5, "Low": 799.0, "PClose": 800.0, "Volume": 1234567, "OpenInterest": 0, "LastTradedQty": 25}
    d.update(extra)
    return {"body": {"head": None, "Data": [d]}, "head": head()}


def mdepth(buys=((805.4, 100), (805.35, 200)), sells=((805.5, 110), (805.55, 220))):
    rows = [{"BbBuySellFlag": 66, "Price": p, "Quantity": q} for p, q in buys] + [{"BbBuySellFlag": 83, "Price": p, "Quantity": q} for p, q in sells]
    return {"head": head(), "body": {"MarketDepthData": rows}}


def ok_order(number=1234567):
    return {"head": head(), "body": {"Status": 0, "BrokerOrderID": number, "Message": "Success", "RMSResponseCode": 1}}


def rejected(message="Insufficient funds", status=1):
    return {"head": head(), "body": {"Status": status, "BrokerOrderID": 0, "Message": message, "RMSResponseCode": 0}}


def place(name, request, reply=None, extra=None):
    broker = {PLACE_EP: reply or ok_order(), **(extra or {})}
    return req(name, "/api/v1/placeorder", request, broker, ignore=IGNORE)


def date(epoch_ms=1793591100000, offset="+0530"):
    return f"/Date({epoch_ms}{offset})/"


def order(broker_id, exch_id, symbol_token, exch, exch_type, side, qty, rate, trigger, at_market, delv, status, **extra):
    o = {"BrokerOrderId": broker_id, "ExchOrderID": exch_id, "ScripCode": symbol_token, "Exch": exch, "ExchType": exch_type, "ScripName": "x", "BuySell": side, "Qty": qty, "Rate": rate, "SLTriggerRate": trigger,
         "AtMarket": at_market, "DelvIntra": delv, "OrderStatus": status, "BrokerOrderTime": date(), "Reason": "", "TradedQty": 0, "PendingQty": qty}
    o.update(extra)
    return o


ORDERS = {"head": head(), "body": {"OrderBookDetail": [
    order(1001, "5000001", 3045, "N", "C", "B", 10, 800.5, 0, "N", "I", "Pending"),
    order(1002, "5000002", 49500, "N", "D", "B", 75, 24010.0, 24000.0, "N", "D", "Modified"),
    order(1003, "5000003", 1594, "N", "C", "S", 5, 1495.0, 0, "N", "D", "Fully Executed", TradedQty=5, PendingQty=0),
    order(1004, "5000004", 3045, "N", "C", "S", 1, 0.0, 790.5, "Y", "I", "Rejected by Exch", Reason="Price out of range"),
    order(1005, "", 500570, "B", "C", "B", 2, 0.0, 0, "Y", "D", "Cancelled"),
    order(1006, "5000006", 90001, "N", "U", "B", 1000, 83.5, 0, "N", "D", "Pending"),
    order(1007, "5000007", 80001, "M", "D", "S", 10, 75000.0, 0, "N", "D", "AH Placed"),
    order(1008, "5000008", 99999, "N", "C", "B", 3, 5.0, None, "X", "Z", "Something Else"),
    order(1009, "", 3045, "N", "C", "B", 4, 800.0, 0, "N", "I", "Pending"),
    order(1010, "5000010", 3045, "N", "C", "B", 6, 800.0, 0, "N", "I", "Xmitted"),
    order(1011, "5000011", 3045, "N", "C", "S", 6, 800.0, 0, "N", "I", "AH Cancelled"),
    order(1012, "5000012", 3045, "N", "C", "S", 6, 800.0, 0, "N", "I", "Rejected by 5P"),
    order(1013, "5000013", 3045, "N", "C", "B", 1, 800.0, 0, "N", "I", "Placed", BrokerOrderTime=date(1793591100000, "-0430")),
    order(1014, "5000014", 3045, "N", "C", "B", 1, 800.0, 0, "N", "I", "Pending", BrokerOrderTime="not a date"),
]}}
NO_ORDERS = {"head": head(), "body": {"OrderBookDetail": None}}
CANCEL_BOOK = {"head": head(), "body": {"OrderBookDetail": [
    order(1001, "5000001", 3045, "N", "C", "B", 10, 800.5, 0, "N", "I", "Pending"),
    order(1002, "5000002", 49500, "N", "D", "B", 75, 24010.0, 24000.0, "N", "D", "Modified"),
    order(1003, "5000003", 1594, "N", "C", "S", 5, 1495.0, 0, "N", "D", "Fully Executed"),
    order(1006, "5000006", 90001, "N", "U", "B", 1000, 83.5, 0, "N", "D", "Pending"),
    order(1009, "", 3045, "N", "C", "B", 4, 800.0, 0, "N", "I", "Pending"),
]}}
TRADES = {"head": head(), "body": {"TradeBookDetail": [
    {"ExchOrderID": "5000003", "ScripCode": 1594, "Exch": "N", "ExchType": "C", "DelvIntra": "D", "BuySell": "S", "Qty": 5, "Rate": 1499.95, "ExchangeTradeTime": date(1793591160000)},
    {"ExchOrderID": "5000002", "ScripCode": 49500, "Exch": "N", "ExchType": "D", "DelvIntra": "D", "BuySell": "B", "Qty": 75, "Rate": 24005.5, "ExchangeTradeTime": date(1793591220000)},
    {"ExchOrderID": "5000099", "ScripCode": 99999, "Exch": "N", "ExchType": "C", "DelvIntra": "I", "BuySell": "B", "Qty": 2, "Rate": 2900.0, "ExchangeTradeTime": date(1793591280000)},
]}}
NO_TRADES = {"head": head(), "body": {"TradeBookDetail": None}}


def pos(token, exch, exch_type, order_for, net, buy_avg=0.0, sell_avg=0.0, booked=0.0, mtom=0.0, name="x"):
    return {"ScripCode": token, "Exch": exch, "ExchType": exch_type, "ScripName": name, "OrderFor": order_for, "NetQty": net, "BuyAvgRate": buy_avg, "SellAvgRate": sell_avg, "BookedPL": booked, "MTOM": mtom, "LTP": 805.45}


POSITIONS = {"head": head(), "body": {"Status": 0, "NetPositionDetail": [
    pos(3045, "N", "C", "I", 10, 800.0, 0.0, 12.5, 54.5),
    pos(49500, "N", "D", "D", -75, 0.0, 24000.0, 0, -412.5),
    pos(1594, "N", "C", "D", 0, 1500.0, 1510.0, 100, 0),
    pos(80001, "M", "D", "D", 10, 75000.0, 0.0, 0, 100),
    pos(99999, "N", "C", "I", 4, 0.0, 0.0),
]}}
FLAT = {"head": head(), "body": {"Status": 0, "NetPositionDetail": None}}
HOLDINGS = {"head": head(), "body": {"Data": [
    {"Exch": "N", "Symbol": "INFY", "Quantity": 5, "AvgRate": 1500.0, "CurrentPrice": 1510.5},
    {"Exch": "B", "Symbol": "TATAMOTORS", "Quantity": 2, "AvgRate": 500.0, "CurrentPrice": 495.0},
]}}
NO_HOLDINGS = {"head": head(), "body": {"Data": None}}
MARGIN = {"head": head(), "body": {"EquityMargin": [{"NetAvailableMargin": 100000.5, "TotalCollateralValue": 2500.25, "MarginUtilized": 1184.5}]}}
NO_MARGIN = {"head": head(), "body": {"EquityMargin": [], "Message": "No record found"}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"head": {"status": "0", "statusDescription": "Success"}, "body": {"BrokerOrderID": 1001, "Message": "Success"}}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_delivery", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}),
    place("placeorder_market_becomes_a_protected_limit", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={SNAP: snap(), DEPTH: mdepth()}),
    place("placeorder_market_sell_is_protected_below", {**PLACE, "pricetype": "MARKET", "price": "0", "action": "SELL"}, extra={SNAP: snap(), DEPTH: mdepth()}),
    place("placeorder_market_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={SNAP: snap(24005.5, ScripCode=49500), DEPTH: mdepth()}),
    place("placeorder_market_option", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={SNAP: snap(7.35, ScripCode=49599), DEPTH: mdepth()}),
    place("placeorder_market_without_a_price_goes_as_it_is", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={SNAP: snap(0), DEPTH: mdepth()}),
    place("placeorder_market_when_the_quote_fails_goes_as_it_is", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={SNAP: {"head": head(False, "Failure"), "body": {}}}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_a_protected_stop_limit", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sell_goes_below", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5", "action": "SELL"}),
    place("placeorder_stop_market_with_no_tick_size", {**PLACE, "symbol": "NOTICK", "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_with_a_trigger_too_low", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0.05", "action": "SELL"}),
    place("placeorder_stop_market_without_a_trigger_goes_as_it_is", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_index_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_rejected_by_risk", PLACE, rejected("Insufficient funds")),
    place("placeorder_rejected_with_no_message", PLACE, {"head": head(), "body": {"Status": 1, "BrokerOrderID": 0}}),
    place("placeorder_status_zero_with_no_order_id", PLACE, {"head": head(), "body": {"Status": 0, "BrokerOrderID": 0, "Message": "Success"}}),
    place("placeorder_status_as_text", PLACE, {"head": head(), "body": {"Status": "0", "BrokerOrderID": 77, "Message": "Success"}}),
    place("placeorder_head_says_failure", PLACE, {"head": head(False, "Invalid token"), "body": {"Status": 0, "BrokerOrderID": 5}}),
    place("placeorder_http_error", PLACE, {"status": 500, "body": {"x": 1}}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {OB: ORDERS, MOD_EP: MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {OB: ORDERS, MOD_EP: MOD_OK}),
    req("modifyorder_answer_without_an_order_id_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {OB: ORDERS, MOD_EP: {"head": {"status": "0"}, "body": {}}}),
    req("modifyorder_order_not_in_the_book", "/api/v1/modifyorder", {**CHANGE, "orderid": "9999"}, {OB: ORDERS}),
    req("modifyorder_order_without_an_exchange_id", "/api/v1/modifyorder", {**CHANGE, "orderid": "1009"}, {OB: ORDERS}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {OB: ORDERS, MOD_EP: {"head": {"status": "1", "statusDescription": "Order is not open"}, "body": {}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {OB: ORDERS, MOD_EP: {"head": {"status": "1"}, "body": {}}}),
    req("modifyorder_empty_book", "/api/v1/modifyorder", CHANGE, {OB: NO_ORDERS}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1001"}, {OB: ORDERS, CAN_EP: {"head": head(), "body": {}}}),
    req("cancelorder_by_the_exchange_id", "/api/v1/cancelorder", {"strategy": "t", "orderid": "5000001"}, {OB: ORDERS, CAN_EP: {"head": head(), "body": {}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1001"}, {OB: ORDERS, CAN_EP: {"head": head(False, "Order already executed"), "body": {"Message": "Already executed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1001"}, {OB: ORDERS, CAN_EP: {"head": head(False, "Failure"), "body": {}}}),
    req("cancelorder_not_in_the_book", "/api/v1/cancelorder", {"strategy": "t", "orderid": "9999"}, {OB: ORDERS}),
    req("cancelorder_still_pending_at_the_broker", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1009"}, {OB: ORDERS}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CAN_EP: [{"head": head(), "body": {}}, {"head": head(False, "No"), "body": {}}, {"head": head(), "body": {}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_TRADES}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_failed_reads_as_flat", "/api/v1/positionbook", {}, {PB: {"status": 500, "body": {"x": 1}}}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("funds", "/api/v1/funds", {}, {MB: MARGIN, PB: POSITIONS}),
    req("funds_with_no_positions", "/api/v1/funds", {}, {MB: MARGIN, PB: FLAT}),
    req("funds_no_margin_record", "/api/v1/funds", {}, {MB: NO_MARGIN, PB: POSITIONS}),
    req("funds_http_error", "/api/v1/funds", {}, {MB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: ok_order(), SNAP: [snap(805.45), snap(24005.5, ScripCode=49500), snap(75010.0, ScripCode=80001)], DEPTH: mdepth()}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ok_order()}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
]


def patch():
    pass
