"""/margin on Dhan: one position to the single-order calculator, several to the multi-order one."""

import scenarios_dhan as d

BROKER = "dhan"
CLIENT_ID, AUTH_TOKEN, ENV, SYMBOLS = d.CLIENT_ID, d.AUTH_TOKEN, d.ENV, d.SYMBOLS
SEED_API_KEY_ROW = True
ONE_EP, MULTI_EP = "POST /v2/margincalculator", "POST /v2/margincalculator/multi"
READ_POSTS = ["/v2/margincalculator", "/v2/margincalculator/multi"]
ONE = {"body": {"totalMargin": 1608.05, "spanMargin": 100.5, "exposureMargin": 20.25, "availableBalance": 99999.0, "variableMargin": 1487.3, "insufficientBalance": 0.0, "brokerage": 20.0, "leverage": "5.00"}}
MULTI = {"body": {"total_margin": "191119.00", "span_margin": "179780.00", "exposure_margin": "11339.00", "equity_margin": "0.00", "fo_margin": "191119.00", "commodity_margin": "0.00", "currency": "INR", "hedge_benefit": "67020.00"}}
MULTI_CAMEL = {"body": {"totalMargin": 5000.5, "spanMargin": "", "exposure": 120.0}}
REFUSED = {"status": 400, "body": {"errorType": "Margin_Error", "errorCode": "DH-905", "errorMessage": "Missing required fields, bad values for parameters etc."}}
REFUSED_OK = {"body": {"status": "failed", "data": {"810": "Invalid Client ID"}}}
EXPIRED = {"status": 401, "body": {"errorType": "Invalid_Authentication", "errorCode": "DH-901", "errorMessage": "Client ID or user generated access token is invalid or expired."}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_uses_the_single_calculator", [pos()], {ONE_EP: ONE}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {ONE_EP: ONE}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {ONE_EP: ONE}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {ONE_EP: ONE}),
    margin("margin_cnc_equity", [pos(product="CNC")], {ONE_EP: ONE}),
    margin("margin_two_positions_use_the_multi_calculator", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {MULTI_EP: MULTI}),
    margin("margin_multi_with_camel_case_answer", [pos(), pos("INFY")], {MULTI_EP: MULTI_CAMEL}),
    margin("margin_broker_refuses_with_an_error_status", [pos()], {ONE_EP: REFUSED}),
    margin("margin_broker_refuses_with_ok_status", [pos()], {ONE_EP: REFUSED_OK}),
    margin("margin_multi_broker_refuses", [pos(), pos("INFY")], {MULTI_EP: REFUSED}),
    margin("margin_token_expired", [pos()], {ONE_EP: EXPIRED}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {ONE_EP: ONE}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
