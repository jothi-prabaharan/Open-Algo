"""Upstox scenarios: OpenAlgo requests and the Upstox v2/v3 replies they meet."""

AUTH_TOKEN = "upstox-access-token"
ENV = {"BROKER_API_KEY": "upstox-api-key", "BROKER_API_SECRET": "upstox-secret"}

SBIN, INFY, FUT = "NSE_EQ|INE062A01020", "NSE_EQ|INE009A01021", "NSE_FO|35001"
SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE_EQ", "token": SBIN, "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE_EQ", "token": INFY, "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY FUT 25 NOV 26", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FO", "token": FUT, "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "NSE_INDEX|Nifty 50", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

ORDER_OK = {"body": {"status": "success", "data": {"order_ids": ["251103000000001"]}, "metadata": {"latency": 30}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100500", "message": "Insufficient funds", "propertyPath": None, "invalidValue": None, "error_code": "UDAPI100500", "property_path": None, "invalid_value": None}]}}


def _order(oid, exch, token, sym, side, otype, product, qty, price, trigger, status, ts, avg=0):
    return {"exchange": exch, "product": product, "price": price, "quantity": qty, "status": status, "tag": "openalgo", "instrument_token": token,
            "placed_by": "AB1234", "tradingsymbol": sym, "trading_symbol": sym, "order_type": otype, "validity": "DAY", "trigger_price": trigger,
            "disclosed_quantity": 0, "transaction_type": side, "average_price": avg, "filled_quantity": qty if status == "complete" else 0,
            "pending_quantity": 0 if status == "complete" else qty, "status_message": None, "exchange_order_id": "x" + oid, "parent_order_id": None,
            "order_id": oid, "variety": "SIMPLE", "order_timestamp": ts, "exchange_timestamp": ts, "is_amo": False, "order_request_id": "1", "order_ref_id": "r" + oid}


ORDERS = {"body": {"status": "success", "data": [
    _order("3001", "NSE", SBIN, "SBIN", "BUY", "LIMIT", "I", 10, 800.5, 0, "open", "2026-11-03 09:20:01"),
    _order("3002", "NSE", INFY, "INFY", "SELL", "MARKET", "D", 5, 0, 0, "complete", "2026-11-03 09:21:11", avg=1499.95),
    _order("3003", "NFO", FUT, "NIFTY FUT 25 NOV 26", "BUY", "SL", "D", 75, 24010, 24000, "trigger pending", "2026-11-03 09:22:00"),
    _order("3004", "NSE", SBIN, "SBIN", "SELL", "SL-M", "I", 1, 0, 790.123, "rejected", "2026-11-03 09:23:00"),
    _order("3005", "NSE", SBIN, "SBIN", "BUY", "LIMIT", "I", 2, 700, 0, "modify pending", "2026-11-03 09:24:00"),
    _order("3006", "NSE", "NSE_EQ|INE000000000", "UNLISTED", "BUY", "LIMIT", "D", 1, 10, 0, "cancelled", "2026-11-03 09:25:00"),
]}}
TRADES = {"body": {"status": "success", "data": [
    {"exchange": "NSE", "product": "D", "trading_symbol": "INFY", "tradingsymbol": "INFY", "instrument_token": INFY, "order_type": "MARKET", "transaction_type": "SELL", "quantity": 5, "exchange_order_id": "x3002", "order_id": "3002", "exchange_timestamp": "2026-11-03 09:21:12", "average_price": 1499.95, "trade_id": "t1", "order_ref_id": "r3002", "order_timestamp": "2026-11-03 09:21:11"},
    {"exchange": "NFO", "product": "D", "trading_symbol": "NIFTY FUT 25 NOV 26", "tradingsymbol": "NIFTY FUT 25 NOV 26", "instrument_token": FUT, "order_type": "LIMIT", "transaction_type": "BUY", "quantity": 75, "exchange_order_id": "x3007", "order_id": "3007", "exchange_timestamp": "2026-11-03 09:25:01", "average_price": 24005.555, "trade_id": "t2", "order_ref_id": "r3007", "order_timestamp": "2026-11-03 09:25:00"},
]}}


def _pos(exch, token, sym, product, qty, avg, ltp, pnl, realised, unrealised, buy_price=0, sell_price=0):
    return {"exchange": exch, "multiplier": 1, "value": 0, "pnl": pnl, "product": product, "instrument_token": token, "average_price": avg,
            "buy_value": 0, "overnight_quantity": 0, "day_buy_value": 0, "day_buy_price": buy_price, "overnight_buy_amount": 0,
            "overnight_buy_quantity": 0, "day_buy_quantity": max(qty, 0), "day_sell_value": 0, "day_sell_price": sell_price,
            "overnight_sell_amount": 0, "overnight_sell_quantity": 0, "day_sell_quantity": max(-qty, 0), "quantity": qty, "last_price": ltp,
            "unrealised": unrealised, "realised": realised, "sell_value": 0, "tradingsymbol": sym, "trading_symbol": sym, "close_price": ltp,
            "buy_price": buy_price, "sell_price": sell_price}


POSITIONS = {"body": {"status": "success", "data": [
    _pos("NSE", SBIN, "SBIN", "I", 10, 801.1234, 805.456, 43.3333, 0, 43.3333),
    _pos("NFO", FUT, "NIFTY FUT 25 NOV 26", "D", -75, None, 23990, 1162.5, 0, 1162.5, sell_price=24005.5),
    _pos("NSE", INFY, "INFY", "D", 0, 0, 1499, -2.5, -2.5, 0),
]}}
HOLDINGS_NULL = None
HOLDINGS = {"body": {"status": "success", "data": [
    {"isin": "INE009A01021", "cnc_used_quantity": 0, "collateral_type": "WC", "company_name": "INFOSYS", "haircut": 0.2, "product": "D", "quantity": 20, "tradingsymbol": "INFY", "trading_symbol": "INFY", "last_price": 1499.0, "close_price": 1490.0, "pnl": 1980.0, "day_change": 9.0, "day_change_percentage": 0.6, "instrument_token": INFY, "average_price": 1400.0, "collateral_quantity": 0, "collateral_update_quantity": 0, "t1_quantity": 0, "exchange": "NSE"},
    {"isin": "INE000000000", "cnc_used_quantity": 0, "collateral_type": "WC", "company_name": "OLDCO", "haircut": 0.2, "product": "D", "quantity": 3, "tradingsymbol": "OLDCO", "trading_symbol": "OLDCO", "last_price": 11.25, "close_price": 11.0, "pnl": -3.75, "day_change": 0, "day_change_percentage": 0, "instrument_token": "NSE_EQ|INE000000000", "average_price": 12.5, "collateral_quantity": 0, "collateral_update_quantity": 0, "t1_quantity": 0, "exchange": "NSE"},
]}}
FUNDS = {"body": {"status": "success", "data": {"available_to_trade": {
    "cash_available_to_trade": {"total": 98500.0, "margin_used": {"total": 274.95}},
    "pledge_available_to_trade": {"margin_from_pledge": {"total": 2000.0}, "margin_used": {"total": 500.0}},
}}}}


def _q(token, sym, ltp, prev, buy=(), sell=(), oi=0):
    return {"ohlc": {"open": 800.0, "high": 810.0, "low": 795.5, "close": ltp}, "depth": {"buy": [{"quantity": q, "price": p, "orders": 1} for p, q in buy], "sell": [{"quantity": q, "price": p, "orders": 1} for p, q in sell]},
            "timestamp": "2026-11-03T09:30:00.000+05:30", "instrument_token": token, "symbol": sym, "last_price": ltp, "volume": 1234567, "average_price": ltp,
            "oi": oi, "net_change": 5.5, "total_buy_quantity": 170, "total_sell_quantity": 97, "lower_circuit_limit": 700, "upper_circuit_limit": 900,
            "last_trade_time": "1793686800000", "oi_day_high": 0, "oi_day_low": 0, "prev_close_price": prev, "last_quantity": 5}


QUOTE_SBIN = {"body": {"status": "success", "data": {"NSE_EQ:SBIN": _q(SBIN, "SBIN", 805.45, 799.95, buy=[(805.4, 120), (805.35, 50)], sell=[(805.5, 80), (805.55, 10), (805.6, 7)])}}}

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}
PLACE_URL = "POST /v3/order/place"
BOOK = "GET /v2/order/retrieve-all"
POS = "GET /v2/portfolio/short-term-positions"
QUOTE = "GET /v3/market-quote/quotes"
CANCEL = "DELETE /v3/order/cancel"
MODIFY = "PUT /v3/order/modify"

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5, "disclosed_quantity": "2"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_slm", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL-M", "price": "805", "trigger_price": "801.05"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_REFUSED}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPE"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "modifyorder", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "3001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {MODIFY: {"body": {"status": "success", "data": {"order_id": "3001"}}}}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "3002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {MODIFY: {"status": 400, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100049", "message": "Order is already complete"}]}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "3001"}, "broker": {CANCEL: {"body": {"status": "success", "data": {"order_id": "3001"}}}}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "3002"}, "broker": {CANCEL: {"status": 400, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100049", "message": "Order is already complete"}]}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {BOOK: ORDERS, CANCEL: {"body": {"status": "success", "data": {"order_id": "x"}}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "closeposition_one_exit_refused", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: POSITIONS, PLACE_URL: [ORDER_OK, ORDER_REFUSED]}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "4"}, "broker": {POS: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "placesmartorder_matched", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "10"}, "broker": {POS: POSITIONS}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {BOOK: ORDERS}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /v2/order/trades/get-trades-for-day": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {POS: POSITIONS}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /v2/portfolio/long-term-holdings": HOLDINGS}},
    {"name": "holdings_unpriced", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /v2/portfolio/long-term-holdings": {"body": {"status": "success", "data": [dict(HOLDINGS["body"]["data"][0]), {**HOLDINGS["body"]["data"][1], "last_price": 0, "pnl": None}]}}}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /v3/user/get-funds-and-margin": FUNDS, POS: POSITIONS}},
    {"name": "funds_outside_hours", "path": "/api/v1/funds", "request": {}, "broker": {"GET /v3/user/get-funds-and-margin": {"status": 423, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100072", "message": "The Funds service is accessible from 5:30 AM to 12:00 AM IST daily"}]}}}},
    {"name": "funds_token_expired", "path": "/api/v1/funds", "request": {}, "broker": {"GET /v3/user/get-funds-and-margin": {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "3002"}, "broker": {BOOK: ORDERS, "GET /v2/order/trades/get-trades-for-day": TRADES}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {POS: POSITIONS}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: QUOTE_SBIN}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: QUOTE_SBIN}},
    {"name": "quotes_refused", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]

# Several quotes in one full-quote call (pass 6); INFY has no quote in the reply.
QUOTE_MULTI = {"body": {"status": "success", "data": {"NSE_EQ:SBIN": _q(SBIN, "SBIN", 805.45, 799.95, buy=[(805.4, 120)], sell=[(805.5, 80)]),
                                                      "NSE_INDEX:Nifty 50": _q("NSE_INDEX|Nifty 50", "Nifty 50", 24050.5, 24000.0)}}}
SCENARIOS.append({"name": "multiquotes", "path": "/api/v1/multiquotes", "request": {"symbols": [
    {"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}]},
    "broker": {QUOTE: QUOTE_MULTI}})
SCENARIOS.append({"name": "multiquotes_refused", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]},
                  "broker": {QUOTE: {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}}})
