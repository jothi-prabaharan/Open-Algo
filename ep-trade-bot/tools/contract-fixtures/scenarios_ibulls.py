"""iBulls (XTS) scenarios: the Wisdom Capital order, book and fund cases, plus the market-to-limit pricing iBulls adds."""

from scenarios_wisdom import *  # noqa: F403
from scenarios_wisdom import ENV as _ENV, PLACE, ORDER_OK, SCENARIOS as _WISDOM
import scenarios_wisdom_data as _data

BROKER = "ibulls"
ENV = dict(_ENV)
QUOTES = "POST /apibinarymarketdata/instruments/quotes"
READ_POSTS = ["/apibinarymarketdata/instruments/quotes"]


def patch():
    # OpenAlgo looks the market-data token up by the logged-in user's name; the recording gives it the token it would find.
    import importlib

    importlib.import_module("broker.ibulls.mapping.transform_data").get_feed_token = lambda *a, **k: "feed-token"


def _q(bid, ask):
    return _data._reply(_data._quote(1, 22, 805.45, bid=bid, ask=ask))


SCENARIOS = [s for s in _WISDOM if s["name"] not in ("placeorder_market", "placesmartorder_reduce", "closeposition")] + [
    {"name": "placeorder_market_buy_priced_from_the_bid", "path": "/api/v1/placeorder", "request": PLACE, "broker": {QUOTES: [_q(805.4, 805.5), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_market_sell_priced_from_the_ask", "path": "/api/v1/placeorder", "request": {**PLACE, "action": "SELL"}, "broker": {QUOTES: [_q(805.4, 805.5), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_market_bid_is_zero", "path": "/api/v1/placeorder", "request": PLACE, "broker": {QUOTES: [_q(0, 805.5), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_market_ask_is_zero", "path": "/api/v1/placeorder", "request": {**PLACE, "action": "SELL"}, "broker": {QUOTES: [_q(805.4, 0), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_market_quote_refused", "path": "/api/v1/placeorder", "request": PLACE, "broker": {QUOTES: {"status": 400, "body": {"type": "error", "description": "Market data not enabled", "result": {}}}, "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_limit_is_not_priced_again", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "LIMIT", "price": "801.5"}, "broker": {QUOTES: [_q(805.4, 805.5), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
    {"name": "placeorder_market_priced_cheap_share", "path": "/api/v1/placeorder", "request": PLACE, "broker": {QUOTES: [_q(12.35, 12.4), _data.NO_OI], "POST /interactive/orders": ORDER_OK}},
]
SYMBOLS = _data.SYMBOLS


# A market order is priced from a quote first; the cases that are not about pricing let that quote be refused, as it is when the market-data service is down.
for _s in SCENARIOS:
    if _s["path"] == "/api/v1/placeorder" and _s["request"].get("pricetype") == "MARKET" and "broker" in _s and QUOTES not in _s["broker"]:
        _s["broker"] = {**_s["broker"], QUOTES: {"status": 400, "body": {"type": "error", "description": "Market data not enabled", "result": {}}}}
