"""HDFC Securities (InvestRight) scenarios: orders, books and funds on developer.hdfcsec.com. The access token goes in Authorization with no prefix, the api_key in the query; instruments are
addressed by the parent exchange, an instrument segment and the broker code. Positions and holdings are priced from one batched /fetch-ltp call."""

AUTH_TOKEN = "hdfc-access-token"
ENV = {"BROKER_API_KEY": "hdfc-api-key", "BROKER_API_SECRET": "hdfc-api-secret"}
SEED_API_KEY_ROW = True


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBINEQN", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFYEQN", "NSE", 1594, "INFOSYS"),
    _sym("RELIANCE", "RELIANCEEQN", "NSE", 2885, "RELIANCE"),
    _sym("NIFTY", "NIFTYEQEQNR", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "49500", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "49599", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("NIFTY25NOV2624000PE", "49600", "NFO", 49600, "NIFTY", 75, "PE", "25-NOV-26", 24000.0, 0.05),
    _sym("RELIANCE25NOV26FUT", "52000", "NFO", 52000, "RELIANCE", 250, "FUT", "25-NOV-26", -1.0, 0.05),
    _sym("SENSEX26NOVFUT", "60001", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("SENSEX", "SENSEXEQ", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX"),
    _sym("GOLDM25NOV26FUT", "80001", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "90001", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.25),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
READ_POSTS = ["/oapi/v1/fetch-ltp"]
IGNORE = []
ORDER_OK = {"body": {"status": "success", "data": {"order_id": "H0001"}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds"}}


def order(oid, security_id, exch, segment, side, qty, price, trigger, otype, product, status, when="2026-11-03 10:20:01", **extra):
    o = {"order_id": oid, "exchange": exch, "instrument_segment": segment, "security_id": security_id, "transaction_type": side, "quantity": qty, "price": price, "trigger_price": trigger,
         "order_type": otype, "product": product, "status": status, "order_timestamp": when, "cancellation_allowed": ""}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "success", "data": [
    order("H1", "SBINEQN", "NSE", "EQUITY", "Buy", 10, 800.5, 0, "LIMIT", "INTRADAY", "PENDING"),
    order("H2", "INFYEQN", "NSE", "EQUITY", "Sell", 5, 0, 0, "MARKET", "DELIVERY", "TRADED", "2026-11-03 10:21:11"),
    order("H3", "49500", "NSE", "FUTIDX", "Buy", 75, 24010, 24000, "SL", "OVERNIGHT", "TRIGGER PENDING", "2026-11-03 10:22:00", expiry_date="25 NOV 2026"),
    order("H4", "SBINEQN", "NSE", "EQUITY", "Sell", 1, 0, 790.5, "SL-M", "INTRADAY", "REJECTED", "2026-11-03 10:23:00", cancellation_allowed="NO"),
    order("H5", "SBINEQN", "NSE", "EQUITY", "Buy", 2, 700, 0, "LIMIT", "INTRADAY", "Cancel_Confirmed", "2026-11-03 10:24:00"),
    order("H6", "49599", "NSE", "OPTIDX", "Buy", 75, 100, 0, "SL-L", "COVER", "Partially Traded", "2026-11-03 10:25:00", cancellation_allowed="YES"),
    order("H7", "UNKNOWNCODE", "NSE", "EQUITY", "Buy", 1, 5, 0, "XX", "WHATEVER", "Something else", "2026-11-03 10:26:00", company_name="MYSTERY"),
    order("H8", "BANKNIFTY", "NSE", "OPTIDX", "Sell", 15, 50, 0, "LIMIT", "OVERNIGHT", "OPEN", "2026-11-03 10:27:00", underlying_symbol="NIFTY", expiry_date="27-NOV-26", option_type="Put", strike_price=24000),
    order("H9", "90001", "NSE", "FUTCUR", "Buy", 1000, 83.5, 0, "LIMIT", "INTRADAY", "OPEN", "2026-11-03 10:28:00"),
    order("H10", "60001", "BSE", "FUTIDX", "Buy", 20, 80000, 0, "LIMIT", "OVERNIGHT", "Placed", "2026-11-03 10:29:00"),
    order("H11", "80001", "MCX", "FUTCOM", "Sell", 10, 75000, 0, "LIMIT", "OVERNIGHT", "Open", "2026-11-03 10:30:00"),
]}}
NO_ORDERS = {"body": {"status": "success", "data": []}}
NULL_ORDERS = {"body": {"status": "success", "data": None}}


def trade(oid, security_id, exch, segment, side, qty, price, value=None, when="2026-11-03 10:21:12", **extra):
    t = {"order_id": oid, "exchange": exch, "instrument_segment": segment, "security_id": security_id, "transaction_type": side, "filled_quantity": qty, "average_price": price, "fill_timestamp": when, "product": "INTRADAY"}
    if value is not None:
        t["total_traded_value"] = value
    t.update(extra)
    return t


TRADES = {"body": {"status": "success", "data": [
    trade("H2", "INFYEQN", "NSE", "EQUITY", "Sell", 5, 1499.95, 7499.75, product="DELIVERY"),
    trade("H3", "NIFTY", "NSE", "OPTIDX", "Buy", 75, 100.5, None, expiry_date="25 NOV 2026", option_type="Call", strike_price=24000, product="OVERNIGHT"),
    trade("H3", "NIFTY", "NSE", "FUTIDX", "Buy", 75, 24005.5, None, expiry_date="25 NOV 2026", product="OVERNIGHT"),
    trade("H4", "CIPLTDEQNR", "NSE", "OPTSTK", "Buy", 10, 5.5, None, expiry_date="30 APR 2026", option_type="Put", strike_price=262.5),
    trade("H5", "RELIANCE", "NSE", "EQUITY", "Buy", "2", "2900", "x"),
    trade("H6", "12345", "NSE", "EQUITY", "Sell", 1, 10.0, 10.0),
]}}


def pos(security_id, exch, segment, product, net, buy_value, sell_value, avg_buy, avg_sell, realised=0, **extra):
    p = {"security_id": security_id, "exchange": exch, "instrument_segment": segment, "product": product, "net_qty": net, "total_buy_value": buy_value, "total_sell_value": sell_value,
         "average_buy_price": avg_buy, "average_sell_price": avg_sell, "realised_pl_overall_position": realised}
    p.update(extra)
    return p


POSITIONS = {"body": {"status": "success", "data": {"net": [
    pos("SBINEQN", "NSE", "EQUITY", "INTRADAY", 10, 8011.234, 0, 801.1234, 0),
    pos("49500", "NSE", "FUTIDX", "OVERNIGHT", -75, 0, 1800412.5, 0, 24005.5, 1200.5),
    pos("INFYEQN", "NSE", "EQUITY", "DELIVERY", 0, 7500, 7495, 1500, 1499, -5),
    pos("UNKNOWNCODE", "NSE", "EQUITY", "COVER", 3, 30, 0, 10, 0),
]}}}
NO_POSITIONS = {"body": {"status": "success", "data": {"net": []}}}
LTPS = {"body": {"status": "success", "data": [{"token": "3045", "exchange": "NSE", "ltp": 805.45, "prev_close": 803.95}, {"token": "49500", "exchange": "NFO", "ltp": 24000.0, "prev_close": 23980.0}]}}


def holding(security_id, qty, avg, close, **extra):
    h = {"security_id": security_id, "exchange": "NSE", "quantity": qty, "average_price": avg, "close_price": close, "isin": "INE000A01001"}
    h.update(extra)
    return h


HOLDINGS = {"body": {"status": "success", "data": [
    holding("INFYEQN", 40, 1400, 1490),
    holding("", 10, 12.5, 0, company_name="OLDCO", isin="INE111"),
    holding("", 7, 50, 55, isin="INE222"),
    holding("SBINEQN", 3, 0, 800),
]}}
HOLDING_LTP = {"body": {"status": "success", "data": [{"token": "1594", "exchange": "NSE", "ltp": 1510.0, "prev_close": 1490.0}]}}
NO_HOLDINGS = {"body": {"status": "success", "data": []}}
MARGINS = {"body": {"status": "success", "data": {"equity": {"total_available_limit": 9999.25, "total_utilised_limit": "1,184.5", "total_limit": 11183.75,
                                                             "totalLimitDetails": {"pledge_limit": 2500.5}}}}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


B = "/oapi/v1"
PLACE_EP, OB, TB, PB, HB, MB, LB = f"POST {B}/orders/regular", f"GET {B}/orders", f"GET {B}/trades", f"GET {B}/portfolio/cumulative-positions", f"GET {B}/portfolio/holdings", f"GET {B}/user/margins", f"PUT {B}/fetch-ltp"
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "H1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
EXPIRED = {"status": 401, "body": {"error": "token expired"}}


def place(name, request, reply=ORDER_OK):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply}, ignore=IGNORE)


SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_market", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_cnc_with_disclosed_quantity", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "SBIN", "exchange": "NSE", "product": "NRML"}),
    place("placeorder_index_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_index_call", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "100"}),
    place("placeorder_index_put_with_mis", {**PLACE, "symbol": "NIFTY25NOV2624000PE", "exchange": "NFO", "quantity": "75", "product": "MIS", "price": "100"}),
    place("placeorder_stock_future", {**PLACE, "symbol": "RELIANCE25NOV26FUT", "exchange": "NFO", "quantity": "250", "product": "NRML", "price": "2900"}),
    place("placeorder_bse_index_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_broker_refuses", PLACE, ORDER_REFUSED),
    place("placeorder_refused_with_an_ok_status", PLACE, {"body": {"status": "error", "message": "Rejected"}}),
    place("placeorder_success_without_an_order_id", PLACE, {"body": {"status": "success", "data": {}}}),
    place("placeorder_success_without_data", PLACE, {"body": {"status": "success"}}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {f"PUT {B}/orders/regular/H1": ORDER_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {f"PUT {B}/orders/regular/H1": ORDER_OK}),
    req("modifyorder_to_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {f"PUT {B}/orders/regular/H1": ORDER_OK}),
    req("modifyorder_derivative_uses_the_overnight_product", "/api/v1/modifyorder", {**CHANGE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {f"PUT {B}/orders/regular/H1": ORDER_OK}),
    req("modifyorder_answer_without_an_id_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {f"PUT {B}/orders/regular/H1": {"body": {"status": "success", "data": {}}}}),
    req("modifyorder_answer_with_another_id", "/api/v1/modifyorder", CHANGE, {f"PUT {B}/orders/regular/H1": {"body": {"status": "success", "data": {"order_id": "H9"}}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {f"PUT {B}/orders/regular/H1": {"status": 400, "body": {"status": "error", "message": "Order is not open"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {f"PUT {B}/orders/regular/H1": {"status": 400, "body": {"status": "error"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "H1"}, {f"DELETE {B}/orders/regular/H1": {"body": {"status": "success", "data": {"order_id": "H1"}}}}),
    req("cancelorder_answer_without_an_id", "/api/v1/cancelorder", {"strategy": "t", "orderid": "H1"}, {f"DELETE {B}/orders/regular/H1": {"body": {"status": "success"}}}),
    req("cancelorder_refused_with_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "H2"}, {f"DELETE {B}/orders/regular/H2": {"status": 400, "body": {"status": "error", "message": "Order already executed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "H2"}, {f"DELETE {B}/orders/regular/H2": {"status": 400, "body": {"status": "error"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, **{f"DELETE {B}/orders/regular/{i}": {"body": {"status": "success", "data": {"order_id": i}}} for i in ("H1", "H6", "H8", "H9", "H10", "H11")},
                                                                    f"DELETE {B}/orders/regular/H3": {"status": 400, "body": {"status": "error", "message": "no"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("cancelallorder_book_error", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"body": {"status": "error", "message": "bad"}}}),
    req("cancelallorder_book_not_a_list", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: {"body": {"status": "success", "data": {"x": 1}}}}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_data_is_null", "/api/v1/orderbook", {}, {OB: NULL_ORDERS}),
    req("orderbook_expired_session", "/api/v1/orderbook", {}, {OB: EXPIRED}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: NO_ORDERS}),
    req("tradebook_expired_session", "/api/v1/tradebook", {}, {TB: EXPIRED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: LTPS}),
    req("positionbook_prices_unavailable", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: {"status": 500, "body": {"x": 1}}}),
    req("positionbook_prices_rate_limited_then_answered", "/api/v1/positionbook", {}, {PB: POSITIONS, LB: [{"status": 429, "body": {}}, LTPS]}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_expired_session", "/api/v1/positionbook", {}, {PB: EXPIRED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS, LB: HOLDING_LTP}),
    req("holdings_without_prices_use_the_previous_close", "/api/v1/holdings", {}, {HB: HOLDINGS, LB: {"status": 500, "body": {"x": 1}}}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_expired_session", "/api/v1/holdings", {}, {HB: EXPIRED}),
    req("funds", "/api/v1/funds", {}, {MB: MARGINS, PB: POSITIONS, LB: LTPS}),
    req("funds_positions_unreadable", "/api/v1/funds", {}, {MB: MARGINS, PB: EXPIRED}),
    req("funds_bare_equity", "/api/v1/funds", {}, {MB: {"body": {"status": "success", "data": {"equity": {}}}}, PB: NO_POSITIONS}),
    req("funds_without_equity", "/api/v1/funds", {}, {MB: {"body": {"status": "success", "data": {}}}, PB: NO_POSITIONS}),
    req("funds_error_status", "/api/v1/funds", {}, {MB: {"body": {"status": "error", "message": "x"}}}),
    req("funds_expired_session", "/api/v1/funds", {}, {MB: EXPIRED}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS, LB: LTPS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"status": "success", "data": {"net": POSITIONS["body"]["data"]["net"][:3]}}}, LB: LTPS, PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("closeposition_an_order_is_refused", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"status": "success", "data": {"net": POSITIONS["body"]["data"]["net"][:2]}}}, LB: LTPS, PLACE_EP: ORDER_REFUSED}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, LB: LTPS, PLACE_EP: ORDER_OK}, ignore=IGNORE),
]


def patch():
    # The quote path opens a short-lived market-data socket; nothing is connected in a recording, so the snapshot is empty, which is what a feed that does not answer in time gives.
    import importlib

    data = importlib.import_module("broker.hdfcsecurities.api.data")
    data.BrokerData._collect_feed_snapshot = lambda self, instruments, is_complete: {}
