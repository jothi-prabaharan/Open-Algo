"""/margin on Sahi: one position to the single-order calculator, two or more to the basket calculator (ten legs at a time); prices in paise."""

import scenarios_sahi as s
from scenarios_sahi import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, SYMBOLS, failure  # noqa: F401

BROKER = "sahi"
ONE_EP, BASKET_EP = "POST /v1/margins/order", "POST /v1/margins/basket"
READ_POSTS = ["/v1/margins/order", "/v1/margins/basket"]
ONE = {"body": {"required_margin": 160805, "available_margin": 9999900}}
BASKET = {"body": {"required_margin": 19111900, "available_margin": 5000000}}
BASKET_2 = {"body": {"required_margin": 2500000, "available_margin": 4000000}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_uses_the_single_calculator", [pos()], {ONE_EP: ONE}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {ONE_EP: ONE}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {ONE_EP: ONE}),
    margin("margin_stop_market_with_trigger", [pos(pricetype="SL-M", trigger_price="801.5")], {ONE_EP: ONE}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {ONE_EP: ONE}),
    margin("margin_cnc_equity", [pos(product="CNC")], {ONE_EP: ONE}),
    margin("margin_two_positions_use_the_basket_calculator", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {BASKET_EP: BASKET}),
    margin("margin_eleven_positions_go_in_two_baskets", [pos(f"BULK{i}") for i in range(11)], {BASKET_EP: [BASKET, BASKET_2]}),
    margin("margin_twenty_positions_go_in_two_full_baskets", [pos(f"BULK{i}") for i in range(20)], {BASKET_EP: [BASKET, BASKET_2]}),
    margin("margin_rupee_figures_are_rounded_to_paise", [pos(), pos("INFY")], {BASKET_EP: {"body": {"required_margin": 12345, "available_margin": 99}}}),
    margin("margin_answer_without_an_available_figure", [pos()], {ONE_EP: {"body": {"required_margin": 160805}}}),
    margin("margin_broker_refuses_with_a_message", [pos()], {ONE_EP: failure(400, "INVALID", "Quantity is not a multiple of the lot size")}),
    margin("margin_broker_refuses_without_a_message", [pos()], {ONE_EP: {"status": 400, "body": {"x": 1}}}),
    margin("margin_calculator_under_maintenance", [pos()], {ONE_EP: failure(503, "UNDER_MAINTENANCE", "closed")}),
    margin("margin_session_expired", [pos()], {ONE_EP: failure(401, "UNAUTHORIZED", "x")}),
    margin("margin_key_without_permission", [pos()], {ONE_EP: failure(403, "FORBIDDEN", "x")}),
    margin("margin_rate_limited", [pos()], {ONE_EP: failure(429, "RATE_LIMITED", "x")}),
    margin("margin_ok_status_without_a_figure", [pos()], {ONE_EP: {"body": {"x": 1}}}),
    margin("margin_second_basket_refused", [pos(f"BULK{i}") for i in range(11)], {BASKET_EP: [BASKET, failure(400, "INVALID", "bad leg")]}),
    margin("margin_index_exchange_refused", [pos("NIFTY", "NSE_INDEX")], {}),
    margin("margin_product_not_offered", [pos(product="BO")], {}),
    margin("margin_price_type_not_offered", [pos(pricetype="LIMITX")], {}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {BASKET_EP: BASKET}),
    margin("margin_quantity_not_a_number", [pos(quantity="ten")], {}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos()]}, "broker": {}},
]
