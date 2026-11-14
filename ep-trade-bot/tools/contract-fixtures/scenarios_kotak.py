"""Kotak Neo: orders, books and funds. The session token carries the trading token, the session id, the account's own base address and the access token; every call carries them as Auth, Sid and neo-fin-key headers, and a write is a jData form. A positions read also asks for the price of each held instrument, because Kotak sends none."""

import uuid

AUTH_TOKEN = "trade-token:::trade-sid:::https://kotak.test:::access-token:::E43"
ENV = {"BROKER_API_KEY": "UCC123", "BROKER_API_SECRET": "access-token"}
SEED_API_KEY_ROW = True
IGNORE = []
ENCODE_PIPE = True
READ_POSTS = ["/quick/user/limits"]


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", "nse_cm", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", "nse_cm", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS-EQ", "BSE", "bse_cm", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", "nse_fo", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", "nse_fo", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", "bse_fo", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", "mcx_fo", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", "cde_fo", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NOTICK", "NOTICK-EQ", "NSE", "nse_cm", 4242, "NO TICK", 1, "EQ", "", -1.0, 0.0),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
PLACE_EP, MOD_EP, CAN_EP = "POST /quick/order/rule/ms/place", "POST /quick/order/vr/modify", "POST /quick/order/cancel"
OB, TB, PB, HB, LB = "GET /quick/user/orders", "GET /quick/user/trades", "GET /quick/user/positions", "GET /portfolio/v1/holdings", "POST /quick/user/limits"
Q = "GET /script-details/1.0/quotes/neosymbol"


def q(query):
    return f"{Q}/{query.replace('|', '%7C')}/all"


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


OK_ORDER = {"body": {"stat": "Ok", "nOrdNo": "250101000001", "stCode": 200}}
REFUSED = {"status": 400, "body": {"stat": "Not_Ok", "emsg": "RMS: Margin Exceeds", "stCode": 1019}}


def place(name, request, reply=None, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply or OK_ORDER, **(extra or {})}, ignore=IGNORE)


def order(number, token, seg, symbol, side, qty, prc, trg, ptype, product, status, **extra):
    o = {"nOrdNo": number, "tok": str(token), "exSeg": seg, "trdSym": symbol, "trnsTp": side, "qty": str(qty), "prc": prc, "trgPrc": trg, "prcTp": ptype, "prod": product, "ordSt": status,
         "ordEntTm": "03-Nov-2026 10:20:01", "avgPrc": "0.00", "GuiOrdId": "openalgo-abc"}
    o.update(extra)
    return o


ORDERS = {"body": {"stat": "Ok", "stCode": 200, "data": [
    order("K1", 3045, "nse_cm", "SBIN-EQ", "B", 10, "800.50", "0.00", "L", "MIS", "open"),
    order("K2", 49500, "nse_fo", "NIFTY25NOV26FUT", "B", 75, "24010.00", "24000.00", "SL", "NRML", "trigger pending"),
    order("K3", 1594, "nse_cm", "INFY-EQ", "S", 5, "1495.00", "0.00", "L", "CNC", "complete", avgPrc="1499.95"),
    order("K4", 3045, "nse_cm", "SBIN-EQ", "S", 1, "0.00", "790.50", "SL-M", "MIS", "rejected"),
    order("K5", 500570, "bse_cm", "TATAMOTORS-EQ", "B", 2, "0.00", "0.00", "MKT", "CNC", "cancelled"),
    order("K6", 90001, "cde_fo", "USDINR25NOV26FUT", "B", 1000, "83.50", "0.00", "L", "NRML", "open"),
    order("K7", 99999, "nse_cm", "NOSUCH-EQ", "B", 3, "5.00", "0.00", "XX", "MIS", "open"),
    order("K8", "", "nse_cm", "SBIN-EQ", "B", 4, "800", "0.00", "L", "MIS", "complete", avgPrc="799.5"),
]}}
CANCEL_BOOK = {"body": {"stat": "Ok", "data": [
    order("K1", 3045, "nse_cm", "SBIN-EQ", "B", 10, "800.50", "0.00", "L", "MIS", "open"),
    order("K2", 49500, "nse_fo", "NIFTY25NOV26FUT", "B", 75, "24010.00", "24000.00", "SL", "NRML", "trigger pending"),
    order("K3", 1594, "nse_cm", "INFY-EQ", "S", 5, "1495.00", "0.00", "L", "CNC", "complete"),
]}}
EMPTY = {"body": {"stat": "Ok", "stCode": 200, "data": []}}
NULL_DATA = {"body": {"stat": "Ok", "stCode": 200, "data": None}}
FAILED = {"body": {"stat": "Not_Ok", "emsg": "Session expired", "stCode": 1003}}
TRADES = {"body": {"stat": "Ok", "data": [
    {"nOrdNo": "K3", "tok": "", "exSeg": "nse_cm", "trdSym": "INFY-EQ", "trnsTp": "S", "prod": "CNC", "fldQty": "5", "avgPrc": "1499.95", "flId": "T1", "exTm": "03-Nov-2026 10:21:12", "GuiOrdId": "openalgo-abc"},
    {"nOrdNo": "K2", "tok": "49500", "exSeg": "nse_fo", "trdSym": "NIFTY25NOV26FUT", "trnsTp": "B", "prod": "NRML", "fldQty": "75", "avgPrc": "24005.50", "flId": "T2", "exTm": "03-Nov-2026 10:22:00", "GuiOrdId": ""},
    {"nOrdNo": "K9", "tok": "99999", "exSeg": "nse_cm", "trdSym": "NOSUCH-EQ", "trnsTp": "B", "prod": "MIS", "fldQty": "2", "avgPrc": "2900", "flId": "T3", "exTm": "03-Nov-2026 10:23:00", "GuiOrdId": ""},
]}}


