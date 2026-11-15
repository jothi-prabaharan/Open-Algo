"""mStock (api.mstock.trade/openapi/typeb): orders, books, funds and quotes. Headers X-Mirae-Version, X-PrivateKey (the API key) and a bearer token. An order write answers 200 for a business refusal, so the payload's status says whether it worked; a reply may be wrapped in a one-item list. Quotes are a GET with a JSON body."""

AUTH_TOKEN = "ms-jwt-token"
ENV = {"BROKER_API_KEY": "ms-client-code", "BROKER_API_SECRET": "ms-private-key"}
SEED_API_KEY_ROW = True
IGNORE = []
V = "/openapi/typeb"
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
    _sym("SENSEX", "SENSEX", "BSE_INDEX", 1, "SENSEX", 1, "INDEX"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, CANCELALL_EP = f"POST {V}/orders/regular", f"POST {V}/orders/cancelall"
OB, TB, PB, HB, FB = f"GET {V}/orders", f"GET {V}/tradebook", f"GET {V}/portfolio/positions", f"GET {V}/portfolio/holdings", f"GET {V}/user/fundsummary"


def mod_ep(order_id):
    return f"PUT {V}/orders/regular/{order_id}"


def cancel_ep(order_id):
    return f"DELETE {V}/orders/regular/{order_id}"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"status": "true", "message": "SUCCESS", "data": {"orderid": "MS0001", "uniqueorderid": "U0001"}}}
REFUSED = {"body": {"status": "false", "message": "RMS: insufficient funds", "errorcode": "RMS001"}}


def place(name, request, reply=None, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply or OK_ORDER, **(extra or {})}, ignore=IGNORE)


def order(number, symbol, exch, side, qty, price, otype, product, status, **extra):
    o = {"orderid": number, "tradingsymbol": symbol, "symboltoken": "3045", "exchange": exch, "instrumenttype": "", "transactiontype": side, "quantity": qty, "price": price, "triggerprice": 0, "averageprice": 0,
         "ordertype": otype, "producttype": product, "status": status, "updatetime": "03-Nov-2026 10:20:01"}
    o.update(extra)
    return o


ORDERS = {"body": {"status": True, "data": [
    order("MS1", "SBIN-EQ", "NSE", "BUY", 10, 800.5, "LIMIT", "INTRADAY", "Pending"),
    order("MS2", "NIFTY25NOV26FUT", "NSE", "BUY", 75, 24010, "STOPLOSS_LIMIT", "CARRYFORWARD", "Trigger Pending", symboltoken="49500", instrumenttype="FUTIDX", triggerprice=24000),
    order("MS3", "INFY-EQ", "NSE", "SELL", 5, 1495, "LIMIT", "DELIVERY", "Traded", symboltoken="1594", averageprice=1499.95),
    order("MS4", "SBIN-EQ", "NSE", "SELL", 1, 0, "STOP_LOSS_MARKET", "INTRADAY", "Rejected", triggerprice=790.5),
    order("MS5", "TATAMOTORS-EQ", "BSE", "BUY", 2, 0, "MARKET", "DELIVERY", "Cancelled", symboltoken="500570"),
    order("MS6", "SENSEX26NOVFUT", "BSE", "BUY", 20, 80000, "SL", "CARRYFORWARD", "O-Pending", symboltoken="60001", instrumenttype="FUTIDX"),
    order("MS7", "GOLDM25NOV26FUT", "MCX", "SELL", 10, 75000, "STOPLOSS_MARKET", "CARRYFORWARD", "O-Modified", symboltoken="80001"),
    order("MS8", "NOSUCH-EQ", "NSE", "BUY", 3, 5, "XX", "ZZ", "TRADE CONFIRMED", symboltoken="999999", averageprice="5.5"),
    order("MS9", "USDINR25NOV26FUT", "CDS", "BUY", 1000, 83.5, "MARKET", "CARRYFORWARD", "Something Else", symboltoken="90001", averageprice="83.6"),
    order("MS10", "SBIN-EQ", "NSE", "BUY", 4, "bad", "LIMIT", "MARGIN", "O-Cancelled"),
    order("MS11", "INFY-EQ", "NSE", "BUY", 1, 10, "LIMIT", "INTRADAY", "", symboltoken="1594"),
]}}
CANCEL_BOOK = {"body": {"status": "true", "data": [
    order("MS1", "SBIN-EQ", "NSE", "BUY", 10, 800.5, "LIMIT", "INTRADAY", "open"),
    order("MS2", "NIFTY25NOV26FUT", "NSE", "BUY", 75, 24010, "SL", "CARRYFORWARD", "Trigger Pending"),
    order("MS3", "INFY-EQ", "NSE", "SELL", 5, 1495, "LIMIT", "DELIVERY", "Traded"),
    order("MS4", "SBIN-EQ", "NSE", "SELL", 1, 0, "MARKET", "INTRADAY", "O-Pending"),
]}}
EMPTY = {"body": {"status": True, "data": []}}
NULL_DATA = {"body": {"status": True, "data": None}}
FAILED = {"body": {"status": False, "message": "Session expired"}}
NOT_JSON = {"status": 200, "body": "<html>"}


