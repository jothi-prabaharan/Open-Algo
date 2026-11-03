"""Dhan scenarios: OpenAlgo requests and the Dhan v2 replies they meet.

Dhan replies follow the shapes documented at dhanhq.co/docs/v2.
"""

CLIENT_ID = "1100000001"
AUTH_TOKEN = "dhan-access-token"
ENV = {"BROKER_API_KEY": f"{CLIENT_ID}:::dhan-app-key", "BROKER_API_SECRET": "dhan-app-secret"}
# Dhan reads quotes with a POST; it changes nothing, so it is compared as a read.
READ_POSTS = ["/v2/marketfeed/quote"]
# Positions and holdings price themselves through the quote service, which looks up the install's API key row.
SEED_API_KEY_ROW = True

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "BSE", "brexchange": "BSE", "token": "500209", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY-Nov2026-FUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FNO", "token": "35001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY-Nov2026-24000-CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FNO", "token": "40001", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "OPTIDX", "tick_size": 0.05},
    {"symbol": "BSEONLY", "brsymbol": "BSEONLY", "name": "LISTED ON BSE ONLY", "exchange": "BSE", "brexchange": "BSE", "token": "500325", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "NOTICK", "brsymbol": "NOTICK", "name": "NO TICK SIZE", "exchange": "NSE", "brexchange": "NSE", "token": "9999", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.0},
]

ORDER_OK = {"body": {"orderId": "112111182198", "orderStatus": "PENDING"}}
ORDER_REFUSED = {"status": 400, "body": {"errorType": "Order_Error", "errorCode": "DH-906", "errorMessage": "Incorrect order request: insufficient funds"}}


def _order(oid, seg, sid, sym, side, otype, product, qty, price, trigger, status, updated):
    return {"dhanClientId": CLIENT_ID, "orderId": oid, "correlationId": "", "orderStatus": status, "transactionType": side,
            "exchangeSegment": seg, "productType": product, "orderType": otype, "validity": "DAY", "tradingSymbol": sym,
            "securityId": sid, "quantity": qty, "disclosedQuantity": 0, "price": price, "triggerPrice": trigger,
            "afterMarketOrder": False, "createTime": updated, "updateTime": updated, "exchangeTime": updated,
            "filledQty": qty if status == "TRADED" else 0, "averageTradedPrice": price if status == "TRADED" else 0}


