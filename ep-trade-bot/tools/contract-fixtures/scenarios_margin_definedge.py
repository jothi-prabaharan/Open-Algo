"""/margin on Definedge: one POST /spancalculator with every leg (symbol name, expiry as DD-MMM-YYYY, strike, option type, open buy and sell quantity). The margin is the span plus the exposure."""

import scenarios_definedge as s
from scenarios_definedge import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, V  # noqa: F401

BROKER = "definedge"
READ_POSTS = [f"{V}/spancalculator"]
EP = f"POST {V}/spancalculator"
SYMBOLS = s.SYMBOLS + [s._sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05), s._sym("NIFTY25NOV2623500PE", "NIFTY25NOV2623500PE", "NFO", 49600, "NIFTY", 75, "PE", "25-NOV-26", 23500.5, 0.05),
                       s._sym("OLDFUT", "OLDFUT", "NFO", 49601, "NIFTY", 75, "FUT", "30-DEC-79", -1.0, 0.1), s._sym("ODDFUT", "ODDFUT", "NFO", 49602, "NIFTY", 75, "FUT", "30DEC2026", 0.0, 0.1)]


def ok(span="100000.5", exposure="25000.25"):
    return {"body": {"status": "SUCCESS", "span": span, "exposure": exposure}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_buy_leg", [pos(action="BUY")], {EP: ok()}),
    margin("margin_an_option_leg", [pos("NIFTY25NOV2624000CE", action="BUY", pricetype="LIMIT", price="100")], {EP: ok()}),
    margin("margin_a_put_with_a_fractional_strike", [pos("NIFTY25NOV2623500PE", action="SELL", pricetype="LIMIT", price="100")], {EP: ok()}),
    margin("margin_several_legs_in_one_call", [pos(), pos("NIFTY25NOV2624000CE", action="BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: ok("5.5", "6.25")}),
    margin("margin_every_product", [pos(product="MIS", symbol="SBIN", exchange="NSE", quantity="10"), pos(product="CNC", symbol="SBIN", exchange="NSE", quantity="10")], {EP: ok()}),
    margin("margin_an_old_expiry_year", [pos("OLDFUT")], {EP: ok()}),
    margin("margin_an_expiry_in_another_shape", [pos("ODDFUT")], {EP: ok()}),
    margin("margin_nothing_in_the_answer", [pos()], {EP: {"body": {"status": "SUCCESS"}}}),
    margin("margin_text_that_is_not_a_number", [pos()], {EP: ok("n/a", "1")}),
    margin("margin_refused_with_a_message", [pos()], {EP: {"body": {"status": "FAILURE", "message": "Invalid basket"}}}),
    margin("margin_refused_without_a_message", [pos()], {EP: {"body": {"status": "ERROR"}}}),
    margin("margin_another_status", [pos()], {EP: {"body": {"status": "PENDING", "message": "Wait"}}}),
    margin("margin_another_status_without_a_message", [pos()], {EP: {"body": {"status": "PENDING"}}}),
    margin("margin_an_answer_that_is_not_json", [pos()], {EP: {"status": 500, "body": "not json"}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {EP: ok()}),
    margin("margin_unknown_symbol_among_others", [pos(), pos("NOSUCH")], {EP: ok()}),
]


def patch():
    import importlib

    limiter = importlib.import_module("broker.definedge.api.rate_limiter")
    limiter.time.sleep = lambda *a, **k: None
