"""HDFC Sky scenarios: orders, books and funds on developer.hdfcsky.com. Instruments are addressed by token; the api_key (and usually the client id, read from the access token) go in the query; a market order is
turned into a protected limit order because HDFC Sky refuses market types over the API. The pending and completed order books are two calls, merged."""

import base64
import json


def _b64(data):
    return base64.urlsafe_b64encode(json.dumps(data).encode()).decode().rstrip("=")


CLIENT = "S0190007"
AUTH_TOKEN = f"{_b64({'alg': 'HS256', 'typ': 'JWT'})}.{_b64({'sub': CLIENT, 'exp': 1999999999})}.signature"
ENV = {"BROKER_API_KEY": "sky-api-key", "BROKER_API_SECRET": "sky-api-secret"}
SEED_API_KEY_ROW = True
READ_POSTS = ["/oapi/v1/fetch-ltp"]
IGNORE = []


def _sym(symbol, brsymbol, exchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY-EQ", "NSE", 1594, "INFOSYS"),
    _sym("RELIANCE", "RELIANCE-EQ", "NSE", 2885, "RELIANCE"),
    _sym("NIFTY", "NIFTY 50", "NSE_INDEX", 26000, "NIFTY", 1, "INDEX"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("NIFTY25NOV2624000PE", "NIFTY25NOV2624000PE", "NFO", 49600, "NIFTY", 75, "PE", "25-NOV-26", 24000.0, 0.05),
    _sym("RELIANCE25NOV26FUT", "RELIANCE25NOV26FUT", "NFO", 52000, "RELIANCE", 250, "FUT", "25-NOV-26", -1.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("SENSEX", "SENSEX", "BSE_INDEX", 26001, "SENSEX", 1, "INDEX"),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.25),
    _sym("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570, "TATA MOTORS"),
]

B = "/oapi/v1"
PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
OB, TB, PB, HB, FB, LB = f"GET {B}/orders", f"GET {B}/trades", f"GET {B}/positions", f"GET {B}/holdings", f"GET {B}/funds/view", f"PUT {B}/fetch-ltp"
PLACE_EP, MODIFY_EP = f"POST {B}/orders", f"PUT {B}/orders"
ORDER_OK = {"body": {"status": "success", "data": {"oms_order_id": "S0001"}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds"}}
EXPIRED = {"status": 401, "body": {"error": "invalid credentials"}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def ltp(*rows):
    return {"body": {"status": "success", "data": [{"token": str(t), "exchange": e, "ltp": p, "prev_close": c} for t, e, p, c in rows]}}


SBIN_LTP = ltp((3045, "NSE", 805.45, 800.0))
FUT_LTP = ltp((49500, "NFO", 24005.5, 23990.0))
OPT_LTP = ltp((49599, "NFO", 7.35, 7.0))


def order(oid, token_symbol, exch, side, qty, price, trigger, otype, product, status, when="2026-11-03 10:20:01", **extra):
    o = {"oms_order_id": oid, "exchange": exch, "trading_symbol": token_symbol, "order_side": side, "quantity": qty, "price": price, "trigger_price": trigger, "order_type": otype,
         "product": product, "order_status": status, "order_entry_time": when, "exchange_time": "2026-11-03 10:20:02"}
    o.update(extra)
    return o


PENDING = {"body": {"status": "success", "data": {"orders": [
    order("S1", "SBIN-EQ", "NSE", "BUY", 10, 800.5, 0, "LIMIT", "MIS", "ACCEPTED"),
    order("S2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, 24010, 24000, "SL", "NRML", "SL_TRIGGER_CONFIRMED", "2026-11-03 10:22:00"),
    order("S3", "NIFTY25NOV2624000CE", "NFO", "SELL", 75, 0, 100, "SLM", "NRML", "TRIGGER_PENDING", "2026-11-03 10:23:00"),
    order("S4", "SBIN-EQ", "NSE", "SELL", 3, 790, 0, "LIMIT", "CNC", "PARTIAL_TRADE", "2026-11-03 10:24:00"),
    order("S5", "USDINR25NOV26FUT", "NCD", "BUY", 1000, 83.5, 0, "LIMIT", "NRML", "MODIFY_PENDING", "2026-11-03 10:25:00"),
    order("S6", "GOLDM25NOV26FUT", "MCX", "SELL", 10, 75000, 0, "LIMIT", "NRML", "CANCEL_PENDING", "2026-11-03 10:26:00"),
    order("S7", "INFY-EQ", "NSE", "BUY", 2, 1490, 0, "LIMIT", "MTF", "AMO_REQ_RECEIVED", "2026-11-03 10:27:00"),
]}}}
COMPLETED = {"body": {"status": "success", "data": {"orders": [
    order("C1", "INFY-EQ", "NSE", "SELL", 5, 0, 0, "MARKET", "CNC", "COMPLETE", "2026-11-03 10:21:11"),
    order("C2", "SBIN-EQ", "NSE", "SELL", 1, 0, 790.5, "SLM", "MIS", "REJECTED", "2026-11-03 10:23:00"),
    order("C3", "SBIN-EQ", "NSE", "BUY", 2, 700, 0, "LIMIT", "MIS", "CANCEL_CONFIRMED", "2026-11-03 10:24:00"),
    order("C4", "SENSEX26NOVFUT", "BFO", "BUY", 20, 80000, 0, "LIMIT", "NRML", "cancelled", "2026-11-03 10:25:00"),
    order("C5", "UNKNOWN-EQ", "BSE", "BUY", 1, 5, 0, "XX", "WHATEVER", "Something else", "2026-11-03 10:26:00"),
    order("C6", "TATAMOTORS-EQ", "BSE", "BUY", "7", "500.5", "0", "LIMIT", "CNC", "MODIFY_REJECTED", "2026-11-03 10:27:00"),
    order("C7", "SBIN-EQ", "NSE", "buy", None, None, None, "SL-M", "MIS", "BATCH_CANCEL_CONFIRMED", None, order_entry_time=None, exchange_time="2026-11-03 10:28:00"),
]}}}
NO_ORDERS = {"body": {"status": "success", "data": {"orders": []}}}
NULL_ORDERS = {"body": {"status": "success", "data": None}}
ORDER_ERROR = {"body": {"status": "error", "message": "bad"}}


def trade(oid, brsymbol, exch, side, qty, price, **extra):
    t = {"oms_order_id": oid, "exchange": exch, "trading_symbol": brsymbol, "order_side": side, "trade_quantity": qty, "trade_price": price, "product": "MIS", "trade_time": "2026-11-03 10:21:12", "exchange_time": "2026-11-03 10:21:13"}
    t.update(extra)
    return t


TRADES = {"body": {"status": "success", "data": {"trades": [
    trade("C1", "INFY-EQ", "NSE", "SELL", 5, 1499.95, product="CNC"),
    trade("S2", "NIFTY25NOV26FUT", "NFO", "BUY", 75, 24005.5, product="NRML"),
    trade("S3", "NIFTY25NOV2624000CE", "NFO", "SELL", 75, 100.5, product="NRML"),
    trade("C5", "NOSUCH-EQ", "NSE", "BUY", "2", "2900", product="MTF"),
    trade("C6", "SBIN-EQ", "NSE", "BUY", None, None, filled_quantity=4, order_price=801.25, trade_time=None),
    trade("C7", "USDINR25NOV26FUT", "NCD", "SELL", 1000, 83.25, product="NRML"),
]}}}


def pos(brsymbol, exch, product, net, buy_amount, sell_amount, avg_buy, avg_sell, ltp_, **extra):
    p = {"trading_symbol": brsymbol, "exchange": exch, "product": product, "net_quantity": net, "buy_amount": buy_amount, "sell_amount": sell_amount, "average_buy_price": avg_buy,
         "average_sell_price": avg_sell, "ltp": ltp_, "multiplier": 1, "average_price": 0}
    p.update(extra)
    return p


POSITIONS = {"body": {"status": "success", "data": [
    pos("SBIN-EQ", "NSE", "MIS", 10, 8000.0, 0.0, 800.0, 0, 805.45),
    pos("NIFTY25NOV26FUT", "NFO", "NRML", -75, 0.0, 1800000.0, 0, 24000.0, 24005.5),
    pos("INFY-EQ", "NSE", "CNC", 0, 15000.0, 15100.0, 1500.0, 1510.0, 1505.0, average_price=1500.0),
    pos("GOLDM25NOV26FUT", "MCX", "NRML", 10, 750000.0, 0.0, 75000.0, 0, 75010.0, multiplier=10),
    pos("USDINR25NOV26FUT", "NCD", "NRML", 1000, 83500.0, 0, 83.5, 0, 83.6),
    pos("UNKNOWN-EQ", "NSE", "MTF", 5, "100", "0", "20", 0, "21.5"),
]}}
NO_POSITIONS = {"body": {"status": "success", "data": []}}
NESTED_POSITIONS = {"body": {"status": "success", "data": {"positions": [pos("SBIN-EQ", "NSE", "MIS", 10, 8000.0, 0.0, 800.0, 0, 805.45)]}}}


def holding(brsymbol, qty, avg, last, **extra):
    h = {"exchange": "NSE", "trading_symbol": brsymbol.split("-")[0], "instrument_details": {"trading_symbol": brsymbol}, "quantity": qty, "buy_avg": avg, "ltp": last}
    h.update(extra)
    return h


HOLDINGS = {"body": {"status": "success", "data": {"holdings": [
    holding("INFY-EQ", 5, 1400.0, 1500.5), holding("SBIN-EQ", 3, 0, 800), holding("TATAMOTORS-EQ", 2, 500.25, 499.5, exchange="BSE"), holding("NOSUCH-EQ", 1, "10", "12"),
]}}}
NO_HOLDINGS = {"body": {"status": "success", "data": {"holdings": []}}}

FUNDS = {"body": {"data": {"client_id": CLIENT, "headers": ["Description", "all"], "realized_mtm": "12.5", "unrealized_mtm": "-3.25",
                           "values": [["Available Margin", "100,000.50"], ["Margin Used", "1,184.5"], ["Pledge Benefit", "2500.5"], ["MTF Available", "999"], ["Other", "1"]]}, "message": "", "status": "success"}}


def place(name, request, reply=ORDER_OK, extra=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_EP: reply, **(extra or {})}, ignore=IGNORE)


CHANGE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "S1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    place("placeorder_limit", PLACE),
    place("placeorder_cnc_with_disclosed_quantity", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}),
    place("placeorder_without_a_strategy_tag", {**PLACE, "strategy": ""}),
    place("placeorder_a_long_strategy_tag_is_cut", {**PLACE, "strategy": "a-strategy-name-that-is-far-longer-than-thirty-two-characters"}),
    place("placeorder_market_becomes_a_protected_limit", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={LB: SBIN_LTP}),
    place("placeorder_market_sell_is_protected_below", {**PLACE, "pricetype": "MARKET", "price": "0", "action": "SELL"}, extra={LB: SBIN_LTP}),
    place("placeorder_market_option_uses_the_option_slab", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={LB: OPT_LTP}),
    place("placeorder_market_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, extra={LB: FUT_LTP}),
    place("placeorder_market_without_a_price_is_sent_as_market", {**PLACE, "pricetype": "MARKET", "price": "0"}, extra={LB: {"status": 500, "body": {"x": 1}}}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_a_protected_stop_limit", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_without_a_trigger_is_sent_as_it_is", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_index_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}),
    place("placeorder_bse_derivative", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "20", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx_future", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "10", "product": "NRML", "price": "75000"}),
    place("placeorder_currency_future", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1000", "product": "NRML", "price": "83.5"}),
    place("placeorder_broker_refuses", PLACE, ORDER_REFUSED),
    place("placeorder_refused_with_an_ok_status", PLACE, {"body": {"status": "error", "message": "Rejected"}}),
    place("placeorder_success_without_an_order_id", PLACE, {"body": {"status": "success", "data": {}}}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    req("modifyorder_limit", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: ORDER_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {**CHANGE, "pricetype": "SL", "price": "800", "trigger_price": "801.5", "disclosed_quantity": "5"}, {MODIFY_EP: ORDER_OK}),
    req("modifyorder_to_market", "/api/v1/modifyorder", {**CHANGE, "pricetype": "MARKET"}, {MODIFY_EP: ORDER_OK}),
    req("modifyorder_derivative", "/api/v1/modifyorder", {**CHANGE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {MODIFY_EP: ORDER_OK}),
    req("modifyorder_answer_without_an_id_keeps_the_given_one", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"body": {"status": "success", "data": {}}}}),
    req("modifyorder_answer_with_another_id", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"body": {"status": "success", "data": {"oms_order_id": "S9"}}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"status": 400, "body": {"status": "error", "message": "Order is not open"}}}),
    req("modifyorder_refused_without_a_message", "/api/v1/modifyorder", CHANGE, {MODIFY_EP: {"status": 400, "body": {"status": "error"}}}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S1"}, {f"DELETE {B}/orders/S1": {"body": {"status": "success", "data": {}}}}),
    req("cancelorder_refused_with_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {f"DELETE {B}/orders/S2": {"status": 400, "body": {"status": "error", "message": "Order already executed"}}}),
    req("cancelorder_refused_without_a_message", "/api/v1/cancelorder", {"strategy": "t", "orderid": "S2"}, {f"DELETE {B}/orders/S2": {"status": 400, "body": {"status": "error"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: [PENDING, NO_ORDERS], **{f"DELETE {B}/orders/{i}": {"body": {"status": "success", "data": {}}} for i in ("S1", "S2", "S3", "S4", "S5", "S6")},
                                                                    f"DELETE {B}/orders/S7": {"status": 400, "body": {"status": "error", "message": "no"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: [NO_ORDERS, NO_ORDERS]}),
    req("cancelallorder_book_error", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: [ORDER_ERROR, ORDER_ERROR]}),
    req("cancelallorder_only_cancellable_statuses", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: [COMPLETED, NO_ORDERS]}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: [PENDING, COMPLETED]}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: [NO_ORDERS, NO_ORDERS]}),
    req("orderbook_data_is_null", "/api/v1/orderbook", {}, {OB: [NULL_ORDERS, COMPLETED]}),
    req("orderbook_one_half_fails_and_the_other_is_kept", "/api/v1/orderbook", {}, {OB: [ORDER_ERROR, COMPLETED]}),
    req("orderbook_the_other_half_fails", "/api/v1/orderbook", {}, {OB: [PENDING, ORDER_ERROR]}),
    req("orderbook_expired_session", "/api/v1/orderbook", {}, {OB: [EXPIRED, EXPIRED]}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "success", "data": {"trades": []}}}}),
    req("tradebook_expired_session", "/api/v1/tradebook", {}, {TB: EXPIRED}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS}),
    req("positionbook_nested_rows", "/api/v1/positionbook", {}, {PB: NESTED_POSITIONS}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("positionbook_expired_session", "/api/v1/positionbook", {}, {PB: EXPIRED}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("holdings_expired_session", "/api/v1/holdings", {}, {HB: EXPIRED}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS}),
    req("funds_rows_as_numbered_objects", "/api/v1/funds", {}, {FB: {"body": {"status": "success", "data": {"values": [{"0": "Available Margin", "1": "500.5"}, {"0": "Margin Used", "1": "50"}]}}}}),
    req("funds_without_the_named_mtm_fields", "/api/v1/funds", {}, {FB: {"body": {"status": "success", "data": {"values": [["Available Margin", "100"], ["Realized MTM", "4.5"], ["Unrealized MTM", "-1"]]}}}}),
    req("funds_nothing_sent", "/api/v1/funds", {}, {FB: {"body": {"status": "success", "data": {}}}}),
    req("funds_error_status", "/api/v1/funds", {}, {FB: {"body": {"status": "error", "message": "x"}}}),
    req("funds_expired_session", "/api/v1/funds", {}, {FB: EXPIRED}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS}),
    req("openposition_other_product_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, {PB: POSITIONS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"status": "success", "data": POSITIONS["body"]["data"][:4]}}, LB: [SBIN_LTP, FUT_LTP, ltp((80001, "MCX", 75010.0, 75000.0))], PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("closeposition_an_order_is_refused", "/api/v1/closeposition", {"strategy": "t"}, {PB: {"body": {"status": "success", "data": POSITIONS["body"]["data"][:2]}}, LB: [SBIN_LTP, FUT_LTP], PLACE_EP: ORDER_REFUSED}, ignore=IGNORE),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ORDER_OK}, ignore=IGNORE),
    req("placesmartorder_already_matched", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "10"}, {PB: POSITIONS}),
]


def patch():
    # The quote a market order is priced from adds the session candle (open, high, low, volume); the price protection reads only the last price, so the candle call is left out of these recordings.
    import importlib

    data = importlib.import_module("broker.hdfcsky.api.data")
    data.BrokerData._session_ohlcv = lambda self, row: {"open": 0.0, "high": 0.0, "low": 0.0, "close": 0.0, "volume": 0}
    data.BrokerData._collect_feed_snapshot = lambda self, instruments, is_complete: {}
