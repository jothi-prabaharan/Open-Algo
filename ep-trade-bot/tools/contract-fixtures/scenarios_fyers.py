"""Fyers scenarios: OpenAlgo requests and the Fyers API v3 replies they meet."""

AUTH_TOKEN = "fyers-access-token"
ENV = {"BROKER_API_KEY": "FYAPPID-100", "BROKER_API_SECRET": "fyers-secret"}

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "NSE:SBIN-EQ", "name": "SBIN", "exchange": "NSE", "brexchange": "NSE", "token": "10100000003045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "NSE:INFY-EQ", "name": "INFY", "exchange": "NSE", "brexchange": "NSE", "token": "10100000001594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NSE:NIFTY26NOVFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "101126112535001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "NSE:NIFTY50-INDEX", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "101000000026000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

ORDER_OK = {"body": {"s": "ok", "code": 1101, "message": "Order submitted successfully. Your Order Ref. No.26110300000001", "id": "26110300000001"}}
ORDER_REFUSED = {"status": 400, "body": {"s": "error", "code": -50, "message": "Insufficient funds to place the order"}}


def _order(oid, exch, seg, sym, side, otype, product, qty, price, trigger, status, ts):
    return {"clientId": "XA00001", "id": oid, "exchOrdId": "x" + oid, "qty": qty, "remainingQuantity": 0 if status == 2 else qty, "filledQty": qty if status == 2 else 0,
            "discloseQty": 0, "limitPrice": price, "stopPrice": trigger, "tradedPrice": price if status == 2 else 0, "type": otype, "fyToken": "1",
            "exchange": exch, "segment": seg, "symbol": sym, "instrument": 0, "message": "", "offlineOrder": False, "orderDateTime": ts,
            "orderValidity": "DAY", "pan": "", "productType": product, "side": side, "status": status, "source": "API", "ex_sym": sym, "description": ""}


ORDERS = {"body": {"s": "ok", "code": 200, "message": "", "orderBook": [
    _order("4001", 10, 10, "NSE:SBIN-EQ", 1, 1, "INTRADAY", 10, 800.5, 0, 6, "03-Nov-2026 09:20:01"),
    _order("4002", 10, 10, "NSE:INFY-EQ", -1, 2, "CNC", 5, 1499.95, 0, 2, "03-Nov-2026 09:21:11"),
    _order("4003", 10, 11, "NSE:NIFTY26NOVFUT", 1, 4, "MARGIN", 75, 24010, 24000, 4, "03-Nov-2026 09:22:00"),
    _order("4004", 10, 10, "NSE:SBIN-EQ", -1, 3, "INTRADAY", 1, 0, 790.123, 5, "03-Nov-2026 09:23:00"),
    _order("4005", 10, 10, "NSE:UNKNOWN-EQ", 1, 1, "CNC", 2, 10, 0, 1, "03-Nov-2026 09:24:00"),
]}}
TRADES = {"body": {"s": "ok", "code": 200, "message": "", "tradeBook": [
    {"clientId": "XA00001", "orderDateTime": "03-Nov-2026 09:21:12", "orderNumber": "4002", "exchangeOrderNo": "x4002", "exchange": 10, "side": -1, "segment": 10, "orderType": 2, "fyToken": "1", "productType": "CNC", "tradedQty": 5, "tradePrice": 1499.95, "tradeValue": 7499.75, "tradeNumber": "t1", "row": 1, "symbol": "NSE:INFY-EQ"},
    {"clientId": "XA00001", "orderDateTime": "03-Nov-2026 09:25:01", "orderNumber": "4006", "exchangeOrderNo": "x4006", "exchange": 10, "side": 1, "segment": 11, "orderType": 1, "fyToken": "2", "productType": "MARGIN", "tradedQty": 75, "tradePrice": 24005.555, "tradeValue": 1800416.625, "tradeNumber": "t2", "row": 2, "symbol": "NSE:NIFTY26NOVFUT"},
]}}


def _pos(exch, seg, sym, product, qty, avg, ltp, pl, realized, unrealized):
    return {"netQty": qty, "qty": abs(qty), "avgPrice": avg, "netAvg": avg, "side": 1 if qty > 0 else -1 if qty < 0 else 0, "productType": product,
            "realized_profit": realized, "unrealized_profit": unrealized, "pl": pl, "ltp": ltp, "buyQty": max(qty, 0), "buyAvg": avg, "buyVal": 0,
            "sellQty": max(-qty, 0), "sellAvg": avg, "sellVal": 0, "slNo": 0, "fyToken": "1", "crossCurrency": "", "rbiRefRate": 1.0,
            "qtyMulti_com": 1.0, "segment": seg, "symbol": sym, "id": sym + "-" + product, "exchange": exch}


