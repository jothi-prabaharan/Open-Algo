"""Delta Exchange leverage: a separate call sets it for the product before an order is placed. The order's own value, the saved common value, then the
environment's default decide it."""

import scenarios_deltaexchange as d

BROKER = "deltaexchange"
AUTH_TOKEN, ENV, SYMBOLS, RECORDED_AT = d.AUTH_TOKEN, d.ENV, d.SYMBOLS, d.RECORDED_AT
INIT = ["database.leverage_db"]
PLACE, ORDER_OK = d.PLACE, d.ORDER_OK
LEVERAGE_URL, ORDER_URL = "POST /v2/products/27/orders/leverage", "POST /v2/orders"
LEVERAGE_OK = {"body": {"success": True, "result": {"leverage": "10", "product_id": 27}}}
LEVERAGE_REFUSED = {"status": 400, "body": {"success": False, "error": {"code": "invalid_leverage"}}}


def place(name, leverage=None, env=None, leverage_reply=LEVERAGE_OK, request=None):
    sc = {"name": name, "path": "/api/v1/placeorder", "request": request or PLACE, "broker": {LEVERAGE_URL: leverage_reply, ORDER_URL: ORDER_OK}}
    sc["leverage"] = 0 if leverage is None else leverage
    if env:
        sc["env"] = env
    return sc


SCENARIOS = [
    place("no_leverage_anywhere_sends_no_leverage_call"),
    place("saved_leverage_is_set_before_the_order", leverage=10),
    place("saved_leverage_applies_to_a_limit_order", leverage=5, request={**PLACE, "pricetype": "LIMIT", "price": "67000"}),
    place("environment_default_is_used_when_nothing_is_saved", env={"DELTA_DEFAULT_LEVERAGE": "7"}),
    place("saved_leverage_wins_over_the_environment", leverage=10, env={"DELTA_DEFAULT_LEVERAGE": "7"}),
    place("environment_zero_means_none", env={"DELTA_DEFAULT_LEVERAGE": "0"}),
    place("a_refused_leverage_call_does_not_stop_the_order", leverage=10, leverage_reply=LEVERAGE_REFUSED),
    place("a_refused_leverage_call_stops_the_order_when_asked", leverage=10, leverage_reply=LEVERAGE_REFUSED, env={"DELTA_ABORT_ON_LEVERAGE_FAILURE": "true"}),
    place("abort_flag_is_read_ignoring_case_and_spaces", leverage=10, leverage_reply=LEVERAGE_REFUSED, env={"DELTA_ABORT_ON_LEVERAGE_FAILURE": " TRUE "}),
    place("abort_flag_off_lets_the_order_through", leverage=10, leverage_reply=LEVERAGE_REFUSED, env={"DELTA_ABORT_ON_LEVERAGE_FAILURE": "false"}),
    place("leverage_for_another_product_uses_its_own_id", leverage=3, request={**PLACE, "symbol": "ETHUSD"}),
]
SCENARIOS[-1]["broker"] = {"POST /v2/products/3136/orders/leverage": LEVERAGE_OK, ORDER_URL: {"body": {"success": True, "result": {"id": 9011, "product_id": 3136}}}}
