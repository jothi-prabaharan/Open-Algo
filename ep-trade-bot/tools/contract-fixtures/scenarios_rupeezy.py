"""Rupeezy (Vortex API) scenarios: orders, books and funds. Requests carry the bearer token and the x-api-key header; the API is under /v2."""

AUTH_TOKEN = "rupeezy-access-token"
ENV = {"BROKER_API_KEY": "rupeezy-app-id", "BROKER_API_SECRET": "rupeezy-api-key"}
SEED_API_KEY_ROW = True
B = "/v2"


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": token, "expiry": expiry, "strike": strike,
            "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", "NSE_EQ", "3045", "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", "NSE_EQ", "1594", "INFOSYS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", "NSE_FO", "35001", "NIFTY", 75, "FUTIDX", "25-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", "MCX_FO", "80001", "GOLDM", 10, "FUTCOM", "25-NOV-26", -1.0, 1.0),
    _sym("NIFTY", "NIFTY", "NSE_INDEX", "NSE_EQ", "26000", "NIFTY"),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}
ORDER_OK = {"body": {"status": "success", "code": "order_placed", "message": "Order placed", "data": {"order_id": "ORD0001"}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "code": "insufficient_funds", "message": "Insufficient funds to place this order"}}
NOT_JSON = {"status": 502, "body": "<html>bad gateway</html>"}


def order(oid, ticker, exch, side, variety, product, qty, price, trigger, status, traded=0, when="2026-11-03 10:20:01", **extra):
    o = {"order_id": oid, "ticker": ticker, "exchange": exch, "transaction_type": side, "variety": variety, "product": product, "total_quantity": qty, "order_price": price,
         "trigger_price": trigger, "status": status, "traded_quantity": traded, "order_created_at": when}
    o.update(extra)
    return o


ORDERS = {"body": {"status": "success", "orders": [
    order("O1", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "INTRADAY", 10, 800.5, 0, "PENDING"),
    order("O2", "NSE:INFY", "NSE_EQ", "SELL", "RL-MKT", "DELIVERY", 5, 0, 0, "EXECUTED", 5, "2026-11-03 10:21:11"),
    order("O3", "NSE:NIFTY25NOV26FUT", "NSE_FO", "BUY", "SL", "DELIVERY", 75, 24010, 24000, "TRIGGER PENDING", 0, "2026-11-03 10:22:00"),
    order("O4", "NSE:SBIN", "NSE_EQ", "SELL", "SL-MKT", "INTRADAY", 1, 0, 790.5, "REJECTED", 0, "2026-11-03 10:23:00"),
    order("O5", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "INTRADAY", 2, 700, 0, "CANCELLED", 0, "2026-11-03 10:24:00"),
    order("O6", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "MTF", 3, 700, 0, "OMSXMITTED", 0, "2026-11-03 10:25:00"),
    order("O7", "MCX:GOLDM25NOV26FUT", "MCX_FO", "BUY", "RL", "DELIVERY", 2, 75000, 0, "EXECUTED", 2, "2026-11-03 10:26:00", lot_size=10),
    order("O8", "NSE:UNKNOWN", "NSE_EQ", "BUY", "XX", "WHATEVER", 1, 5, 0, "SOMETHING ELSE", 0, "2026-11-03 10:27:00"),
]}}
NO_ORDERS = {"body": {"status": "success", "orders": []}}


def trade(oid, price, qty, no="T1", when="2026-11-03 10:21:12", **extra):
    t = {"order_id": oid, "trade_no": no, "trade_price": price, "trade_quantity": qty, "traded_at": when}
    t.update(extra)
    return t


TRADES = {"body": {"status": "success", "trades": [
    trade("O2", 1499.95, 5, "T1", ticker="NSE:INFY", exchange="NSE_EQ", transaction_type="SELL", product="DELIVERY"),
    trade("O7", 75000.5, 2, "T2", ticker="MCX:GOLDM25NOV26FUT", exchange="MCX_FO", transaction_type="BUY", product="DELIVERY", lot_size=10),
]}}
TRADES_BARE = {"body": {"status": "success", "trades": [trade("O2", 1499.95, 5, "T1"), trade("OX", 10, 1, "T3")]}}


def pos(ticker, exch, product, qty, buy, sell, avg, **extra):
    p = {"ticker": ticker, "exchange": exch, "product": product, "quantity": qty, "buy_value": buy, "sell_value": sell, "average_price": avg}
    p.update(extra)
    return p


