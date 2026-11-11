"""/margin on Angel One (SmartAPI batch margin)."""

import scenarios_angel as a

BROKER = "angel"
AUTH_TOKEN, ENV, SYMBOLS = a.AUTH_TOKEN, a.ENV, a.SYMBOLS
EP = "POST /rest/secure/angelbroking/margin/v1/batch"
READ_POSTS = ["/rest/secure/angelbroking/margin/v1/batch"]
OK = {"status": True, "message": "SUCCESS", "errorcode": ""}
ONE = {"body": {**OK, "data": {"totalMarginRequired": 1608.05, "marginComponents": {"netPremium": 0, "spanMargin": 100.5, "marginBenefit": 0, "deliveryMargin": 0, "nonNFOMargin": 1507.55, "totOptionsPremium": 0}}}}
TWO = {"body": {**OK, "data": {"totalMarginRequired": 191119.0, "marginComponents": {"spanMargin": 179780.0, "marginBenefit": 67020.0}}}}
REFUSED = {"status": 400, "body": {"status": False, "message": "Invalid token", "errorCode": "AB1010", "data": None}}
REFUSED_OK = {"body": {"status": False, "message": "Margin cannot be calculated for this instrument", "errorcode": "AB4000", "data": None}}
EXPIRED = {"status": 403, "body": {"message": "Invalid Token", "errorCode": "AG8001", "status": False}}


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
