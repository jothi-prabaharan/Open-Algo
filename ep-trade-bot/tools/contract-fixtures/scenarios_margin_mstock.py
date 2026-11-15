"""/margin on mStock: one POST /margins/orders with every leg (product, side, quantity, price, exchange, symbol name, token, trigger price). The margin is the summary's total charges, with the span and exposure parts from its breakup."""

import scenarios_mstock as s
from scenarios_mstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V  # noqa: F401

BROKER = "mstock"
READ_POSTS = [f"{V}/margins/orders"]
EP = f"POST {V}/margins/orders"
SYMBOLS = s.SYMBOLS + [s._sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05), s._sym("BADTOKEN", "BADTOKEN-EQ", "NSE", "ab12", "BAD"), s._sym("DECIMALTOKEN", "DECIMALTOKEN-EQ", "NSE", "12.5", "DEC")]


def ok(total=100000.5, span=60000.25, exposure=25000.25, extra=None):
    breakup = [{"name": "SPANMARGIN", "amount": span}, {"name": "EXPOMARGIN", "amount": exposure}] + (extra or [])
    return {"body": {"status": True, "data": {"summary": {"total_charges": total, "breakup": breakup}}}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_buy_leg", [pos(action="BUY")], {EP: ok()}),
    margin("margin_a_limit_leg_with_a_price", [pos("NIFTY25NOV2624000CE", action="BUY", pricetype="LIMIT", price="100.5")], {EP: ok()}),
    margin("margin_a_stop_leg_with_a_trigger", [pos(pricetype="SL", price="24000", trigger_price="23990.5")], {EP: ok()}),
    margin("margin_an_equity_leg_in_each_product", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos("INFY", "NSE", "BUY", "5", "CNC")], {EP: ok()}),
    margin("margin_several_legs_in_one_call", [pos(), pos("NIFTY25NOV2624000CE", action="BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: ok(5.5, 3.25, 2.25)}),
    margin("margin_totals_as_text", [pos()], {EP: {"body": {"status": "true", "data": {"summary": {"total_charges": "12345.5", "breakup": [{"name": "SPANMARGIN", "amount": "100"}, {"name": "EXPOMARGIN", "amount": "50"}]}}}}}),
    margin("margin_without_a_span_part", [pos()], {EP: {"body": {"status": True, "data": {"summary": {"total_charges": 500, "breakup": [{"name": "EXPOMARGIN", "amount": 50}]}}}}}),
    margin("margin_with_other_parts_in_the_breakup", [pos()], {EP: ok(extra=[{"name": "BROKERAGE", "amount": 9}])}),
    margin("margin_the_last_part_of_a_name_wins", [pos()], {EP: {"body": {"status": True, "data": {"summary": {"total_charges": 1, "breakup": [{"name": "SPANMARGIN", "amount": 10}, {"name": "SPANMARGIN", "amount": 20}]}}}}}),
    margin("margin_without_a_summary", [pos()], {EP: {"body": {"status": True, "data": {}}}}),
    margin("margin_without_data", [pos()], {EP: {"body": {"status": True}}}),
    margin("margin_a_token_with_a_decimal_point_is_priced", [pos("DECIMALTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_a_token_that_is_not_digits_is_left_out", [pos(), pos("BADTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_every_leg_left_out", [pos("BADTOKEN", "NSE", "BUY", "1", "MIS")]),
    margin("margin_refused", [pos()], {EP: {"body": {"status": False, "message": "Insufficient data"}}}),
    margin("margin_refused_without_a_message", [pos()], {EP: {"body": {"status": False}}}),
    margin("margin_status_as_text_false", [pos()], {EP: {"body": {"status": "false", "message": "No"}}}),
    margin("margin_http_error", [pos()], {EP: {"status": 500, "body": {"status": False, "message": "down"}}}),
    margin("margin_not_json", [pos()], {EP: {"status": 200, "body": "<html>"}}),
    margin("margin_answer_that_is_a_list", [pos()], {EP: {"body": [1]}}),
    margin("margin_unknown_symbol", [pos("NOPE")]),
]
