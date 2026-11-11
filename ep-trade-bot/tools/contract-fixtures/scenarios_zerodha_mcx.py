"""Zerodha on MCX: OpenAlgo counts units, Kite counts contracts, so a quantity is divided by the lot going out and multiplied coming back."""

import scenarios_zerodha as z

BROKER = "zerodha"


def _fut(symbol, brsymbol, token, name, lot, expiry="18-DEC-30"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": "MCX", "brexchange": "MCX", "token": token, "expiry": expiry, "strike": -1.0, "lotsize": lot, "instrumenttype": "FUT", "tick_size": 1.0}


SYMBOLS = [
    *z.SYMBOLS,
    _fut("CRUDEOIL18DEC30FUT", "CRUDEOIL30DECFUT", "70000001::::470001", "CRUDEOIL", 100),
    _fut("GOLD05DEC30FUT", "GOLD30DECFUT", "70000002::::470002", "GOLD", 1, "05-DEC-30"),
    _fut("GOLDPETAL05DEC30FUT", "GOLDPETAL30DECFUT", "70000003::::470003", "GOLDPETAL", 1, "05-DEC-30"),
    _fut("SILVERM05DEC30FUT", "SILVERM30DECFUT", "70000004::::470004", "SILVERM", 5, "05-DEC-30"),
    _fut("MCXBULLDEX18DEC30FUT", "MCXBULLDEX30DECFUT", "70000005::::470005", "MCXBULLDEX", 15),
]

OK = {"body": {"status": "success", "data": {"order_id": "151220000000777"}}}


def order(symbol="CRUDEOIL18DEC30FUT", quantity="100", **extra):
    return {"strategy": "t", "symbol": symbol, "exchange": "MCX", "action": "BUY", "quantity": quantity, "pricetype": "MARKET", "product": "NRML", **extra}


def _row(order_id, symbol, qty, status="COMPLETE", **extra):
    return {"order_id": order_id, "exchange": "MCX", "tradingsymbol": symbol, "transaction_type": "BUY", "order_type": "MARKET", "product": "NRML",
            "quantity": qty, "filled_quantity": qty if status == "COMPLETE" else 0, "pending_quantity": 0 if status == "COMPLETE" else qty, "disclosed_quantity": 0,
            "cancelled_quantity": 0, "price": 0, "trigger_price": 0, "average_price": 0, "status": status, "order_timestamp": "2026-11-03 09:20:01", **extra}


ORDERS = {"body": {"status": "success", "data": [
    _row("151220000000001", "CRUDEOIL30DECFUT", 2),
    _row("151220000000002", "GOLD30DECFUT", 3, "OPEN"),
    _row("151220000000003", "SILVERM30DECFUT", 4, "OPEN"),
]}}
TRADES = {"body": {"status": "success", "data": [
    {"trade_id": "10000001", "order_id": "151220000000001", "exchange": "MCX", "tradingsymbol": "CRUDEOIL30DECFUT", "product": "NRML", "transaction_type": "BUY", "quantity": 2, "average_price": 6500.5, "fill_timestamp": "2026-11-03 09:21:12", "order_timestamp": "2026-11-03 09:21:11"},
    {"trade_id": "10000002", "order_id": "151220000000002", "exchange": "MCX", "tradingsymbol": "GOLD30DECFUT", "product": "NRML", "transaction_type": "SELL", "quantity": 3, "average_price": 7500.25, "fill_timestamp": "2026-11-03 09:25:00", "order_timestamp": "2026-11-03 09:25:00"},
    {"trade_id": "10000003", "order_id": "151220000000003", "exchange": "MCX", "tradingsymbol": "SILVERM30DECFUT", "product": "NRML", "transaction_type": "BUY", "quantity": 4, "average_price": 90000.0, "fill_timestamp": "2026-11-03 09:26:00", "order_timestamp": "2026-11-03 09:26:00"},
    {"trade_id": "10000004", "order_id": "151220000000004", "exchange": "MCX", "tradingsymbol": "GOLDPETAL30DECFUT", "product": "NRML", "transaction_type": "BUY", "quantity": 4, "average_price": 7500.0, "fill_timestamp": "2026-11-03 09:27:00", "order_timestamp": "2026-11-03 09:27:00"},
]}}


def _pos(symbol, qty, avg=6500.0, last=6510.0, pnl=100.0):
    return {"tradingsymbol": symbol, "exchange": "MCX", "product": "NRML", "quantity": qty, "overnight_quantity": 0, "buy_quantity": abs(qty), "sell_quantity": 0,
            "day_buy_quantity": abs(qty), "day_sell_quantity": 0, "average_price": avg, "last_price": last, "pnl": pnl, "buy_value": 0, "sell_value": 0, "multiplier": 1}


POSITIONS = {"body": {"status": "success", "data": {"net": [_pos("CRUDEOIL30DECFUT", 2), _pos("GOLD30DECFUT", -3)], "day": []}}}
POSITIONS_UNSIZED = {"body": {"status": "success", "data": {"net": [_pos("CRUDEOIL30DECFUT", 2), _pos("MCXBULLDEX30JANFUT", 1)], "day": []}}}
POSITIONS_CRUDE = {"body": {"status": "success", "data": {"net": [_pos("CRUDEOIL30DECFUT", 2)], "day": []}}}
MARGIN_ONE = {"body": {"status": "success", "data": [{"type": "commodity", "tradingsymbol": "CRUDEOIL30DECFUT", "span": 100.5, "exposure": 20.25, "total": 1608.05}]}}

SCENARIOS = [
    {"name": "mcx_placeorder_one_lot", "path": "/api/v1/placeorder", "request": order(), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_two_lots", "path": "/api/v1/placeorder", "request": order(quantity="200"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_not_a_whole_lot", "path": "/api/v1/placeorder", "request": order(quantity="150"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_disclosed_not_a_whole_lot", "path": "/api/v1/placeorder", "request": order(quantity="200", disclosed_quantity="50"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_disclosed_a_whole_lot", "path": "/api/v1/placeorder", "request": order(quantity="200", disclosed_quantity="100"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_lot_of_one", "path": "/api/v1/placeorder", "request": order("GOLD05DEC30FUT", "3"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_gold_petal_not_read_as_gold", "path": "/api/v1/placeorder", "request": order("GOLDPETAL05DEC30FUT", "4"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_silver_mini_lot_of_five", "path": "/api/v1/placeorder", "request": order("SILVERM05DEC30FUT", "10"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_placeorder_revised_size_from_the_master_row", "path": "/api/v1/placeorder", "request": order("MCXBULLDEX18DEC30FUT", "30"), "broker": {"POST /orders/regular": OK, "GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_modifyorder_quantity", "path": "/api/v1/modifyorder", "request": {"orderid": "151220000000002", "strategy": "t", "symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "product": "NRML", "pricetype": "LIMIT", "price": "6500", "quantity": "300", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /orders/regular/151220000000002": OK}},
    {"name": "mcx_modifyorder_disclosed_a_whole_lot", "path": "/api/v1/modifyorder", "request": {"orderid": "151220000000002", "strategy": "t", "symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "product": "NRML", "pricetype": "LIMIT", "price": "6500", "quantity": "300", "disclosed_quantity": "200", "trigger_price": "0"}, "broker": {"PUT /orders/regular/151220000000002": OK}},
    {"name": "mcx_modifyorder_disclosed_not_a_whole_lot", "path": "/api/v1/modifyorder", "request": {"orderid": "151220000000002", "strategy": "t", "symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "product": "NRML", "pricetype": "LIMIT", "price": "6500", "quantity": "300", "disclosed_quantity": "50", "trigger_price": "0"}, "broker": {"PUT /orders/regular/151220000000002": OK}},
    {"name": "mcx_modifyorder_not_a_whole_lot", "path": "/api/v1/modifyorder", "request": {"orderid": "151220000000002", "strategy": "t", "symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "product": "NRML", "pricetype": "LIMIT", "price": "6500", "quantity": "250", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"PUT /orders/regular/151220000000002": OK}},
    {"name": "mcx_orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {"GET /orders": ORDERS}},
    {"name": "mcx_tradebook_value_uses_the_quotation_multiplier", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /trades": TRADES}},
    {"name": "mcx_positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /portfolio/positions": POSITIONS}},
    {"name": "mcx_positionbook_unsizable_root", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /portfolio/positions": POSITIONS_UNSIZED}},
    {"name": "mcx_openposition_in_units", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "product": "NRML"}, "broker": {"GET /portfolio/positions": POSITIONS_CRUDE}},
    {"name": "mcx_margin_one_lot", "path": "/api/v1/margin", "request": {"positions": [{"symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "quantity": "100", "product": "NRML", "pricetype": "MARKET"}]}, "broker": {"POST /margins/orders": MARGIN_ONE}},
    {"name": "mcx_margin_not_a_whole_lot", "path": "/api/v1/margin", "request": {"positions": [{"symbol": "CRUDEOIL18DEC30FUT", "exchange": "MCX", "action": "BUY", "quantity": "150", "product": "NRML", "pricetype": "MARKET"}]}, "broker": {"POST /margins/orders": MARGIN_ONE}},
    {"name": "mcx_closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /portfolio/positions": POSITIONS_CRUDE, "POST /orders/regular": OK}},
]
READ_POSTS = ["/margins/orders", "/margins/basket"]
