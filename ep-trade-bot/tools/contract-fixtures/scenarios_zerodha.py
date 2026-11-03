"""Zerodha scenarios: OpenAlgo requests and the Kite replies they meet.

Kite replies follow the shapes documented at kite.trade/docs/connect/v3.
"""

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "779521::::3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "408065::::1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY26NOVFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "13238786::::51714", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUT", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "NIFTY 50", "name": "NIFTY 50", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "256265::::1001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

ORDER_OK = {"body": {"status": "success", "data": {"order_id": "151220000000000"}}}
ORDERS = {
    "body": {
        "status": "success",
        "data": [
            {"order_id": "151220000000000", "exchange": "NSE", "tradingsymbol": "SBIN", "transaction_type": "BUY", "order_type": "LIMIT", "product": "MIS", "quantity": 10, "filled_quantity": 0, "pending_quantity": 10, "disclosed_quantity": 0, "cancelled_quantity": 0, "price": 800.5, "trigger_price": 0, "average_price": 0, "status": "OPEN", "order_timestamp": "2026-11-03 09:20:01", "tag": "openalgo"},
            {"order_id": "151220000000001", "exchange": "NSE", "tradingsymbol": "INFY", "transaction_type": "SELL", "order_type": "MARKET", "product": "CNC", "quantity": 5, "filled_quantity": 5, "pending_quantity": 0, "disclosed_quantity": 0, "cancelled_quantity": 0, "price": 0, "trigger_price": 0, "average_price": 1500.25, "status": "COMPLETE", "order_timestamp": "2026-11-03 09:21:11", "tag": None},
            {"order_id": "151220000000002", "exchange": "NFO", "tradingsymbol": "NIFTY26NOVFUT", "transaction_type": "BUY", "order_type": "SL", "product": "NRML", "quantity": 75, "filled_quantity": 0, "pending_quantity": 75, "disclosed_quantity": 0, "cancelled_quantity": 0, "price": 24010, "trigger_price": 24000, "average_price": 0, "status": "TRIGGER PENDING", "order_timestamp": "2026-11-03 09:22:00", "tag": None},
            {"order_id": "151220000000005", "exchange": "NSE", "tradingsymbol": "INFY", "transaction_type": "BUY", "order_type": "MARKET", "product": "MIS", "quantity": 2, "filled_quantity": 2, "pending_quantity": 0, "disclosed_quantity": 0, "cancelled_quantity": 0, "price": 1499.95, "trigger_price": 0, "average_price": 1499.95, "status": "COMPLETE", "order_timestamp": "2026-11-03 09:24:00", "tag": None},
            {"order_id": "151220000000003", "exchange": "NSE", "tradingsymbol": "SBIN", "transaction_type": "SELL", "order_type": "LIMIT", "product": "MIS", "quantity": 1, "filled_quantity": 0, "pending_quantity": 0, "disclosed_quantity": 0, "cancelled_quantity": 1, "price": 900.123, "trigger_price": 0, "average_price": 0, "status": "REJECTED", "order_timestamp": "2026-11-03 09:23:00", "tag": None},
        ],
    }
}
TRADES = {
    "body": {
        "status": "success",
        "data": [
            {"trade_id": "10000001", "order_id": "151220000000001", "exchange": "NSE", "tradingsymbol": "INFY", "product": "CNC", "transaction_type": "SELL", "quantity": 5, "average_price": 1500.25, "fill_timestamp": "2026-11-03 09:21:12", "order_timestamp": "09:21:11"},
            {"trade_id": "10000002", "order_id": "151220000000004", "exchange": "NFO", "tradingsymbol": "NIFTY26NOVFUT", "product": "NRML", "transaction_type": "BUY", "quantity": 75, "average_price": 24005.5, "fill_timestamp": None, "order_timestamp": "2026-11-03 09:25:00"},
        ],
    }
}
POSITIONS = {
    "body": {
        "status": "success",
        "data": {
            "net": [
                {"tradingsymbol": "SBIN", "exchange": "NSE", "product": "MIS", "quantity": 10, "overnight_quantity": 0, "buy_quantity": 10, "sell_quantity": 0, "day_buy_quantity": 10, "day_sell_quantity": 0, "average_price": 801.1234, "last_price": 805.456, "pnl": 43.3333, "buy_value": 8011.234, "sell_value": 0, "multiplier": 1},
                {"tradingsymbol": "NIFTY26NOVFUT", "exchange": "NFO", "product": "NRML", "quantity": -75, "overnight_quantity": 0, "buy_quantity": 0, "sell_quantity": 75, "day_buy_quantity": 0, "day_sell_quantity": 75, "average_price": 24005.5, "last_price": 23990, "pnl": 1162.5, "buy_value": 0, "sell_value": 1800412.5, "multiplier": 1},
                {"tradingsymbol": "INFY", "exchange": "NSE", "product": "CNC", "quantity": 0, "overnight_quantity": 0, "buy_quantity": 5, "sell_quantity": 5, "day_buy_quantity": 5, "day_sell_quantity": 5, "average_price": 0, "last_price": 1499, "pnl": -2.5, "buy_value": 7500.0, "sell_value": 7497.5, "multiplier": 1},
            ],
            "day": [],
        },
    }
}
HOLDINGS = {
    "body": {
        "status": "success",
        "data": [
            {"tradingsymbol": "INFY", "exchange": "NSE", "quantity": 20, "product": "CNC", "average_price": 1400.0, "last_price": 1499.0, "pnl": 1980.0},
            {"tradingsymbol": "SBIN", "exchange": "NSE", "quantity": 3, "product": "CNC", "average_price": 0, "last_price": None, "pnl": None},
        ],
    }
}
MARGINS = {
    "body": {
        "status": "success",
        "data": {
            "equity": {"net": 99725.05, "available": {"cash": 100000.0, "collateral": 1500.0, "intraday_payin": 0, "opening_balance": 100000.0, "live_balance": 99725.05}, "utilised": {"debits": 274.95, "m2m_realised": -10.5, "m2m_unrealised": 43.3333, "option_premium": 0, "span": 0, "exposure": 0}},
            "commodity": {"net": 0, "available": {"cash": 0, "collateral": 0, "intraday_payin": 0, "opening_balance": 0, "live_balance": 0}, "utilised": {"debits": 0, "m2m_realised": 0, "m2m_unrealised": 0, "option_premium": 0, "span": 0, "exposure": 0}},
        },
    }
}
LTP = {"body": {"status": "success", "data": {"NSE:SBIN": {"instrument_token": 779521, "last_price": 806.0}, "NFO:NIFTY26NOVFUT": {"instrument_token": 13238786, "last_price": 23980.25}}}}
QUOTE_SBIN = {
    "body": {
        "status": "success",
        "data": {
            "NSE:SBIN": {
                "instrument_token": 779521, "last_price": 805.45, "last_quantity": 5, "volume": 1234567, "oi": 0,
                "ohlc": {"open": 800.0, "high": 810.0, "low": 795.5, "close": 799.95},
                "depth": {
                    "buy": [{"price": 805.4, "quantity": 120, "orders": 3}, {"price": 805.35, "quantity": 50, "orders": 1}],
                    "sell": [{"price": 805.5, "quantity": 80, "orders": 2}, {"price": 805.55, "quantity": 10, "orders": 1}, {"price": 805.6, "quantity": 7, "orders": 1}],
                },
            }
        },
    }
}

