"""/margin on Fyers (v3 multiorder margin)."""

import scenarios_fyers as f

BROKER = "fyers"
AUTH_TOKEN, ENV, SYMBOLS = f.AUTH_TOKEN, f.ENV, f.SYMBOLS
EP = "POST /api/v3/multiorder/margin"
READ_POSTS = ["/api/v3/multiorder/margin"]
ONE = {"body": {"s": "ok", "code": 200, "message": "", "data": {"margin_avail": 99999.9, "margin_total": 1700.0, "margin_new_order": 1608.05}}}
TWO = {"body": {"s": "ok", "code": 200, "message": "", "data": {"margin_avail": 99999.9, "margin_total": 200000.0, "margin_new_order": 191119.0}}}
REFUSED = {"status": 400, "body": {"s": "error", "code": -50, "message": "Invalid input"}}
REFUSED_OK = {"body": {"s": "error", "code": -99, "message": "Margin cannot be calculated for this instrument"}}
EXPIRED = {"status": 401, "body": {"s": "error", "code": -16, "message": "Could not authenticate the user"}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position", [pos()], {EP: ONE}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {EP: ONE}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {EP: ONE}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {EP: ONE}),
    margin("margin_cnc_equity", [pos(product="CNC")], {EP: ONE}),
    margin("margin_two_positions", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {EP: TWO}),
    margin("margin_broker_refuses_with_an_error_status", [pos()], {EP: REFUSED}),
    margin("margin_broker_refuses_with_ok_status", [pos()], {EP: REFUSED_OK}),
    margin("margin_token_expired", [pos()], {EP: EXPIRED}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {EP: ONE}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_price_negative", [pos(price="-1")], {}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
