"""/margin on IIFL Capital: one span and exposure call for every leg (POST /spanexposure), each leg only an instrument, exchange, side and quantity."""

import scenarios_iiflcapital as s
from scenarios_iiflcapital import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "iiflcapital"
READ_POSTS = ["/spanexposure"]
EP = "POST /v1/spanexposure"


def ok(span=1000.5, exposure=200.25, total=None):
    result = {"span": span, "exposureMargin": exposure}
    if total is not None:
        result["totalMargin"] = total
    return {"body": {"status": "Ok", "result": result}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position", [pos()], {EP: ok()}),
    margin("margin_the_total_is_the_brokers_when_it_sends_one", [pos()], {EP: ok(total=1500.75)}),
    margin("margin_the_total_is_span_plus_exposure_when_it_does_not", [pos()], {EP: ok()}),
    margin("margin_a_future_sold", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {EP: ok(100000.456, 20000.25)}),
    margin("margin_several_legs_go_in_one_call", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos("NIFTY25NOV2624000CE", "NFO", "BUY", "75", "NRML"), pos()], {EP: ok(50000, 10000, 55000.5)}),
    margin("margin_every_exchange_code", [pos("SBIN"), pos("TATAMOTORS", "BSE"), pos("SENSEX26NOVFUT", "BFO", "BUY", "20", "NRML"), pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML"), pos("USDINR25NOV26FUT", "CDS", "BUY", "1000", "NRML")], {EP: ok()}),
    margin("margin_text_figures", [pos()], {EP: {"body": {"status": "Ok", "result": {"span": "1000.5", "exposureMargin": "x", "totalMargin": ""}}}}),
    margin("margin_figures_at_the_top_level", [pos()], {EP: {"body": {"status": "Ok", "span": 10, "exposureMargin": 5}}}),
    margin("margin_nothing_in_the_answer", [pos()], {EP: {"body": {"status": "Ok", "result": {}}}}),
    margin("margin_a_result_that_is_not_an_object", [pos()], {EP: {"body": {"status": "Ok", "result": [1, 2]}}}),
    margin("margin_refused_with_an_error_status", [pos()], {EP: {"status": 400, "body": {"status": "error", "message": "Invalid instrument"}}}),
    margin("margin_refused_without_a_message", [pos()], {EP: {"status": 400, "body": {"status": "error"}}}),
    margin("margin_an_answer_that_is_not_json_text", [pos()], {EP: {"status": 500, "body": {"x": 1}}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {EP: ok()}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
