"""Pocketful has no margin calculator: OpenAlgo's call raises NotImplementedError and the platform answers with its own error."""

import scenarios_pocketful as s
from scenarios_pocketful import AUTH_TOKEN, CLIENT_ID, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "pocketful"


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {}}


SCENARIOS = [
    margin("margin_one_future", [pos()]),
    margin("margin_several_legs", [pos(), pos("SBIN", "NSE", "BUY", "10", "MIS")]),
    margin("margin_unknown_symbol", [pos("NOPE")]),
]
