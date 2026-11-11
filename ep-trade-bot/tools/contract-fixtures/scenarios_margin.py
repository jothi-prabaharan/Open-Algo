"""/margin on Zerodha: one position goes to Kite's orders endpoint, several to its basket endpoint."""

import scenarios_history as h

BROKER = "zerodha"
SYMBOLS = [
    *h.SYMBOLS,
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "408065::::1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
]

ORDERS = "POST /margins/orders"
BASKET = "POST /margins/basket"
READ_POSTS = ["/margins/orders", "/margins/basket"]


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


ONE = {"body": {"status": "success", "data": [{"type": "equity", "tradingsymbol": "SBIN", "exchange": "NSE", "span": 0, "exposure": 0, "option_premium": 0, "additional": 0, "bo": 0, "cash": 0, "var": 1608.05, "total": 1608.05, "leverage": 5}]}}
TWO_ORDERS = {"body": {"status": "success", "data": [
    {"type": "equity", "tradingsymbol": "SBIN", "span": 100.5, "exposure": 20.25, "total": 1608.05},
    {"type": "equity", "tradingsymbol": "INFY", "span": 50, "exposure": 10, "total": 3000},
]}}
BASKET_OK = {"body": {"status": "success", "data": {
    "initial": {"type": "", "span": 0, "exposure": 0, "option_premium": 0, "total": 258139.0},
    "final": {"type": "", "span": 179780.0, "exposure": 11339.0, "option_premium": 0, "additional": 0, "bo": 0, "cash": 0, "var": 0, "total": 191119.0},
    "orders": [{"tradingsymbol": "NIFTY25NOV26FUT", "span": 179780.0, "exposure": 11339.0, "total": 191119.0}],
}}}
BASKET_EMPTY = {"body": {"status": "success", "data": {}}}
REFUSED = {"status": 400, "body": {"status": "error", "message": "Invalid tradingsymbol.", "error_type": "InputException"}}
EXPIRED = {"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_uses_the_orders_endpoint", [pos()], {ORDERS: ONE}),
    margin("margin_limit_order_with_prices", [pos(pricetype="LIMIT", price="805.5", trigger_price="0")], {ORDERS: ONE}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {ORDERS: ONE}),
    margin("margin_futures", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {ORDERS: ONE}),
    margin("margin_two_positions_use_the_basket_endpoint", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos("SBIN")], {BASKET: BASKET_OK}),
    margin("margin_basket_with_no_figures", [pos(), pos("INFY")], {BASKET: BASKET_EMPTY}),
    margin("margin_orders_summed", [pos()], {ORDERS: TWO_ORDERS}),
    margin("margin_broker_refuses", [pos()], {ORDERS: REFUSED}),
    margin("margin_token_expired", [pos()], {ORDERS: EXPIRED}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {ORDERS: ONE}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_quantity_with_decimals", [pos(quantity="10.5")], {}),
    margin("margin_quantity_zero", [pos(quantity="0")], {}),
    margin("margin_second_position_bad", [pos(), pos(quantity="-3")], {}),
    margin("margin_price_not_a_number", [pos(price="abc")], {}),
    margin("margin_price_negative", [pos(price="-1")], {}),
    margin("margin_no_positions", [], {}),
    margin("margin_fifty_one_positions", [pos()] * 51, {}),
    margin("margin_bad_action", [pos(action="HOLD")], {}),
    margin("margin_bad_product", [pos(product="XYZ")], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
    {"name": "margin_bad_position_with_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos(quantity="ten")]}, "broker": {}},
]