ORDERS = {"body": [
    _order("1001", "NSE_EQ", "3045", "SBIN", "BUY", "LIMIT", "INTRADAY", 10, 800.5, 0, "PENDING", "2026-11-03 09:20:01"),
    _order("1002", "NSE_EQ", "1594", "INFY", "SELL", "MARKET", "CNC", 5, 1499.95, 0, "TRADED", "2026-11-03 09:21:11"),
    _order("1003", "NSE_FNO", "35001", "NIFTY-Nov2026-FUT", "BUY", "STOP_LOSS", "MARGIN", 75, 24010, 24000, "PENDING", "2026-11-03 09:22:00"),
    _order("1004", "NSE_EQ", "3045", "SBIN", "SELL", "STOP_LOSS_MARKET", "INTRADAY", 1, 0, 790.123, "REJECTED", "2026-11-03 09:23:00"),
    _order("1005", "NSE_EQ", "3045", "SBIN", "BUY", "LIMIT", "INTRADAY", 2, 700, 0, "CANCELLED", "2026-11-03 09:24:00"),
    _order("1006", "NSE_EQ", "3045", "SBIN", "BUY", "LIMIT", "INTRADAY", 3, 701, 0, "TRANSIT", "2026-11-03 09:25:00"),
    _order("1007", "NSE_EQ", "77777", "UNLISTED", "BUY", "LIMIT", "MARGIN", 1, 10, 0, "PART_TRADED", "2026-11-03 09:26:00"),
]}
TRADES = {"body": [
    {"dhanClientId": CLIENT_ID, "orderId": "1002", "exchangeOrderId": "x1", "exchangeTradeId": "t1", "transactionType": "SELL", "exchangeSegment": "NSE_EQ", "productType": "CNC", "orderType": "MARKET", "tradingSymbol": "INFY", "securityId": "1594", "tradedQuantity": 5, "tradedPrice": 1499.95, "createTime": "2026-11-03 09:21:11", "updateTime": "2026-11-03 09:21:12", "exchangeTime": "2026-11-03 09:21:12"},
    {"dhanClientId": CLIENT_ID, "orderId": "1008", "exchangeOrderId": "x2", "exchangeTradeId": "t2", "transactionType": "BUY", "exchangeSegment": "NSE_FNO", "productType": "MARGIN", "orderType": "LIMIT", "tradingSymbol": "NIFTY-Nov2026-FUT", "securityId": "35001", "tradedQuantity": 75, "tradedPrice": 24005.555, "createTime": "2026-11-03 09:25:00", "updateTime": "2026-11-03 09:25:01", "exchangeTime": "2026-11-03 09:25:01"},
]}
POSITIONS = {"body": [
    {"dhanClientId": CLIENT_ID, "tradingSymbol": "SBIN", "securityId": "3045", "positionType": "LONG", "exchangeSegment": "NSE_EQ", "productType": "INTRADAY", "buyAvg": 801.1234, "buyQty": 10, "costPrice": 801.1234, "sellAvg": 0, "sellQty": 0, "netQty": 10, "realizedProfit": 0, "unrealizedProfit": 43.3333, "dayBuyQty": 10, "daySellQty": 0},
    {"dhanClientId": CLIENT_ID, "tradingSymbol": "NIFTY-Nov2026-FUT", "securityId": "35001", "positionType": "SHORT", "exchangeSegment": "NSE_FNO", "productType": "MARGIN", "buyAvg": 0, "buyQty": 0, "costPrice": 24005.5, "sellAvg": 24005.5, "sellQty": 75, "netQty": -75, "realizedProfit": 0, "unrealizedProfit": 1162.5, "dayBuyQty": 0, "daySellQty": 75},
    {"dhanClientId": CLIENT_ID, "tradingSymbol": "INFY", "securityId": "1594", "positionType": "CLOSED", "exchangeSegment": "NSE_EQ", "productType": "CNC", "buyAvg": 1500, "buyQty": 5, "costPrice": 0, "sellAvg": 1499.5, "sellQty": 5, "netQty": 0, "realizedProfit": -2.5, "unrealizedProfit": 0, "dayBuyQty": 5, "daySellQty": 5},
]}
HOLDINGS = {"body": [
    {"exchange": "ALL", "tradingSymbol": "INFOSYS", "securityId": "1594", "isin": "INE009A01021", "totalQty": 20, "dpQty": 20, "t1Qty": 0, "availableQty": 20, "collateralQty": 0, "avgCostPrice": 1400.0},
    {"exchange": "ALL", "tradingSymbol": "BSEONLY", "securityId": "500325", "isin": "INE111111111", "totalQty": 4, "dpQty": 4, "t1Qty": 0, "availableQty": 4, "collateralQty": 0, "avgCostPrice": 250.0},
    {"exchange": "ALL", "tradingSymbol": "OLDCO", "securityId": "55555", "isin": "INE000000000", "totalQty": 3, "dpQty": 3, "t1Qty": 0, "availableQty": 3, "collateralQty": 0, "avgCostPrice": 12.5},
]}
FUNDS = {"body": {"dhanClientId": CLIENT_ID, "availabelBalance": 101500.0, "sodLimit": 100000.0, "collateralAmount": 1500.0, "receiveableAmount": 0, "utilizedAmount": 274.95, "blockedPayoutAmount": 0, "withdrawableBalance": 98000.0}}


def _q(ltp, close, buy=(), sell=(), volume=1234567, oi=0, ltq=5):
    return {"last_price": ltp, "last_quantity": ltq, "volume": volume, "oi": oi,
            "ohlc": {"open": round(close * 1.001, 2), "high": round(ltp * 1.01, 2), "low": round(ltp * 0.99, 2), "close": close},
            "depth": {"buy": [{"price": p, "quantity": q, "orders": 1} for p, q in buy], "sell": [{"price": p, "quantity": q, "orders": 1} for p, q in sell]}}


