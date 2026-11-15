"""/margin on Groww: POST /v1/margins/detail/orders?segment=CASH|FNO with a list of order items. Groww works out a basket only for F&O, so the F&O legs go as one basket and each cash leg as a request of its own; the answers are added. Every item carries
trading_symbol, quantity, exchange (NSE or BSE), segment, product, order_type and transaction_type, and a price in rupees when one is given."""

import scenarios_groww as s
from scenarios_groww import AUTH_TOKEN, ENV, SEED_API_KEY_ROW  # noqa: F401

BROKER = "groww"
EP = "POST /v1/margins/detail/orders"
READ_POSTS = ["/v1/margins/detail/orders"]
SYMBOLS = s.SYMBOLS


def ok(total=100500.75, span=80000.5, exposure=20500.25):
    return s.ok({"exposure_required": exposure, "span_required": span, "option_buy_premium": 0, "brokerage_and_charges": 10.5, "total_requirement": total})


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_buy_leg", [pos(action="BUY")], {EP: ok()}),
    margin("margin_a_limit_option_leg_with_a_price", [pos("NIFTY25NOV2624000CE", action="BUY", pricetype="LIMIT", price="100.55")], {EP: ok()}),
    margin("margin_a_stop_limit_leg", [pos(pricetype="SL", price="24000", trigger_price="23990.5")], {EP: ok()}),
    margin("margin_a_stop_market_leg", [pos(pricetype="SL-M", trigger_price="23990.5")], {EP: ok()}),
    margin("margin_a_bse_future", [pos("SENSEX26NOVFUT", "BFO", "BUY", "20")], {EP: ok()}),
    margin("margin_f_and_o_legs_are_one_basket", [pos(), pos("NIFTY25NOV2624000CE", action="BUY"), pos("SENSEX26NOVFUT", "BFO", "BUY", "20")], {EP: ok()}),
    margin("margin_each_cash_leg_is_a_request", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos("INFY", "NSE", "BUY", "5", "CNC"), pos("TATAMOTORS", "BSE", "BUY", "2", "CNC")], {EP: [ok(100, 60, 40), ok(200, 120, 80), ok(300, 180, 120)]}),
    margin("margin_cash_and_f_and_o_are_added", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos()], {EP: [ok(100, 60, 40), ok(250, 150, 100)]}),
    margin("margin_a_cash_leg_with_a_price", [pos("SBIN", "NSE", "BUY", "10", "MIS", pricetype="LIMIT", price="800.5")], {EP: ok()}),
    margin("margin_a_cash_leg_without_a_price_in_the_request", [pos("SBIN", "NSE", "BUY", "10", "MIS", price="0")], {EP: ok()}),
    margin("margin_numbers_that_are_missing_are_zero", [pos()], {EP: s.ok({"total_requirement": 500})}),
    margin("margin_numbers_as_text", [pos()], {EP: s.ok({"total_requirement": "1200.5", "span_required": "1000", "exposure_required": "200.5"})}),
    margin("margin_numbers_that_are_not_numbers", [pos()], {EP: s.ok({"total_requirement": "x"})}),
    margin("margin_with_an_empty_payload", [pos()], {EP: {"body": {"status": "SUCCESS"}}}),
    margin("margin_refused_with_an_error", [pos()], {EP: s.fail(400, "Invalid order", "GA040")}),
    margin("margin_refused_with_no_reason", [pos()], {EP: {"status": 400, "body": {}}}),
    margin("margin_a_failure_status_in_a_200", [pos()], {EP: {"body": {"status": "FAILURE", "error": {"message": "Not allowed"}}}}),
    margin("margin_http_error_with_a_reason", [pos()], {EP: s.fail(500, "Down", "GA500")}),
    margin("margin_answer_is_not_json", [pos()], {EP: s.NOT_JSON}),
    margin("margin_throttled_then_answered", [pos()], {EP: [s.THROTTLED, ok()]}),
    margin("margin_throttled_every_time", [pos()], {EP: s.THROTTLED}),
    margin("margin_the_second_request_is_refused", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos("INFY", "NSE", "BUY", "5", "CNC")], {EP: [ok(), s.fail(400, "Bad leg", "GA041")]}),
    margin("margin_unknown_symbol", [pos("NOPE")]),
    margin("margin_mcx_is_refused", [pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML")]),
    margin("margin_a_leg_that_is_refused_spoils_the_basket", [pos(), pos("NOPE")], {EP: ok()}),
]


def before_each(sc):
    s.before_each(sc)
