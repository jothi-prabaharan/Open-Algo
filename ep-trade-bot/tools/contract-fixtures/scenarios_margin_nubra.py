"""/margin on Nubra: one POST /sentinel/orders/funds_required with every leg as an order item (requestType NEW). The margin is the funds required, which include charges, else the margin blocked; there is no split into span and exposure."""

import scenarios_nubra as s
from scenarios_nubra import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "nubra"
EP = "POST /sentinel/orders/funds_required"
READ_POSTS = ["/sentinel/orders/funds_required"]
SYMBOLS = s.SYMBOLS + [s._sym("BADTOKEN", "BADTOKEN", "NSE", "NSE", "ab12", "BAD"), s._sym("DECIMALTOKEN", "DECIMALTOKEN", "NSE", "NSE", "12.5", "DEC")]


def ok(total_margin=100000.5, required=100500.75, charges=13.5):
    return {"body": {"code": 1, "marginInfo": {"totalMargin": total_margin, "message": None}, "brokerageInfo": {"totalChargesFloat": charges}, "totalFundsRequired": required, "willBeAutoSliced": False}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_buy_leg", [pos(action="BUY")], {EP: ok()}),
    margin("margin_a_limit_leg_with_a_price", [pos("NIFTY25NOV2624000CE", action="BUY", pricetype="LIMIT", price="100.55")], {EP: ok()}),
    margin("margin_a_stop_limit_leg", [pos(pricetype="SL", price="24000", trigger_price="23990.5")], {EP: ok()}),
    margin("margin_a_stop_market_sell_leg", [pos(pricetype="SL-M", trigger_price="23990.5")], {EP: ok()}),
    margin("margin_a_stop_leg_without_a_trigger", [pos(pricetype="SL", price="24000")], {EP: ok()}),
    margin("margin_an_equity_leg_in_each_product", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos("INFY", "NSE", "BUY", "5", "CNC")], {EP: ok()}),
    margin("margin_several_legs_are_one_request", [pos(), pos("NIFTY25NOV2624000CE", action="BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: ok()}),
    margin("margin_without_funds_required_uses_the_margin", [pos()], {EP: ok(required=0)}),
    margin("margin_with_text_numbers", [pos()], {EP: {"body": {"marginInfo": {"totalMargin": "1200.5"}, "totalFundsRequired": "1300"}}}),
    margin("margin_with_no_numbers", [pos()], {EP: {"body": {"marginInfo": {}}}}),
    margin("margin_with_numbers_that_are_not_numbers", [pos()], {EP: {"body": {"marginInfo": {"totalMargin": "x"}}}}),
    margin("margin_refused_with_an_error", [pos()], {EP: {"status": 400, "body": {"error": "Invalid order"}}}),
    margin("margin_refused_with_a_message", [pos()], {EP: {"status": 400, "body": {"message": "Bad leg"}}}),
    margin("margin_refused_with_no_reason", [pos()], {EP: {"status": 400, "body": {}}}),
    margin("margin_session_expired", [pos()], {EP: {"status": 440, "body": {}}}),
    margin("margin_answer_is_not_json", [pos()], {EP: {"status": 200, "body": "<html>"}}),
    margin("margin_a_token_with_a_decimal_point_is_priced", [pos("DECIMALTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_a_token_that_is_not_digits_is_left_out", [pos(), pos("BADTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_every_leg_left_out", [pos("BADTOKEN", "NSE", "BUY", "1", "MIS")]),
    margin("margin_unknown_symbol", [pos("NOPE")]),
    margin("margin_mcx_leg", [pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML")], {EP: ok()}),
]
