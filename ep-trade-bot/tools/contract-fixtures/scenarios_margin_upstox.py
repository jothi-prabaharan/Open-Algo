"""/margin on Upstox (v2 charges/margin)."""

import scenarios_upstox as u

BROKER = "upstox"
AUTH_TOKEN, ENV, SYMBOLS = u.AUTH_TOKEN, u.ENV, u.SYMBOLS
EP = "POST /v2/charges/margin"
READ_POSTS = ["/v2/charges/margin"]
ONE = {"body": {"status": "success", "data": {"required_margin": 1608.05, "final_margin": 1608.05, "margins": [{"span_margin": 100.5, "exposure_margin": 20.25, "equity_margin": 0, "net_buy_premium": 0, "additional_margin": 0, "tender_margin": 0}]}}}
TWO = {"body": {"status": "success", "data": {"required_margin": 258139.0, "final_margin": 191119.0, "margins": [{"span_margin": 179780.0, "exposure_margin": 11339.0}, {"span_margin": 0, "exposure_margin": 0}]}}}
REFUSED = {"status": 400, "body": {"status": "error", "errors": [{"errorCode": "UDAPI1021", "message": "Invalid Instrument key", "propertyPath": "instruments"}]}}
REFUSED_OK = {"body": {"status": "error", "errors": [{"errorCode": "UDAPI1999", "message": "Margin cannot be calculated for this instrument"}]}}
EXPIRED = {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}


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
    margin("margin_twenty_positions_accepted", [pos()] * 20, {EP: ONE}),
    margin("margin_twenty_one_positions_refused", [pos()] * 21, {EP: ONE}),
]