def trade(order_no, symbol, exch, side, qty, price, value, **extra):
    t = {"ORDER_NUMBER": order_no, "SYMBOL": symbol, "EXCHANGE": exch, "SEC_ID": "3045", "INSTRUMENT_NAME": "EQUITY", "PRODUCT": "INTRADAY", "BUY_SELL": side, "QUANTITY": qty, "PRICE": price, "TRADE_VALUE": value,
         "ORDER_DATE_TIME": "03-Nov-2026 10:21:12"}
    t.update(extra)
    return t


TRADES = {"body": {"status": "true", "data": [
    trade("MS3", "INFY", "NSE", "Sell", 5, 1499.95, 7499.75, SEC_ID="1594", PRODUCT="DELIVERY"),
    trade("MS2", "NIFTY25NOV26FUT", "NSE", "Buy", 75, 24005.5, 1800412.5, SEC_ID="49500", INSTRUMENT_NAME="FUTIDX", PRODUCT="CARRYFORWARD"),
    trade("MS99", "NOSUCH", "NSE", "BUY", 2, 2900, 5800, SEC_ID="999999", PRODUCT="CNC"),
    trade("MS98", "SBIN", "NSE", "buy", "x", "y", "z", SEC_ID=""),
    trade("MS97", "SBIN", "NSE", "Buy", 1, 800, 800, SEC_ID="", PRODUCT="MARGIN"),
    trade("MS96", "", "NSE", "Buy", 1, 800, 800, SEC_ID="", PRODUCT="INTRADAY"),
    trade("MS95", "TATAMOTORS", "BSE", "Sell", 3, 500, 1500, SEC_ID="500570", PRODUCT="DELIVERY"),
    {"ORDER_NUMBER": "MS94", "SYMBOL": "SBIN", "SEC_ID": "3045", "BUY_SELL": "Buy", "QUANTITY": 1, "PRICE": 800, "TRADE_VALUE": 800},
]}}


def pos(token, exch, product, net, avg="0", value="0", **extra):
    p = {"symboltoken": token, "exchange": exch, "instrumenttype": "", "symbolname": "", "producttype": product, "netqty": str(net), "avgnetprice": avg, "netvalue": value}
    p.update(extra)
    return p


