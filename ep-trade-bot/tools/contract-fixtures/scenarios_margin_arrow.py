"""/margin on Arrow: one position to the single-order calculator, two or more to the basket calculator (which, if refused, is replaced by the sum of the single-order answers)."""

import scenarios_arrow as s
from scenarios_arrow import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "arrow"
READ_POSTS = ["/margin/order", "/margin/basket"]
ONE_EP, BASKET_EP = "POST /margin/order", "POST /margin/basket"


def one(required=1608.05, charge=12.345):
    return {"body": {"status": "success", "data": {"requiredMargin": required, "charge": {"total": charge}}}}


BASKET = {"body": {"status": "success", "data": {"final_margin": "206000.456", "initial_margin": "336000", "orders": [{"charge": {"total": "10.1"}}, {"charge": {"total": 20.25}}, {}]}}}
REFUSED = {"status": 400, "body": {"status": "error", "message": "Invalid trading symbol"}}
REFUSED_OK = {"body": {"status": "error", "message": "Rejected"}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_uses_the_single_calculator", [pos()], {ONE_EP: one()}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {ONE_EP: one()}),
    margin("margin_stop_orders_are_priced_as_limit", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {ONE_EP: one()}),
    margin("margin_stop_market_is_priced_as_limit", [pos(pricetype="SL-M", trigger_price="801.5")], {ONE_EP: one()}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {ONE_EP: one()}),
    margin("margin_cnc_equity", [pos(product="CNC")], {ONE_EP: one()}),
    margin("margin_single_answer_without_data", [pos()], {ONE_EP: {"body": {"status": "success"}}}),
    margin("margin_single_answer_with_text_figures", [pos()], {ONE_EP: {"body": {"status": "success", "data": {"requiredMargin": "1608.055", "charge": {"total": "x"}}}}}),
    margin("margin_single_refused_with_an_error_status", [pos()], {ONE_EP: REFUSED}),
    margin("margin_single_refused_with_an_ok_status", [pos()], {ONE_EP: REFUSED_OK}),
    margin("margin_single_refused_without_a_message", [pos()], {ONE_EP: {"status": 400, "body": {"status": "error"}}}),
    margin("margin_two_positions_use_the_basket_calculator", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {BASKET_EP: BASKET}),
    margin("margin_basket_answer_without_data", [pos(), pos("INFY")], {BASKET_EP: {"body": {"status": "success"}}}),
    margin("margin_a_refused_basket_is_the_sum_of_single_orders", [pos(), pos("INFY", pricetype="LIMIT", price="1500")], {BASKET_EP: REFUSED, ONE_EP: [one(1000.5, 5.5), one(2000.25, 7.25)]}),
    margin("margin_a_refused_basket_with_an_ok_status_is_summed_too", [pos(), pos("INFY")], {BASKET_EP: REFUSED_OK, ONE_EP: [one(1000.5, 5.5), one(2000.25, 7.25)]}),
    margin("margin_the_sum_stops_at_a_refused_order", [pos(), pos("INFY")], {BASKET_EP: REFUSED, ONE_EP: [one(1000.5), REFUSED]}),
    margin("margin_the_sum_stops_at_an_order_refused_with_an_ok_status", [pos(), pos("INFY")], {BASKET_EP: REFUSED, ONE_EP: [REFUSED_OK]}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {ONE_EP: one()}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
