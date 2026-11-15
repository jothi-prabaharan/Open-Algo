"""/margin on IndMoney: one GET /margin for each leg (the call takes a single order, with a JSON body of segment, side, quantity and price as text, product, security id and exchange). The margins of the legs that worked are added up; a leg that failed is left out of the sum and named when every leg failed."""

import scenarios_indmoney as s
from scenarios_indmoney import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "indmoney"
EP = "GET /margin"
SYMBOLS = s.SYMBOLS + [s._sym("NIFTY25NOV2624000CE", "NIFTY-25NOV2026-24000-CE", "NFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05), s._sym("BADTOKEN", "BADTOKEN", "NSE", "ab12", "BAD"), s._sym("DECIMALTOKEN", "DECIMALTOKEN", "NSE", "12.5", "DEC")]


def ok(total=100000.5, span=60000.25, exposure=25000.25):
    return {"body": {"status": "success", "data": {"total_margin": total, "span_margin": span, "exposure_margin": exposure}}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future", [pos()], {EP: ok()}),
    margin("margin_a_buy_leg", [pos(action="BUY")], {EP: ok()}),
    margin("margin_a_limit_leg_with_a_price", [pos("NIFTY25NOV2624000CE", action="BUY", pricetype="LIMIT", price="100.5")], {EP: ok()}),
    margin("margin_a_leg_with_a_whole_price", [pos("SBIN", "NSE", "BUY", "10", "MIS", "LIMIT", price="800")], {EP: ok()}),
    margin("margin_an_equity_leg_in_each_product", [pos("SBIN", "NSE", "BUY", "10", "MIS"), pos("INFY", "NSE", "BUY", "5", "CNC")], {EP: [ok(100, 60, 40), ok(50, 30, 20)]}),
    margin("margin_bse_legs", [pos("TATAMOTORS", "BSE", "BUY", "5", "CNC"), pos("SENSEX26NOVFUT", "BFO", "SELL", "20", "NRML")], {EP: [ok(100, 60, 40), ok(50, 30, 20)]}),
    margin("margin_several_legs_are_added_up", [pos(), pos("NIFTY25NOV2624000CE", action="BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [ok(5.5, 3.25, 2.25), ok(10, 6, 4), ok(1.25, 1, 0.25)]}),
    margin("margin_a_leg_that_fails_is_left_out_of_the_sum", [pos(), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [ok(100, 60, 40), {"body": {"status": "error", "message": "Invalid"}}]}),
    margin("margin_a_leg_that_is_not_json_is_left_out", [pos(), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [ok(100, 60, 40), {"status": 200, "body": "<html>"}]}),
    margin("margin_every_leg_failed", [pos(), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: {"body": {"status": "error", "message": "Invalid"}}}),
    margin("margin_http_error_is_read_from_its_body", [pos()], {EP: {"status": 500, "body": {"status": "error", "message": "down"}}}),
    margin("margin_http_error_with_a_body_that_says_success", [pos()], {EP: {"status": 500, "body": {"status": "success", "data": {"total_margin": 5}}}}),
    margin("margin_without_data", [pos()], {EP: {"body": {"status": "success"}}}),
    margin("margin_with_data_that_is_empty", [pos()], {EP: {"body": {"status": "success", "data": {}}}}),
    margin("margin_numbers_as_text", [pos()], {EP: {"body": {"status": "success", "data": {"total_margin": "12345.5", "span_margin": "100", "exposure_margin": "50"}}}}),
    margin("margin_numbers_as_text_in_a_sum", [pos(), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: [ok(100, 60, 40), {"body": {"status": "success", "data": {"total_margin": "12345.5", "span_margin": "100", "exposure_margin": "50"}}}]}),
    margin("margin_a_token_with_a_decimal_point_is_priced", [pos("DECIMALTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_a_token_that_is_not_digits_is_left_out", [pos(), pos("BADTOKEN", "NSE", "BUY", "1", "MIS")], {EP: ok()}),
    margin("margin_every_leg_left_out", [pos("BADTOKEN", "NSE", "BUY", "1", "MIS")]),
    margin("margin_unknown_symbol", [pos("NOPE")]),
    margin("margin_mcx_leg", [pos("GOLDM25NOV26FUT", "MCX", "BUY", "10", "NRML")], {EP: ok()}),
]


def before_each(sc):
    import importlib

    importlib.import_module("broker.indmoney.api.rate_limiter").time.sleep = lambda *a, **k: None