QUOTES = {"body": {"status": "success", "data": {
    "NSE_EQ": {"3045": _q(805.45, 799.95, buy=[(805.4, 120), (805.35, 50)], sell=[(805.5, 80), (805.55, 10), (805.6, 7)]),
               "1594": _q(1499.0, 1490.0, buy=[(1498.9, 10)], sell=[(1499.1, 20)])},
    "NSE_FNO": {"35001": _q(23990.0, 24050.0, oi=1200000)},
    "BSE_EQ": {"500325": _q(262.5, 260.0)},
}}}

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5, "disclosed_quantity": "2"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_slm_buy_equity", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL-M", "trigger_price": "801.03"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_slm_sell_option", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "action": "SELL", "quantity": "75", "pricetype": "SL-M", "product": "NRML", "trigger_price": "120.3"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_slm_sell_cheap_option", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "action": "SELL", "quantity": "75", "pricetype": "SL-M", "product": "NRML", "trigger_price": "0.4"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_slm_no_tick_size", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOTICK", "pricetype": "SL-M", "trigger_price": "50"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /v2/orders": ORDER_REFUSED}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPE"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "modifyorder_limit", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /v2/orders/1001": {"body": {"orderId": "1001", "orderStatus": "PENDING"}}}},
    {"name": "modifyorder_slm", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1004", "action": "SELL", "quantity": "1", "pricetype": "SL-M", "product": "MIS", "price": "0", "disclosed_quantity": "1", "trigger_price": "790.1"}, "broker": {"PUT /v2/orders/1004": {"body": {"orderId": "1004", "orderStatus": "PENDING"}}}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /v2/orders/1002": {"status": 400, "body": {"errorType": "Order_Error", "errorCode": "DH-908", "errorMessage": "Order is already traded"}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "1001"}, "broker": {"DELETE /v2/orders/1001": {"body": {"orderId": "1001", "orderStatus": "CANCELLED"}}}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "1002"}, "broker": {"DELETE /v2/orders/1002": {"status": 400, "body": {"errorType": "Order_Error", "errorCode": "DH-908", "errorMessage": "Order is already traded"}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {"GET /v2/orders": ORDERS, "DELETE /v2/orders/1001": {"body": {"orderId": "1001", "orderStatus": "CANCELLED"}}, "DELETE /v2/orders/1003": {"body": {"orderId": "1003", "orderStatus": "CANCELLED"}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /v2/positions": POSITIONS, "POST /v2/orders": ORDER_OK, "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "closeposition_one_exit_refused", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /v2/positions": POSITIONS, "POST /v2/orders": [ORDER_OK, ORDER_REFUSED], "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "4"}, "broker": {"GET /v2/positions": POSITIONS, "POST /v2/orders": ORDER_OK, "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /v2/orders": ORDERS}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /v2/trades": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /v2/positions": POSITIONS, "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /v2/holdings": HOLDINGS, "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "holdings_none", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /v2/holdings": {"status": 500, "body": {"errorType": "Holding_Error", "errorCode": "DHOLDING_ERROR", "errorMessage": "No holdings available"}}}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /v2/fundlimit": FUNDS, "GET /v2/positions": POSITIONS}},
    {"name": "funds_token_expired", "path": "/api/v1/funds", "request": {}, "broker": {"GET /v2/fundlimit": {"status": 401, "body": {"errorType": "Invalid_Authentication", "errorCode": "DH-901", "errorMessage": "Client ID or user generated access token is invalid or expired."}}, "GET /v2/positions": POSITIONS}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "1002"}, "broker": {"GET /v2/orders": ORDERS, "GET /v2/trades": TRADES}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {"GET /v2/positions": POSITIONS, "POST /v2/marketfeed/quote": QUOTES}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"POST /v2/marketfeed/quote": QUOTES}},
    {"name": "quotes_futures", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, "broker": {"POST /v2/marketfeed/quote": QUOTES}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"POST /v2/marketfeed/quote": QUOTES}},
    {"name": "quotes_not_subscribed", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"POST /v2/marketfeed/quote": {"body": {"status": "failed", "data": {"806": "Data APIs not subscribed"}}}}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]

# Several quotes in one market-feed call (pass 6). The option has no quote in the reply.
SCENARIOS.append({"name": "multiquotes", "path": "/api/v1/multiquotes", "request": {"symbols": [
    {"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {"symbol": "NOPE", "exchange": "NSE"},
    {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}]}, "broker": {"POST /v2/marketfeed/quote": QUOTES}})
SCENARIOS.append({"name": "multiquotes_not_subscribed", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]},
                  "broker": {"POST /v2/marketfeed/quote": {"body": {"status": "failed", "data": {"806": "Data APIs not subscribed"}}}}})
