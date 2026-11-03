"""Delta Exchange (India) scenarios: OpenAlgo requests and the Delta v2 replies they meet.

Delta signs every private request (HMAC-SHA256 over method, timestamp, path, query and
body); the signature and timestamp change every second, so they are not recorded but
checked by recomputing them. Order and trade books keep only today's IST entries, so
the rows are dated at recording time, and RECORDED_AT pins the replay's clock.
"""

from datetime import datetime, timedelta, timezone

AUTH_TOKEN = "delta-api-key"  # OpenAlgo stores the Delta API key as the session token
ENV = {"BROKER_API_KEY": "delta-api-key", "BROKER_API_SECRET": "delta-api-secret"}
NOW = datetime.now(timezone.utc).replace(microsecond=0)
RECORDED_AT = NOW.isoformat()


def _ts(minutes_ago):
    return (NOW - timedelta(minutes=minutes_ago)).strftime("%Y-%m-%dT%H:%M:%S.000000Z")


YESTERDAY = (NOW - timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%S.000000Z")

SYMBOLS = [
    {"symbol": "BTCUSD", "brsymbol": "BTCUSD", "name": "Bitcoin perpetual", "exchange": "CRYPTO", "brexchange": "CRYPTO", "token": "27", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "PERPFUT", "tick_size": 0.5, "contract_value": 0.001},
    {"symbol": "ETHUSD", "brsymbol": "ETHUSD", "name": "Ether perpetual", "exchange": "CRYPTO", "brexchange": "CRYPTO", "token": "3136", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "PERPFUT", "tick_size": 0.05, "contract_value": 0.01},
    {"symbol": "SOL_INR", "brsymbol": "SOL_INR", "name": "Solana spot", "exchange": "CRYPTO", "brexchange": "CRYPTO", "token": "1001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "SPOT", "tick_size": 0.01, "contract_value": 1.0},
]

ORDER_OK = {"body": {"success": True, "result": {"id": 9001, "product_id": 27, "product_symbol": "BTCUSD", "size": 1, "side": "buy", "state": "open"}}}
ORDER_REFUSED = {"status": 400, "body": {"success": False, "error": {"code": "insufficient_margin", "context": {"available_balance": "10.0", "required_additional_balance": "57.0"}}}}


def _order(oid, pid, sym, side, otype, size, limit, stop, state, created, stop_type=None):
    o = {"id": oid, "product_id": pid, "product_symbol": sym, "side": side, "order_type": otype, "state": state, "size": size,
         "unfilled_size": size if state == "open" else 0, "limit_price": limit, "stop_price": stop, "reduce_only": False, "created_at": created,
         "user_id": 1, "time_in_force": "gtc", "average_fill_price": None}
    if stop_type:
        o["stop_order_type"] = stop_type
    return o


OPEN = {"body": {"success": True, "result": [
    _order(9001, 27, "BTCUSD", "buy", "limit_order", 2, "66000.0", None, "open", _ts(30)),
    _order(9003, 3136, "ETHUSD", "sell", "limit_order", 5, "3400.0", "3410.0", "pending", _ts(20), stop_type="stop_loss_order"),
]}}
HISTORY = {"body": {"success": True, "result": [
    _order(9002, 27, "BTCUSD", "sell", "market_order", 1, None, None, "closed", _ts(25)),
    _order(9004, 3136, "ETHUSD", "buy", "market_order", 1, None, "3300.0", "cancelled", _ts(15), stop_type="stop_loss_order"),
    _order(8999, 27, "BTCUSD", "buy", "limit_order", 1, "60000.0", None, "closed", YESTERDAY),
]}}
FILLS = {"body": {"success": True, "result": [
    {"id": 1, "product_id": 27, "product_symbol": "BTCUSD", "side": "sell", "size": 1, "price": "67012.5", "order_id": 9002, "created_at": _ts(25)},
    {"id": 2, "product_id": 1001, "product_symbol": "SOL_INR", "side": "buy", "size": "0.5", "price": "15000.25", "order_id": 9100, "created_at": _ts(10)},
    {"id": 0, "product_id": 27, "product_symbol": "BTCUSD", "side": "buy", "size": 1, "price": "60000.0", "order_id": 8999, "created_at": YESTERDAY},
]}}
MARGINED = {"body": {"success": True, "result": [
    {"product_id": 27, "product_symbol": "BTCUSD", "size": 2, "entry_price": "66500.5", "realized_pnl": "12.5", "unrealized_pnl": "-3.25", "margin": "13.3"},
    {"product_id": 3136, "product_symbol": "ETHUSD", "size": -3, "entry_price": "3450.0", "realized_pnl": "0", "unrealized_pnl": "4.5", "margin": "10.35"},
]}}
WALLET_INR = {"body": {"success": True, "result": [
    {"asset_id": 14, "asset_symbol": "INR", "balance": "100000.0", "blocked_margin": "274.95", "balance_inr": "101500.5", "cross_locked_collateral": "1500.0"},
]}}
WALLET_SPOT = {"body": {"success": True, "result": [
    {"asset_id": 14, "asset_symbol": "INR", "balance": "100000.0", "blocked_margin": "274.95", "balance_inr": "101500.5", "cross_locked_collateral": "1500.0"},
    {"asset_id": 1001, "asset_symbol": "SOL", "balance": "0.75", "blocked_margin": "0.25", "balance_inr": "7500.0", "cross_locked_collateral": "0"},
]}}
TICKER = {"body": {"success": True, "result": {"symbol": "BTCUSD", "product_id": 27, "mark_price": "67000.5", "open": 66000, "high": 68000, "low": 65000, "close": 66500, "volume": 1234.7, "oi": "1500.5", "quotes": {"best_bid": "67000.0", "best_ask": "67001.0", "bid_size": "120", "ask_size": "80"}}}}
L2 = {"body": {"success": True, "result": {"buy": [{"price": "67000.0", "size": 1500, "depth": 1500}, {"price": "66999.5", "size": 200, "depth": 1700}], "sell": [{"price": "67001.0", "size": 800, "depth": 800}]}}}

PLACE = {"strategy": "t", "symbol": "BTCUSD", "exchange": "CRYPTO", "action": "BUY", "quantity": "1", "pricetype": "MARKET", "product": "NRML"}
POS = "GET /v2/positions/margined"
WALLET = "GET /v2/wallet/balances"

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "BTCUSD"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_limit", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "ETHUSD", "action": "SELL", "quantity": 3, "pricetype": "LIMIT", "price": 3450.5}, "broker": {"POST /v2/orders": {"body": {"success": True, "result": {"id": 9010, "product_id": 3136}}}}},
    {"name": "placeorder_sl", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL", "price": "68010", "trigger_price": "68000"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_slm", "path": "/api/v1/placeorder", "request": {**PLACE, "action": "SELL", "pricetype": "SL-M", "trigger_price": "65000.5"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {"POST /v2/orders": ORDER_REFUSED}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPEUSD"}, "broker": {"POST /v2/orders": ORDER_OK}},
    {"name": "modifyorder", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "BTCUSD", "exchange": "CRYPTO", "orderid": "27:9001", "action": "BUY", "quantity": "3", "pricetype": "LIMIT", "product": "NRML", "price": "66100", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /v2/orders": {"body": {"success": True, "result": {"id": 9001, "product_id": 27}}}}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "BTCUSD", "exchange": "CRYPTO", "orderid": "27:9002", "action": "BUY", "quantity": "3", "pricetype": "LIMIT", "product": "NRML", "price": "66100", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /v2/orders": {"status": 400, "body": {"success": False, "error": {"code": "open_order_not_found"}}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "27:9001"}, "broker": {"DELETE /v2/orders": {"body": {"success": True, "result": {"id": 9001, "state": "cancelled"}}}}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "27:9002"}, "broker": {"DELETE /v2/orders": {"status": 400, "body": {"success": False, "error": {"code": "open_order_not_found"}}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {"DELETE /v2/orders/all": {"body": {"success": True, "result": {}}}, "GET /v2/orders": OPEN, "GET /v2/orders/history": HISTORY, "DELETE /v2/orders": {"body": {"success": True, "result": {}}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: MARGINED, WALLET: WALLET_INR, "POST /v2/orders": ORDER_OK}},
    {"name": "closeposition_with_spot", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: MARGINED, WALLET: WALLET_SPOT, "POST /v2/orders": ORDER_OK}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "1"}, "broker": {POS: MARGINED, WALLET: WALLET_INR, "POST /v2/orders": ORDER_OK}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /v2/orders": OPEN, "GET /v2/orders/history": HISTORY}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /v2/fills": FILLS}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {POS: MARGINED, WALLET: WALLET_SPOT}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {WALLET: WALLET_SPOT, POS: MARGINED}},
    {"name": "funds_key_refused", "path": "/api/v1/funds", "request": {}, "broker": {WALLET: {"status": 401, "body": {"success": False, "error": {"code": "invalid_api_key"}}}}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "27:9002"}, "broker": {"GET /v2/orders": OPEN, "GET /v2/orders/history": HISTORY, "GET /v2/fills": FILLS}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "ETHUSD", "exchange": "CRYPTO", "product": "NRML"}, "broker": {POS: MARGINED, WALLET: WALLET_INR}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "BTCUSD", "exchange": "CRYPTO"}, "broker": {"GET /v2/tickers/BTCUSD": TICKER}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "BTCUSD", "exchange": "CRYPTO"}, "broker": {"GET /v2/tickers/BTCUSD": TICKER, "GET /v2/l2orderbook/27": L2}},
    {"name": "depth_book_unavailable", "path": "/api/v1/depth", "request": {"symbol": "BTCUSD", "exchange": "CRYPTO"}, "broker": {"GET /v2/tickers/BTCUSD": TICKER, "GET /v2/l2orderbook/27": {"status": 503, "body": {"success": False, "error": {"code": "service_unavailable"}}}}},
    {"name": "quotes_refused", "path": "/api/v1/quotes", "request": {"symbol": "BTCUSD", "exchange": "CRYPTO"}, "broker": {"GET /v2/tickers/BTCUSD": {"status": 404, "body": {"success": False, "error": {"code": "not_found"}}}}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]

# No batched quote at Delta Exchange: /multiquotes asks for each ticker in turn (pass 6).
SCENARIOS.append({"name": "multiquotes", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "BTCUSD", "exchange": "CRYPTO"}, {"symbol": "ETHUSD", "exchange": "CRYPTO"}, {"symbol": "NOPE", "exchange": "CRYPTO"}]},
                  "broker": {"GET /v2/tickers/BTCUSD": TICKER, "GET /v2/tickers/ETHUSD": {"status": 404, "body": {"success": False, "error": {"code": "not_found"}}}}})