POSITIONS = {"body": {"status": "success", "data": {"net": [
    pos("NSE:SBIN", "NSE_EQ", "INTRADAY", 10, 8011.234, 0, 801.1234),
    pos("NSE:NIFTY25NOV26FUT", "NSE_FO", "DELIVERY", -75, 0, 1800412.5, 24005.5),
    pos("NSE:INFY", "NSE_EQ", "DELIVERY", 0, 7500, 7495, 0),
    pos("MCX:GOLDM25NOV26FUT", "MCX_FO", "DELIVERY", 1, 750000, 0, 75000.0, lot_size=10, multiplier=1),
], "day": []}}}
NO_POSITIONS = {"body": {"status": "success", "data": {"net": [], "day": []}}}
LTPS = {"body": {"status": "success", "data": {"NSE:SBIN": {"last_trade_price": 805.45}, "NSE:NIFTY25NOV26FUT": {"last_trade_price": 24000.0}, "MCX:GOLDM25NOV26FUT": {"last_trade_price": 75100.0}}}}
LTPS_PARTIAL = {"body": {"status": "success", "data": {"NSE:SBIN": {"last_trade_price": 805.45}}}}


def holding(isin, nse_ticker, qty, free, t1, avg, last):
    h = {"isin": isin, "total_free": free, "t1_quantity": t1, "average_price": avg, "last_price": last}
    if nse_ticker:
        h["nse"] = {"ticker": nse_ticker, "exchange": "NSE_EQ"}
    return h


