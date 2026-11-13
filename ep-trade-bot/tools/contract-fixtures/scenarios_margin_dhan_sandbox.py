"""/margin on Dhan Sandbox: the multi-order calculator first, then one call per position if it refuses."""

import scenarios_dhan_sandbox as s

BROKER = "dhan_sandbox"
CLIENT_ID, AUTH_TOKEN, ENV, SYMBOLS = s.CLIENT_ID, s.AUTH_TOKEN, s.ENV, s.SYMBOLS
SEED_API_KEY_ROW = True
ONE_EP, MULTI_EP = "POST /v2/margincalculator", "POST /v2/margincalculator/multi"
READ_POSTS = ["/v2/margincalculator", "/v2/margincalculator/multi", *s.READ_POSTS]
ONE = {"body": {"totalMargin": 1608.05, "spanMargin": 100.5, "exposureMargin": 20.25, "availableBalance": 99999.0, "variableMargin": 1487.3, "insufficientBalance": 0.0, "brokerage": 20.0, "leverage": "5.00"}}
ONE_B = {"body": {"totalMargin": 2000.0, "spanMargin": 50.0, "exposureMargin": 10.0, "availableBalance": 99999.0, "brokerage": 15.0}}
MULTI = {"body": {"total_margin": "191119.00", "span_margin": "179780.00", "exposure_margin": "11339.00", "equity_margin": "0.00", "fo_margin": "191119.00", "commodity_margin": "0.00", "currency": "INR", "hedge_benefit": "67020.00"}}
MULTI_BLANKS = {"body": {"total_margin": "5000.50", "span_margin": "", "exposure_margin": None}}
REFUSED = {"status": 400, "body": {"errorType": "Margin_Error", "errorCode": "DH-905", "errorMessage": "Missing required fields, bad values for parameters etc."}}
REFUSED_OK = {"body": {"status": "failed", "errorMessage": "Invalid Client ID"}}
EXPIRED = {"status": 401, "body": {"errorType": "Invalid_Authentication", "errorCode": "DH-901", "errorMessage": "Client ID or user generated access token is invalid or expired."}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_goes_to_the_multi_calculator_first", [pos()], {MULTI_EP: MULTI}),
    margin("margin_two_positions_use_the_multi_calculator", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {MULTI_EP: MULTI}),
    margin("margin_multi_answer_with_blank_figures", [pos(), pos("INFY")], {MULTI_EP: MULTI_BLANKS}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {MULTI_EP: MULTI}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {MULTI_EP: MULTI}),
    margin("margin_cnc_equity", [pos(product="CNC")], {MULTI_EP: MULTI}),
    margin("margin_multi_refused_one_position_falls_back_to_the_single_calculator", [pos()], {MULTI_EP: REFUSED, ONE_EP: ONE}),
    margin("margin_multi_refused_ok_status_falls_back", [pos()], {MULTI_EP: REFUSED_OK, ONE_EP: ONE}),
    margin("margin_multi_refused_two_positions_are_added_up", [pos(), pos("INFY")], {MULTI_EP: REFUSED, ONE_EP: [ONE, ONE_B]}),
    margin("margin_both_calculators_refuse", [pos()], {MULTI_EP: REFUSED, ONE_EP: REFUSED}),
    margin("margin_both_calculators_refuse_with_ok_status", [pos()], {MULTI_EP: REFUSED, ONE_EP: REFUSED_OK}),
    margin("margin_token_expired", [pos()], {MULTI_EP: EXPIRED, ONE_EP: EXPIRED}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {MULTI_EP: MULTI}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_no_positions", [], {}),
]