def pos(token, seg, symbol, product, fb=0, fs=0, cb=0, cs=0, buy="0", sell="0", cfbuy="0", cfsell="0", **extra):
    p = {"tok": str(token), "exSeg": seg, "trdSym": symbol, "prod": product, "flBuyQty": str(fb), "flSellQty": str(fs), "cfBuyQty": str(cb), "cfSellQty": str(cs), "buyAmt": buy, "sellAmt": sell, "cfBuyAmt": cfbuy,
         "cfSellAmt": cfsell, "multiplier": "1", "genNum": "1", "genDen": "1", "prcNum": "1", "prcDen": "1", "upldPrc": "0.00", "avgnetprice": "0.00"}
    p.update(extra)
    return p


POSITIONS = {"body": {"stat": "Ok", "data": [
    pos(3045, "nse_cm", "SBIN-EQ", "MIS", fb=10, buy="8000.00"),
    pos(49500, "nse_fo", "NIFTY25NOV26FUT", "NRML", fs=75, sell="1800000.00"),
    pos(1594, "nse_cm", "INFY-EQ", "CNC", fb=5, fs=5, buy="7400.00", sell="7500.00"),
    pos(80001, "mcx_fo", "GOLDM25NOV26FUT", "NRML", cb=10, cfbuy="750000.00", upldPrc="75000.00"),
    pos(90001, "cde_fo", "USDINR25NOV26FUT", "NRML", cs=1000, cfsell="83500.00"),
    pos(99999, "nse_cm", "NOSUCH-EQ", "MIS", fb=4, buy="20.00"),
]}}
FLAT = {"body": {"stat": "Ok", "data": []}}


def quote_list(*entries):
    return {"body": [{"exchange": e, "exchange_token": t, "display_symbol": d, "ltp": ltp, "ohlc": {"open": "1", "high": "2", "low": "0.5", "close": "1.5"}, "depth": {"buy": [{"price": ltp, "quantity": 1}], "sell": [{"price": ltp, "quantity": 1}]},
                      "last_volume": "10", "open_int": "0", "total_buy": 1, "total_sell": 1} for e, t, d, ltp in entries]}


