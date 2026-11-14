"""/margin on Samco: one POST /spanMargin for every leg. Only derivatives are accepted (NFO, BFO, MCX, CDS); each leg goes as a limit order with its price, or zero."""

import scenarios_samco as s
from scenarios_samco import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "samco"
READ_POSTS = ["/spanMargin"]
EP = "POST /spanMargin"


def ok(total=1500.75, span=1000.5, exposure=500.25, spread=0.0, **extra):
    d = {"totalMargin": total, "marginRequired": span, "exposureMargin": exposure, "spreadBenefit": spread}
    d.update(extra)
    return {"body": {"status": "Success", "statusMessage": "ok", "spanDetails": d}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_priced_leg_sends_its_price", [pos(price="24005.5", pricetype="LIMIT")], {EP: ok()}),
    margin("margin_a_zero_price_is_zero", [pos(price="0")], {EP: ok()}),
    margin("margin_several_legs_go_in_one_call", [pos(), pos("NIFTY25NOV2624000CE", "NFO", "BUY"), pos("GOLDM25NOV26FUT", "MCX", "BUY", "10")], {EP: ok(55000.5, 50000, 10000, 5000)}),
    margin("margin_every_derivative_exchange", [pos(), pos("SENSEX26NOVFUT", "BFO", "BUY", "20"), pos("GOLDM25NOV26FUT", "MCX", "BUY", "10"), pos("USDINR25NOV26FUT", "CDS", "BUY", "1000")], {EP: ok()}),
    margin("margin_products_are_mapped", [pos(product="MIS"), pos(product="CNC")], {EP: ok()}),
    margin("margin_the_action_is_upper_cased", [pos(action="buy")], {EP: ok()}),
    margin("margin_total_requirement_names_are_read", [pos()], {EP: {"body": {"status": "Success", "spanDetails": {"totalRequirement": "2,000.5", "spanRequirement": "1,500.25", "exposureMargin": "500", "spreadBenefit": "100"}}}}),
    margin("margin_the_first_name_wins_when_it_is_nonzero", [pos()], {EP: ok(1, 2, 3, 4, totalRequirement=9, spanRequirement=9)}),
    margin("margin_a_zero_first_name_falls_to_the_second", [pos()], {EP: ok(0, 0, 3, 4, totalRequirement=9, spanRequirement=8)}),
    margin("margin_text_figures", [pos()], {EP: ok("1,500.75", "x", None, "")}),
    margin("margin_single_scrip_answer_without_span_details", [pos()], {EP: {"body": {"status": "Success", "totalMargin": 777.5, "marginRequired": 600.25, "exposureMargin": 177.25}}}),
    margin("margin_single_scrip_answer_with_only_the_required_margin", [pos()], {EP: {"body": {"status": "Success", "marginRequired": 600.25}}}),
    margin("margin_nothing_in_the_answer", [pos()], {EP: {"body": {"status": "Success"}}}),
    margin("margin_refused_with_a_message", [pos()], {EP: {"body": {"status": "Failure", "statusMessage": "Invalid scrip"}}}),
    margin("margin_refused_without_a_message", [pos()], {EP: {"body": {"status": "Failure"}}}),
    margin("margin_an_answer_that_is_not_json", [pos()], {EP: {"status": 500, "body": "not json"}}),
    margin("margin_cash_equity_only", [pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: ok()}),
    margin("margin_cash_equity_among_derivatives", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos()], {EP: ok()}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {EP: ok()}),
    margin("margin_unknown_symbol_among_others", [pos(), pos("NOSUCH")], {EP: ok()}),
    margin("margin_a_zero_quantity_leg", [pos(quantity="0")], {EP: ok()}),
]


def patch():
    pass