POSITIONS = {"body": {"s": "ok", "code": 200, "message": "", "netPositions": [
    _pos(10, 10, "NSE:SBIN-EQ", "INTRADAY", 10, 801.1234, 805.456, 43.3333, 0, 43.3333),
    _pos(10, 11, "NSE:NIFTY26NOVFUT", "MARGIN", -75, 24005.5, 23990, 1162.5, 0, 1162.5),
    _pos(10, 10, "NSE:INFY-EQ", "CNC", 0, 0, 1499, -2.5, -2.5, 0),
], "overall": {}}}
HOLDINGS = {"body": {"s": "ok", "code": 200, "message": "", "holdings": [
    {"holdingType": "HLD", "quantity": 20, "costPrice": 1400.0, "marketVal": 29980.0, "remainingQuantity": 20, "pl": 1980.0, "ltp": 1499.0, "id": 1, "fyToken": "1", "exchange": 10, "symbol": "NSE:INFY-EQ", "segment": 10, "isin": "INE009A01021", "qty_t1": 0, "remainingPledgeQuantity": 0, "collateralQuantity": 0},
    {"holdingType": "T1", "quantity": 3, "costPrice": 12.5, "marketVal": 33.75, "remainingQuantity": 3, "pl": -3.75, "ltp": 11.25, "id": 2, "fyToken": "2", "exchange": 10, "symbol": "NSE:OLDCO-EQ", "segment": 10, "isin": "INE000000000", "qty_t1": 3, "remainingPledgeQuantity": 0, "collateralQuantity": 0},
], "overall": {}}}
FUNDS = {"body": {"code": 200, "message": "", "s": "ok", "fund_limit": [
    {"id": 1, "title": "Total Balance", "equityAmount": 101500.0, "commodityAmount": 0.0},
    {"id": 2, "title": "Utilized Amount", "equityAmount": 274.95, "commodityAmount": 100.0},
    {"id": 3, "title": "Clear Balance", "equityAmount": 98500.0, "commodityAmount": 2000.0},
    {"id": 4, "title": "Realized Profit and Loss", "equityAmount": -2.5, "commodityAmount": 0.0},
    {"id": 5, "title": "Collaterals", "equityAmount": 1500.0, "commodityAmount": 0.0},
    {"id": 10, "title": "Available Balance", "equityAmount": 99725.05, "commodityAmount": 1900.0},
]}}
DEPTH = {"body": {"s": "ok", "message": "", "d": {"NSE:SBIN-EQ": {
    "totalbuyqty": 170, "totalsellqty": 97, "bids": [{"price": 805.4, "volume": 120, "ord": 3}, {"price": 805.35, "volume": 50, "ord": 1}],
    "ask": [{"price": 805.5, "volume": 80, "ord": 2}, {"price": 805.55, "volume": 10, "ord": 1}, {"price": 805.6, "volume": 7, "ord": 1}],
    "o": 800.0, "h": 810.0, "l": 795.5, "c": 799.95, "chp": 0.69, "tick_Size": 0.05, "ch": 5.5, "ltq": 5, "ltt": 1793686800, "ltp": 805.45,
    "v": 1234567, "atp": 803.1, "lower_ckt": 720.0, "upper_ckt": 880.0, "expiry": "", "oi": 0, "oiflag": False, "pdoi": 0, "oipercent": 0}}}}

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}
SYNC = "/api/v3/orders/sync"
BOOK = "GET /api/v3/orders"
POS = "GET /api/v3/positions"

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST " + SYNC: ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5, "disclosed_quantity": "2"}, "broker": {"POST " + SYNC: ORDER_OK}},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {"POST " + SYNC: ORDER_OK}},
    {"name": "placeorder_slm", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL-M", "trigger_price": "801.05"}, "broker": {"POST " + SYNC: ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST " + SYNC: ORDER_REFUSED}},
    {"name": "placeorder_refused_with_200", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST " + SYNC: {"body": {"s": "error", "code": -99, "message": "Order rejected by RMS"}}}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPE"}, "broker": {"POST " + SYNC: ORDER_OK}},
    {"name": "modifyorder", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "4001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PATCH " + SYNC: {"body": {"s": "ok", "code": 1102, "message": "Successfully modified order", "id": "4001"}}}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "4002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PATCH " + SYNC: {"status": 400, "body": {"s": "error", "code": -52, "message": "Order is already traded"}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "4001"}, "broker": {"DELETE " + SYNC: {"body": {"s": "ok", "code": 1103, "message": "Successfully cancelled order", "id": "4001"}}}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "4002"}, "broker": {"DELETE " + SYNC: {"status": 400, "body": {"s": "error", "code": -52, "message": "Order is already traded"}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {BOOK: ORDERS, "DELETE " + SYNC: {"body": {"s": "ok", "code": 1103, "message": "Successfully cancelled order", "id": "x"}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"DELETE /api/v3/positions": {"body": {"s": "ok", "code": 200, "message": "All positions are closed"}}, POS: POSITIONS, "POST " + SYNC: ORDER_OK}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "4"}, "broker": {POS: POSITIONS, "POST " + SYNC: ORDER_OK}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {BOOK: ORDERS}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /api/v3/tradebook": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {POS: POSITIONS}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /api/v3/holdings": HOLDINGS}},
    {"name": "funds_token_expired", "path": "/api/v1/funds", "request": {}, "broker": {"GET /api/v3/funds": {"status": 401, "body": {"s": "error", "code": -16, "message": "Could not authenticate the user"}}}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /api/v3/funds": FUNDS, POS: POSITIONS}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "4002"}, "broker": {BOOK: ORDERS, "GET /api/v3/tradebook": TRADES}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {POS: POSITIONS}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/depth": DEPTH}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/depth": DEPTH}},
    {"name": "quotes_refused", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/depth": {"status": 401, "body": {"s": "error", "code": -16, "message": "Could not authenticate the user"}}}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]