POS_QUOTES = quote_list(("nse_cm", "3045", "SBIN-EQ", "805.45"), ("nse_fo", "49500", "NIFTY25NOV26FUT", "24005.5"), ("mcx_fo", "80001", "GOLDM25NOV26FUT", "75100.0"), ("cde_fo", "90001", "USDINR25NOV26FUT", "83.5"))
POS_QUERY = "nse_cm|3045,nse_fo|49500,nse_cm|1594,mcx_fo|80001,cde_fo|90001"
HOLDINGS = {"body": {"stat": "Ok", "data": [
    {"displaySymbol": "INFY", "exchangeSegment": "nse_cm", "instrumentToken": "1594", "instrumentType": "Equity", "quantity": 10, "averagePrice": "1450.5", "mktValue": 15000.0, "holdingCost": 14505.0, "symbol": "INFY"},
    {"displaySymbol": "TATAMOTORS", "exchangeSegment": "bse_cm", "instrumentToken": "500570", "instrumentType": "Equity", "quantity": 4, "averagePrice": "500", "mktValue": 1900.0, "holdingCost": 0.0},
]}}
LIMITS = {"body": {"stat": "Ok", "CollateralValue": "179542.80", "Collateral": "222565.50", "UnrealizedMtomPrsnt": "100.5", "RealizedMtomPrsnt": "-20.25", "MarginUsed": "0.20", "Net": "402108.1"}}
CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "K1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}
MOD_OK = {"body": {"stat": "Ok", "nOrdNo": "K1", "stCode": 200}}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_delivery", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_product", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_market", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_a_protected_stop_limit_buy", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sell_is_protected_below", {**PLACE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_option_uses_the_option_bands", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "SL-M", "price": "0", "trigger_price": "7.35"}),
    place("placeorder_stop_market_option_under_one_rupee", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "0.55"}),
    place("placeorder_stop_market_without_a_trigger", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}),
    place("placeorder_stop_market_without_a_tick_size", {**PLACE, "symbol": "NOTICK", "pricetype": "SL-M", "price": "0", "trigger_price": "100"}),
    place("placeorder_stop_market_sell_too_low", {**PLACE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "0.05"}),
    place("placeorder_disclosed_quantity", {**PLACE, "disclosed_quantity": "5"}),
    place("placeorder_order_tag", {**PLACE, "strategy": "my-strategy-name-which-is-long"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_refused", PLACE, REFUSED),
    place("placeorder_refused_without_a_message", PLACE, {"status": 400, "body": {"stat": "Not_Ok"}}),
    place("placeorder_success_without_an_order_number", PLACE, {"body": {"stat": "Ok"}}),
    place("placeorder_not_json", PLACE, {"status": 502, "body": "Bad gateway"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_market_becomes_a_protected_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "action": "SELL", "price": "0", "trigger_price": "801.5"}, {MOD_EP: MOD_OK}),
    req("modifyorder_stop_market_without_a_trigger", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}, {MOD_EP: MOD_OK}),
    req("modifyorder_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET", "price": "0"}, {MOD_EP: MOD_OK}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"stat": "Not_Ok", "emsg": "Order is not open", "stCode": 1020}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MOD_EP: {"status": 400, "body": {"stat": "Not_Ok"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "K1"}, {CAN_EP: {"body": {"stat": "Ok", "nOrdNo": "K1", "stCode": 200}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "K3"}, {CAN_EP: {"status": 400, "body": {"stat": "Not_Ok", "emsg": "Order not found to cancel", "stCode": 1021}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "K3"}, {CAN_EP: {"status": 400, "body": {"stat": "Not_Ok"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: CANCEL_BOOK, CAN_EP: [{"body": {"stat": "Ok", "nOrdNo": "K1"}}, {"status": 400, "body": {"stat": "Not_Ok", "emsg": "No"}}]}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: EMPTY}),
    req("cancelallorder_book_without_data", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NULL_DATA}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: EMPTY}),
    req("orderbook_without_data", "/api/v1/orderbook", {}, {OB: NULL_DATA}),
    req("orderbook_failed", "/api/v1/orderbook", {}, {OB: FAILED}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: EMPTY}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: FAILED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
    req("positionbook_without_prices", "/api/v1/positionbook", {}, {PB: POSITIONS, q(POS_QUERY): {"status": 500, "body": {"stat": "Not_Ok", "emsg": "down"}}}),
    req("positionbook_flat", "/api/v1/positionbook", {}, {PB: FLAT}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {PB: FAILED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: EMPTY}),
    req("holdings_failed", "/api/v1/holdings", {}, {HB: FAILED}),
    req("funds", "/api/v1/funds", {}, {LB: LIMITS}),
    req("funds_missing_fields_are_zero", "/api/v1/funds", {}, {LB: {"body": {"stat": "Ok"}}}),
    req("funds_failed", "/api/v1/funds", {}, {LB: {"body": {"stat": "Not_Ok", "emsg": "Session expired"}}}),
    req("funds_http_error", "/api/v1/funds", {}, {LB: {"status": 500, "body": "oops"}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
    req("openposition_short_is_negative", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
    req("openposition_carried_forward", "/api/v1/openposition", {"strategy": "t", "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "product": "NRML"}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: OK_ORDER, q(POS_QUERY): POS_QUOTES}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: FLAT}),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {PB: FAILED}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: OK_ORDER, q(POS_QUERY): POS_QUOTES}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS, q(POS_QUERY): POS_QUOTES}),
]


def patch():
    import broker.kotak.mapping.transform_data as td

    td.uuid.uuid4 = lambda: uuid.UUID("12345678-1234-5678-1234-567812345678")
