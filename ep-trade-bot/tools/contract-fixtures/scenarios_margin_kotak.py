"""/margin on Kotak Neo: one POST /quick/user/check-margin a leg (Kotak takes one order at a time), the answers added together. A leg whose symbol is not in the master contract is left out."""

import scenarios_kotak as s
from scenarios_kotak import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "kotak"
READ_POSTS = ["/quick/user/check-margin"]
EP = "POST /quick/user/check-margin"


def ok(required="126783.5", **extra):
    d = {"stat": "Ok", "stCode": 200, "reqdMrgn": required, "avlMrgn": "500000.00"}
    d.update(extra)
    return {"body": d}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_limit_leg_carries_its_price", [pos(pricetype="LIMIT", price="24005.5")], {EP: ok()}),
    margin("margin_a_stop_limit_leg", [pos(pricetype="SL", price="24000", trigger_price="24010")], {EP: ok()}),
    margin("margin_a_stop_market_leg", [pos(pricetype="SL-M", trigger_price="24010")], {EP: ok()}),
    margin("margin_buy", [pos(action="BUY", pricetype="LIMIT", price="100")], {EP: ok()}),
    margin("margin_products_are_mapped", [pos(product="MIS", symbol="SBIN", exchange="NSE", quantity="10")], {EP: ok("1000")}),
    margin("margin_cash_delivery", [pos(product="CNC", symbol="SBIN", exchange="NSE", quantity="10", action="BUY", pricetype="LIMIT", price="805")], {EP: ok("8050")}),
    margin("margin_every_exchange", [pos("SBIN", "NSE", "BUY", "1", "MIS"), pos("TATAMOTORS", "BSE", "BUY", "1", "CNC"), pos("SENSEX26NOVFUT", "BFO", "SELL", "20", "NRML"), pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML"), pos("USDINR25NOV26FUT", "CDS", "SELL", "1000", "NRML")], {EP: ok("100")}),
    margin("margin_several_legs_are_added", [pos(), pos("NIFTY25NOV2624000CE", "NFO", "BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [ok("100.5"), ok("200.25"), ok("300")]}),
    margin("margin_text_amount_with_a_comma", [pos(pricetype="LIMIT", price="1")], {EP: ok("1,500")}),
    margin("margin_no_amount_in_the_answer", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"stat": "Ok"}}}),
    margin("margin_refused_with_a_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"stat": "Not_Ok", "errMsg": "Invalid order"}}}),
    margin("margin_refused_without_a_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"stat": "Not_Ok"}}}),
    margin("margin_an_answer_that_is_not_json", [pos(pricetype="LIMIT", price="1")], {EP: {"status": 500, "body": "not json"}}),
    margin("margin_a_failed_leg_is_left_out_of_the_total", [pos(pricetype="LIMIT", price="1"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [{"body": {"stat": "Not_Ok", "errMsg": "Invalid order"}}, ok("300")]}),
    margin("margin_every_leg_failed", [pos(pricetype="LIMIT", price="1"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: {"body": {"stat": "Not_Ok", "errMsg": "Invalid order"}}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH", pricetype="LIMIT", price="1")], {EP: ok()}),
    margin("margin_unknown_symbol_among_others", [pos(pricetype="LIMIT", price="24000"), pos("NOSUCH", pricetype="LIMIT", price="1")], {EP: ok()}),
]


def patch():
    pass