SCENARIOS = [
    # Authentication and validation, answered before any broker call.
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "auth_missing_key", "path": "/api/v1/funds", "request": {}, "auth": "none"},
    {"name": "auth_header_key", "path": "/api/v1/funds", "request": {}, "auth": "header", "broker": {"GET /user/margins": MARGINS, "GET /portfolio/positions": POSITIONS, "GET /quote/ltp": LTP}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_bad_action", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "HOLD", "quantity": "1", "pricetype": "MARKET", "product": "MIS"}},
    {"name": "placeorder_zero_quantity", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "0", "pricetype": "MARKET", "product": "MIS"}},
    {"name": "placeorder_bad_exchange", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "LSE", "action": "BUY", "quantity": "1", "pricetype": "MARKET", "product": "MIS"}},
    # Orders.
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}, "broker": {"POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "placeorder_limit", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5}, "broker": {"POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "action": "BUY", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {"POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}, "broker": {"POST /orders/regular": {"status": 400, "body": {"status": "error", "message": "Insufficient funds. Required margin is 8054.50 but available margin is 100.00.", "error_type": "InputException"}}}, "ignore": ["sent[*].form.tag"]},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {"strategy": "t", "symbol": "NOPE", "exchange": "NSE", "action": "BUY", "quantity": "1", "pricetype": "MARKET", "product": "MIS"}, "broker": {"POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "modifyorder", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "151220000000000", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /orders/regular/151220000000000": ORDER_OK}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "151220000000000"}, "broker": {"DELETE /orders/regular/151220000000000": ORDER_OK}},
    {"name": "cancelorder_missing_id", "path": "/api/v1/cancelorder", "request": {"strategy": "t"}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {"GET /orders": ORDERS, "DELETE /orders/regular/151220000000000": {"body": {"status": "success", "data": {"order_id": "151220000000000"}}}, "DELETE /orders/regular/151220000000002": {"body": {"status": "success", "data": {"order_id": "151220000000002"}}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /portfolio/positions": POSITIONS, "POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "0", "position_size": "4", "pricetype": "MARKET", "product": "MIS"}, "broker": {"GET /portfolio/positions": POSITIONS, "POST /orders/regular": ORDER_OK}, "ignore": ["sent[*].form.tag"]},
    {"name": "placesmartorder_matched", "path": "/api/v1/placesmartorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "0", "position_size": "10", "pricetype": "MARKET", "product": "MIS"}, "broker": {"GET /portfolio/positions": POSITIONS}},
    # Books.
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /orders": ORDERS}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /trades": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /portfolio/positions": POSITIONS}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /portfolio/holdings": HOLDINGS}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /user/margins": MARGINS, "GET /portfolio/positions": POSITIONS, "GET /quote/ltp": LTP}},
    {"name": "orderstatus_found", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "151220000000001"}, "broker": {"GET /orders": ORDERS, "GET /trades": TRADES}},
    {"name": "orderstatus_missing", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "999"}, "broker": {"GET /orders": ORDERS}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {"GET /portfolio/positions": POSITIONS}},
    {"name": "openposition_none", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "INFY", "exchange": "NSE", "product": "MIS"}, "broker": {"GET /portfolio/positions": POSITIONS}},
    # Market data.
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /quote": QUOTE_SBIN}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /quote": QUOTE_SBIN}},
    {"name": "quotes_missing_symbol", "path": "/api/v1/quotes", "request": {"exchange": "NSE"}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]
