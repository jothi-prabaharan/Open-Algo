"""Wisdom Capital (XTS) scenarios: OpenAlgo requests and the XTS replies they meet.

Replies follow the shapes in the XTS interactive API documentation. Paths carry the /interactive prefix of the Wisdom base address.
"""

AUTH_TOKEN = "wisdom-session-token"
ENV = {"BROKER_API_KEY": "wisdom-app-key", "BROKER_API_SECRET": "wisdom-app-secret",
       "BROKER_API_KEY_MARKET": "wisdom-market-key", "BROKER_API_SECRET_MARKET": "wisdom-market-secret"}
SEED_API_KEY_ROW = True


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN", "NSE", "NSECM", "22", "STATE BANK OF INDIA"),
    _sym("INFY", "INFY", "NSE", "NSECM", "1594", "INFOSYS"),
    _sym("NIFTY25NOV26FUT", "NIFTY", "NFO", "NSEFO", "35001", "NIFTY", 75, "FUTIDX", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY", "NFO", "NSEFO", "40001", "NIFTY", 75, "OPTIDX", "25-NOV-26", 24000.0),
    _sym("CRUDEOIL", "CRUDEOIL", "MCX", "MCXFO", "50001", "CRUDEOIL", 100, "FUTCOM", "", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR", "CDS", "NSECD", "60001", "USDINR", 1000, "FUTCUR", "25-NOV-26", -1.0, 0.0025),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS", "price": "0", "trigger_price": "0", "disclosed_quantity": "0"}

ORDER_OK = {"body": {"type": "success", "code": "s-orders-0001", "description": "Request sent", "result": {"AppOrderID": 1100000001, "ClientID": "WC001", "OrderUniqueIdentifier": "openalgo"}}}
ORDER_REFUSED = {"status": 400, "body": {"type": "error", "code": "e-orders-0005", "description": "Insufficient funds", "result": {}}}
CANCEL_OK = {"body": {"type": "success", "code": "s-orders-0001", "description": "Request sent", "result": {"AppOrderID": 1100000001, "ClientID": "WC001"}}}


def _order(oid, seg, iid, sym, side, otype, product, qty, price, stop, status, when):
    return {"AppOrderID": oid, "ClientID": "WC001", "ExchangeSegment": seg, "ExchangeInstrumentID": iid, "TradingSymbol": sym, "OrderSide": side,
            "OrderType": otype, "ProductType": product, "TimeInForce": "DAY", "OrderQuantity": qty, "OrderPrice": price, "OrderStopPrice": stop,
            "OrderStatus": status, "OrderAverageTradedPrice": str(price) if status == "Filled" else "", "LeavesQuantity": 0 if status == "Filled" else qty,
            "CumulativeQuantity": qty if status == "Filled" else 0, "LastUpdateDateTime": when, "OrderGeneratedDateTime": when}


ORDERS = {"body": {"type": "success", "result": [
    _order(2001, "NSECM", 22, "SBIN-EQ", "BUY", "Limit", "MIS", 10, 800.5, 0, "New", "2026-11-03T09:20:01"),
    _order(2002, "NSECM", 1594, "INFY-EQ", "SELL", "Market", "CNC", 5, 1499.95, 0, "Filled", "2026-11-03T09:21:11"),
    _order(2003, "NSEFO", 35001, "NIFTY", "BUY", "StopLimit", "NRML", 75, 24010, 24000, "Trigger Pending", "2026-11-03T09:22:00"),
    _order(2004, "NSECM", 22, "SBIN-EQ", "SELL", "StopMarket", "MIS", 1, 0, 790.5, "Rejected", "2026-11-03T09:23:00"),
    _order(2005, "NSECM", 22, "SBIN-EQ", "BUY", "Limit", "MIS", 2, 700, 0, "Cancelled", "2026-11-03T09:24:00"),
    _order(2006, "NSECM", 99999, "UNLISTED", "BUY", "Limit", "NRML", 1, 10, 0, "PartiallyFilled", "2026-11-03T09:25:00"),
    _order(2007, "MCXFO", 50001, "CRUDEOIL", "BUY", "Limit", "NRML", 1, 6500, 0, "New", "2026-11-03T09:26:00"),
    _order("2010.0", "NSECM", 22, "SBIN-EQ", "SELL", "Limit", "MIS", 4, 802, 0, "Cancelled", "2026-11-03T09:28:00"),
    _order(2011, "NSECM", 22, "SBIN-EQ", "SELL", "Limit", "MIS", 4, 803, 0, "Cancelled", "2026-11-03T09:29:00"),
]}}
TRADES = {"body": {"type": "success", "result": [
    _order(2002, "NSECM", 1594, "INFY-EQ", "SELL", "Market", "CNC", 5, 1499.95, 0, "Filled", "2026-11-03T09:21:11") | {"OrderAverageTradedPrice": "1499.95"},
    _order(2008, "NSEFO", 35001, "NIFTY", "BUY", "Limit", "NRML", 75, 24005.55, 0, "Filled", "2026-11-03T09:25:00") | {"OrderAverageTradedPrice": "24005.555"},
    _order(2009, "NSECM", 22, "SBIN-EQ", "BUY", "Limit", "MIS", 3, 801, 0, "Filled", "2026-11-03T09:27:00") | {"OrderAverageTradedPrice": ""},
]}}


def _pos(seg, iid, sym, product, qty, buy, sell):
    return {"AccountID": "WC001", "ExchangeSegment": seg, "ExchangeInstrumentId": iid, "ProductType": product, "Quantity": str(qty),
            "BuyAveragePrice": str(buy), "SellAveragePrice": str(sell), "TradingSymbol": sym, "MTM": "12.5"}


POSITIONS = {"body": {"type": "success", "result": {"positionList": [
    _pos("NSECM", 22, "SBIN-EQ", "MIS", 10, 801.1234, 0),
    _pos("NSEFO", 35001, "NIFTY", "NRML", -75, 0, 24005.5),
    _pos("NSECM", 1594, "INFY-EQ", "CNC", 0, 1500, 1499.5),
    _pos("NSECM", 99999, "UNLISTED", "NRML", 3, 12.0, 0),
]}}}
HOLDINGS = {"body": {"type": "success", "result": {"RMSHoldings": {"Holdings": {
    "INE009A01021": {"ExchangeNSEInstrumentId": 1594, "HoldingQuantity": 20, "BuyAvgPrice": 1400.0},
    "INE111111111": {"ExchangeNSEInstrumentId": 77777, "HoldingQuantity": 4, "BuyAvgPrice": 250.5},
}}}}}
BALANCE_NAN = {"body": {"type": "success", "result": {"BalanceList": [{"limitObject": {"RMSSubLimits": {
    "netMarginAvailable": "nan", "collateral": "NaN", "UnrealizedMTM": "n/a", "RealizedMTM": 7.005, "marginUtilized": None}}}]}}}
BALANCE = {"body": {"type": "success", "result": {"BalanceList": [{"limitObject": {"RMSSubLimits": {
    "netMarginAvailable": 101500.456, "collateral": 1500, "UnrealizedMTM": -12.5, "RealizedMTM": 30, "marginUtilized": 274.949}}}]}}}

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5, "disclosed_quantity": "2"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_slm_option", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "action": "SELL", "quantity": "75", "pricetype": "SL-M", "product": "NRML", "trigger_price": "120.3"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_mcx", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "CRUDEOIL", "exchange": "MCX", "quantity": "1", "pricetype": "LIMIT", "product": "NRML", "price": "6500"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_currency", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "1", "pricetype": "LIMIT", "product": "NRML", "price": "84.5"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /interactive/orders": ORDER_REFUSED}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPE"}, "broker": {"POST /interactive/orders": ORDER_OK}},
    {"name": "modifyorder_limit", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "2001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /interactive/orders": ORDER_OK}},
    {"name": "placeorder_error_type_carrying_an_order_number", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /interactive/orders": {"body": {"type": "error", "description": "Rejected by risk", "result": {"AppOrderID": 1100000009}}}}},
    {"name": "modifyorder_stop_limit", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "orderid": "2003", "action": "BUY", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24020", "disclosed_quantity": "5", "trigger_price": "24005"}, "broker": {"PUT /interactive/orders": ORDER_OK}},
    {"name": "modifyorder_stop_market_cnc", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "INFY", "exchange": "NSE", "orderid": "2012", "action": "SELL", "quantity": "5", "pricetype": "SL-M", "product": "CNC", "price": "0", "disclosed_quantity": "0", "trigger_price": "1490.5"}, "broker": {"PUT /interactive/orders": ORDER_OK}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "2002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /interactive/orders": {"status": 400, "body": {"type": "error", "code": "e-orders-0004", "description": "Order already filled", "result": {}}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "2001"}, "broker": {"DELETE /interactive/orders": CANCEL_OK}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "2002"}, "broker": {"DELETE /interactive/orders": {"status": 400, "body": {"type": "error", "code": "e-orders-0004", "description": "Order cannot be cancelled", "result": {}}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {"GET /interactive/orders": ORDERS, "DELETE /interactive/orders": CANCEL_OK}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /interactive/portfolio/positions": POSITIONS, "POST /interactive/orders": ORDER_OK}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "4"}, "broker": {"GET /interactive/portfolio/positions": POSITIONS, "POST /interactive/orders": ORDER_OK}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /interactive/orders": ORDERS}},
    {"name": "orderbook_empty", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /interactive/orders": {"body": {"type": "success", "result": []}}}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /interactive/orders/trades": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /interactive/portfolio/positions": POSITIONS}},
    {"name": "positionbook_empty", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /interactive/portfolio/positions": {"body": {"type": "success", "result": {"positionList": []}}}}},
    {"name": "positionbook_no_result", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /interactive/portfolio/positions": {"body": {"type": "success", "result": {}}}}},
    {"name": "tradebook_empty", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /interactive/orders/trades": {"body": {"type": "success", "result": []}}}},
    {"name": "holdings_empty", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /interactive/portfolio/holdings": {"body": {"type": "success", "result": {"RMSHoldings": {"Holdings": {}}}}}}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /interactive/portfolio/holdings": HOLDINGS}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": BALANCE}},
    {"name": "funds_nan_and_missing_figures", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": BALANCE_NAN}},
    {"name": "funds_session_expired", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": {"status": 401, "body": {"type": "error", "code": "e-session-0002", "description": "Session expired"}}}},
    {"name": "funds_session_code_on_a_bad_request", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": {"status": 400, "body": {"type": "error", "code": "e-session-0001", "description": "Invalid token"}}}},
    {"name": "funds_empty", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": {"body": {"type": "success", "result": {"BalanceList": []}}}}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "2002"}, "broker": {"GET /interactive/orders": ORDERS, "GET /interactive/orders/trades": TRADES}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {"GET /interactive/portfolio/positions": POSITIONS}},
]