HOLDINGS = {"body": {"status": "success", "data": [
    holding("INE001", "NSE:INFY", 40, 38, 2, 1400.0, 1499.0),
    holding("INE002", "NSE:OLD", 11, 10, 1, 12.5, 0),
    {"isin": "INE003", "total_free": 7, "t1_quantity": 0, "average_price": 50.0, "last_price": 55.0, "bse": {"ticker": "BSE:XYZ", "exchange": "BSE_EQ"}},
    {"isin": "INE004", "total_free": 1, "t1_quantity": 0, "average_price": 0, "last_price": 10.0},
]}}
HOLDINGS_NO_PRICE = {"body": {"status": "success", "data": [holding("INE001", "NSE:INFY", 40, 38, 2, 1400.0, 0)]}}
LTP_INFY = {"body": {"status": "success", "data": {"NSE:INFY": {"last_trade_price": 1510.0}}}}
NO_HOLDINGS = {"body": {"status": "success", "data": []}}
FUNDS = {"body": {"status": "success", "data": {"exchange_combined": {"net_available": 101225.8, "collateral": 1500.0, "total_utilization": -274.95, "booked_profit": 7.0, "mtm_and_booked_loss": -2.0}}}}
FUNDS_SPLIT = {"body": {"status": "success", "data": {"nse": {"net_available": 1000.5, "collateral": 10, "total_utilization": -5, "booked_profit": 1, "mtm_and_booked_loss": 2}, "mcx": {"net_available": 200.25, "collateral": 5, "total_utilization": -7, "booked_profit": 3, "mtm_and_booked_loss": 4}}}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


PLACE_EP = f"POST {B}/trading/orders/regular"
OB = f"GET {B}/trading/orders"
TB = f"GET {B}/trading/trades"
PB = f"GET {B}/trading/portfolio/positions"
QB = f"GET {B}/data/quotes"
HB = f"GET {B}/trading/portfolio/holdings"
FB = f"GET {B}/user/funds"

SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    req("placeorder_limit", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_OK}),
    req("placeorder_market", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_cnc_with_disclosed_quantity", "/api/v1/placeorder", {**PLACE, "product": "CNC", "disclosed_quantity": "2"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_limit", "/api/v1/placeorder", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_stop_market", "/api/v1/placeorder", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_limit_carries_no_trigger", "/api/v1/placeorder", {**PLACE, "trigger_price": "799"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_futures_nrml", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "price": "24005"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_mcx_counts_lots", "/api/v1/placeorder", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "20", "product": "NRML", "price": "75000"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_mcx_disclosed_quantity_counts_lots", "/api/v1/placeorder", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "20", "disclosed_quantity": "10", "product": "NRML", "price": "75000"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_mcx_not_a_whole_number_of_lots", "/api/v1/placeorder", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "15", "product": "NRML", "price": "75000"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_strategy_name_is_cut_to_fifty", "/api/v1/placeorder", {**PLACE, "strategy": "s" * 60}, {PLACE_EP: ORDER_OK}),
    req("placeorder_broker_refuses", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_REFUSED}),
    req("placeorder_reply_is_not_json", "/api/v1/placeorder", PLACE, {PLACE_EP: NOT_JSON}),
    req("placeorder_success_without_an_order_id", "/api/v1/placeorder", PLACE, {PLACE_EP: {"body": {"status": "success", "data": {}}}}),
    req("placeorder_unknown_symbol", "/api/v1/placeorder", {**PLACE, "symbol": "NOPE"}, {PLACE_EP: ORDER_OK}),
    req("modifyorder_limit", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "O1", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O1": {"body": {"status": "success", "orders": [order("O1", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "INTRADAY", 10, 800.5, 0, "PENDING", 4)]}}, f"PUT {B}/trading/orders/regular/O1": ORDER_OK}),
    req("modifyorder_stop_limit_passes_the_traded_quantity", "/api/v1/modifyorder", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "orderid": "O3", "action": "BUY", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24020", "disclosed_quantity": "5", "trigger_price": "24005"},
        {f"GET {B}/trading/orders/O3": {"body": {"status": "success", "orders": [order("O3", "NSE:NIFTY25NOV26FUT", "NSE_FO", "BUY", "SL", "DELIVERY", 75, 24010, 24000, "TRIGGER PENDING", 25)]}}, f"PUT {B}/trading/orders/regular/O3": ORDER_OK}),
    req("modifyorder_to_market", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "O1", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O1": {"body": {"status": "success", "data": [order("O1", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "INTRADAY", 10, 800.5, 0, "PENDING", 0)]}}, f"PUT {B}/trading/orders/regular/O1": ORDER_OK}),
    req("modifyorder_mcx_counts_lots", "/api/v1/modifyorder", {"strategy": "t", "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "orderid": "O7", "action": "BUY", "quantity": "30", "pricetype": "LIMIT", "product": "NRML", "price": "75010", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O7": {"body": {"status": "success", "orders": [order("O7", "MCX:GOLDM25NOV26FUT", "MCX_FO", "BUY", "RL", "DELIVERY", 2, 75000, 0, "PENDING", 1)]}}, f"PUT {B}/trading/orders/regular/O7": ORDER_OK}),
    req("modifyorder_order_not_found", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "O9", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O9": {"body": {"status": "success", "orders": []}}}),
    req("modifyorder_history_unreadable", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "O1", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O1": {"status": 401, "body": {"status": "error", "message": "Invalid token"}}}),
    req("modifyorder_refused", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "O1", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"GET {B}/trading/orders/O1": {"body": {"status": "success", "orders": [order("O1", "NSE:SBIN", "NSE_EQ", "BUY", "RL", "INTRADAY", 10, 800.5, 0, "PENDING", 0)]}}, f"PUT {B}/trading/orders/regular/O1": ORDER_REFUSED}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "O1"}, {f"DELETE {B}/trading/orders/regular/O1": {"body": {"status": "success"}}}),
    req("cancelorder_with_a_question_mark_in_the_id", "/api/v1/cancelorder", {"strategy": "t", "orderid": "OFPIM00001?:"}, {f"DELETE {B}/trading/orders/regular/OFPIM00001%3F%3A": {"body": {"status": "success"}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "O2"}, {f"DELETE {B}/trading/orders/regular/O2": {"status": 400, "body": {"status": "error", "message": "Order already executed"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ORDERS, f"DELETE {B}/trading/orders/regular/O1": {"body": {"status": "success"}}, f"DELETE {B}/trading/orders/regular/O3": {"body": {"status": "success"}},
                                                                   f"DELETE {B}/trading/orders/regular/O6": {"status": 400, "body": {"status": "error", "message": "no"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: NO_ORDERS}),
    req("orderbook", "/api/v1/orderbook", {}, {OB: ORDERS}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: NO_ORDERS}),
    req("orderbook_refused", "/api/v1/orderbook", {}, {OB: {"status": 401, "body": {"status": "error", "message": "Invalid token"}}}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: TRADES}),
    req("tradebook_rows_without_a_ticker_are_completed_from_the_orders", "/api/v1/tradebook", {}, {TB: TRADES_BARE, OB: ORDERS}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: {"body": {"status": "success", "trades": []}}}),
    req("positionbook", "/api/v1/positionbook", {}, {PB: POSITIONS, QB: LTPS}),
    req("positionbook_some_prices_missing", "/api/v1/positionbook", {}, {PB: POSITIONS, QB: LTPS_PARTIAL}),
    req("positionbook_quotes_refused", "/api/v1/positionbook", {}, {PB: POSITIONS, QB: {"status": 500, "body": {"status": "error", "message": "down"}}}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {PB: NO_POSITIONS}),
    req("holdings", "/api/v1/holdings", {}, {HB: HOLDINGS, QB: LTP_INFY}),
    req("holdings_priced_from_a_quote_when_the_row_has_none", "/api/v1/holdings", {}, {HB: HOLDINGS_NO_PRICE, QB: LTP_INFY}),
    req("holdings_empty", "/api/v1/holdings", {}, {HB: NO_HOLDINGS}),
    req("funds", "/api/v1/funds", {}, {FB: FUNDS, PB: POSITIONS, QB: LTPS}),
    req("funds_two_exchanges_added_up", "/api/v1/funds", {}, {FB: FUNDS_SPLIT, PB: NO_POSITIONS}),
    req("funds_positions_unreadable_uses_the_booked_figures", "/api/v1/funds", {}, {FB: FUNDS, PB: {"status": 500, "body": {"status": "error", "message": "down"}}}),
    req("funds_refused", "/api/v1/funds", {}, {FB: {"status": 401, "body": {"status": "error", "message": "Invalid token"}}}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {PB: POSITIONS, QB: LTPS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {PB: POSITIONS, PLACE_EP: ORDER_OK, QB: LTPS}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {PB: NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {PB: POSITIONS, PLACE_EP: ORDER_OK, QB: LTPS}),
]