POSITIONS = {"body": {"status": True, "data": [
    pos("3045", "NSE", "INTRADAY", 10, "800.00", "54.5"),
    pos("49500", "NSE", "CARRYFORWARD", -75, "24000", "-412.5", instrumenttype="FUTIDX"),
    pos("1594", "NSE", "DELIVERY", 0, "1500", "100"),
    pos("80001", "MCX", "CARRYFORWARD", 10, "75000", "100"),
    pos("999999", "NSE", "INTRADAY", 4, "10", "0", symbolname="NOSUCH-EQ"),
    pos("60001", "BSE", "CARRYFORWARD", 20, "80000", "x", instrumenttype="FUTIDX"),
    pos("500570", "BSE", "MARGIN", "n/a", "5", "0"),
]}}
POSITIONS_ODD = {"body": {"status": True, "data": [
    pos("", "NSE", "INTRADAY", 1, "bad", "0", symbolname="SBIN-EQ"),
    pos("", "NSE", "INTRADAY", 1, "5", "0"),
    pos("3045", "NSE", "INTRADAY", 2, "5", "0", symbolname="IGNORED"),
    pos("999999", "NSE", "INTRADAY", 3, "5", "0", symbolname="INFY-EQ"),
    pos("3045", "", "INTRADAY", 2, "5", "0"),
]}}
FLAT = {"body": {"status": True, "data": []}}
HOLDINGS = {"body": {"status": "true", "data": [
    {"tradingsymbol": "INFY-EQ", "exchange": "NSE", "quantity": 6, "averageprice": 1450.5, "ltp": 1510, "profitandloss": 357, "pnlpercentage": 4.1, "product": "DELIVERY"},
    {"tradingsymbol": "TATAMOTORS-EQ", "exchange": "BSE", "quantity": "4", "averageprice": 500, "ltp": 510, "profitandloss": "40", "pnlpercentage": "2.0", "product": None},
    {"tradingsymbol": "SBIN-EQ", "exchange": None, "quantity": 3, "averageprice": 800, "ltp": 805, "profitandloss": None, "pnlpercentage": "n/a"},
    {"tradingsymbol": "NOSUCH-EQ", "exchange": "NSE", "quantity": "x", "averageprice": "y", "ltp": 11, "profitandloss": 2, "pnlpercentage": 10, "product": "DELIVERY"},
]}}
FUNDS = {"body": {"status": True, "data": [{"AVAILABLE_BALANCE": "100000.5", "COLLATERALS": 2500.25, "REALISED_PROFITS": "340.25", "MTM_COMBINED": -120.5, "AMOUNT_UTILIZED": "1184.5"}]}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "MS1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"status": "true", "message": "SUCCESS", "data": {"orderid": "MS1"}}}

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
    place("placeorder_reply_with_a_boolean_status", PLACE, {"body": {"status": True, "data": {"orderid": "MS2"}}}),
    place("placeorder_reply_in_a_list", PLACE, {"body": [{"status": "true", "data": {"orderid": "MS3"}}]}),
    place("placeorder_reply_with_only_the_unique_id", PLACE, {"body": {"status": "True", "data": {"uniqueorderid": "U9"}}}),
    place("placeorder_reply_with_the_status_in_capitals", PLACE, {"body": {"status": "TRUE", "data": {"orderid": "MS4"}}}),
    place("placeorder_refused_with_status_200", PLACE, REFUSED),
    place("placeorder_refused_with_status_400", PLACE, {"status": 400, "body": {"status": "false", "message": "Invalid quantity"}}),
    place("placeorder_refused_without_a_message", PLACE, {"body": {"status": "false"}}),
    place("placeorder_success_without_an_order_id", PLACE, {"body": {"status": "true", "data": {}}}),
    place("placeorder_success_with_no_data", PLACE, {"body": {"status": "true"}}),
    place("placeorder_data_that_is_not_an_object", PLACE, {"body": {"status": "true", "data": "ok"}}),
    place("placeorder_empty_list", PLACE, {"body": []}),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_stop_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_disclosed", "/api/v1/modifyorder", {**CHANGE, "disclosed_quantity": "3"}, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_product_change", "/api/v1/modifyorder", {**CHANGE, "product": "CNC"}, {mod_ep("MS1"): MOD_OK}),
    req("modifyorder_reply_in_a_list", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": [{"status": True, "data": {"orderid": " MS1X "}}]}}),
    req("modifyorder_reply_with_a_message_only", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": {"message": "SUCCESS"}}}),
    req("modifyorder_success_without_data", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": {"status": "true"}}}),
    req("modifyorder_success_with_data_that_has_no_order_id", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": {"status": "true", "data": {"x": 1}}}}),
    req("modifyorder_refused_with_status_200", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": {"status": "false", "message": "Order is not open", "errorcode": "E1"}}}),
    req("modifyorder_refused_with_status_400", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"status": 400, "body": {"status": "false", "message": "Bad price"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): {"body": {"status": "false"}}}),
    req("modifyorder_not_json", "/api/v1/modifyorder", CHANGE, {mod_ep("MS1"): NOT_JSON}),
    req("modifyorder_unknown_symbol", "/api/v1/modifyorder", {**CHANGE, "symbol": "NOPE"}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS1"}, {cancel_ep("MS1"): {"body": {"status": "true", "message": "SUCCESS"}}}),
    req("cancelorder_with_only_the_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS1"}, {cancel_ep("MS1"): {"body": {"message": "SUCCESS"}}}),
    req("cancelorder_reply_in_a_list", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS1"}, {cancel_ep("MS1"): {"body": [{"status": True}]}}),
    req("cancelorder_refused_with_status_200", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS3"}, {cancel_ep("MS3"): {"body": {"status": "false", "message": "Order already complete"}}}),
    req("cancelorder_refused_with_status_404", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS3"}, {cancel_ep("MS3"): {"status": 404, "body": {"status": "false", "message": "No such order"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS3"}, {cancel_ep("MS3"): {"body": {"status": "false"}}}),
    req("cancelorder_not_json", "/api/v1/cancelorder", {"strategy": "t", "orderid": "MS3"}, {cancel_ep("MS3"): NOT_JSON}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCELALL_EP: {"body": {"status": "true", "message": "SUCCESS"}}}),
    req("cancelallorder_reply_status_success", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCELALL_EP: {"body": {"status": "success"}}}),
    req("cancelallorder_reply_message_success", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCELALL_EP: {"body": {"message": "SUCCESS"}}}),
    req("cancelallorder_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCELALL_EP: {"body": {"status": "false", "message": "Cannot cancel now"}}}),
    req("cancelallorder_not_json", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CANCELALL_EP: NOT_JSON}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY}),
    req("cancelallorder_nothing_pending", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"body": {"status": True, "data": [order("MS3", "INFY-EQ", "NSE", "SELL", 5, 1495, "LIMIT", "DELIVERY", "Traded")]}}}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: FAILED}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY}),
    req("orderbook_null_data", "/api/v1/orderbook", {}, {OB: NULL_DATA}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: NOT_JSON}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": {"status": "false", "message": "down"}}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: EMPTY}),
    req("tradebook_null_data", "/api/v1/tradebook", {}, {TB: NULL_DATA}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: FAILED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_rows_without_a_token_or_not_in_the_master", "/api/v1/positionbook", {}, {PB: POSITIONS_ODD}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_null_data", "/api/v1/positionbook", {}, {PB: NULL_DATA}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: EMPTY}),
    req("holdings_null_data", "/api/v1/holdings", {}, {HB: NULL_DATA}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS}),
    req("funds_values_that_are_empty_or_text", "/api/v1/funds", {}, {FB: {"body": {"status": True, "data": [{"AVAILABLE_BALANCE": "None", "COLLATERALS": "", "REALISED_PROFITS": None, "MTM_COMBINED": "n/a", "AMOUNT_UTILIZED": 12}]}}}),
    req("funds_without_data", "/api/v1/funds", {}, {FB: {"body": {"status": True, "data": []}}}),
    req("funds_failed", "/api/v1/funds", {}, {FB: {"body": {"status": False, "message": "Session expired"}}}),
    req("funds_http_error", "/api/v1/funds", {}, {FB: {"status": 500, "body": {"x": 1}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
    req("placesmartorder_book_failed_is_refused", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: FAILED}),
]


def before_each(sc):
    """The smart order keeps the position book for a second, and the next scenario must not read the last one's."""
    import importlib

    importlib.import_module("broker.mstock.api.order_api")._POSITION_BOOK.invalidate("ms-jwt-token")
