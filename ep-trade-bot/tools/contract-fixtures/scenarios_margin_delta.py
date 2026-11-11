"""/margin on Delta Exchange: each position asks margin_required and the initial margins are added."""

import scenarios_deltaexchange as d

BROKER = "deltaexchange"
AUTH_TOKEN, ENV, SYMBOLS = d.AUTH_TOKEN, d.ENV, d.SYMBOLS
BTC, ETH = "GET /v2/products/27/margin_required", "GET /v2/products/3136/margin_required"
OK_BTC = {"body": {"success": True, "result": {"initial_margin": "500.25", "maintenance_margin": "250.0", "order_size": 10}}}
OK_ETH = {"body": {"success": True, "result": {"initial_margin": "120.5", "maintenance_margin": "60.0", "order_size": 5}}}
REFUSED = {"body": {"success": False, "error": {"code": "invalid_product", "message": "Product not found"}}}
SERVER = {"status": 500, "body": {"success": False, "error": {"code": "internal_error"}}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_position_market", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML")], {BTC: OK_BTC}),
    margin("margin_limit_order_with_price", [pos("BTCUSD", "CRYPTO", "SELL", "10", "NRML", pricetype="LIMIT", price="67000.5")], {BTC: OK_BTC}),
    margin("margin_limit_order_with_a_whole_price", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML", pricetype="LIMIT", price="67000")], {BTC: OK_BTC}),
    margin("margin_limit_order_without_a_price_is_asked_as_market", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML", pricetype="LIMIT")], {BTC: OK_BTC}),
    margin("margin_stop_loss_is_asked_as_a_limit_order", [pos("BTCUSD", "CRYPTO", "SELL", "10", "NRML", pricetype="SL", price="66000", trigger_price="66100")], {BTC: OK_BTC}),
    margin("margin_two_positions_added", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML"), pos("ETHUSD", "CRYPTO", "SELL", "5", "NRML")], {BTC: OK_BTC, ETH: OK_ETH}),
    margin("margin_one_position_refused", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML")], {BTC: REFUSED}),
    margin("margin_one_of_two_refused", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML"), pos("ETHUSD", "CRYPTO", "SELL", "5", "NRML")], {BTC: OK_BTC, ETH: REFUSED}),
    margin("margin_server_error", [pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML")], {BTC: SERVER}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH", "CRYPTO")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH", "CRYPTO"), pos("BTCUSD", "CRYPTO", "BUY", "10", "NRML")], {BTC: OK_BTC}),
    margin("margin_quantity_not_a_number", [pos("BTCUSD", "CRYPTO", quantity="ten")], {}),
    margin("margin_no_positions", [], {}),
    {"name": "margin_invalid_apikey", "path": "/api/v1/margin", "auth": "none", "request": {"apikey": "not-a-key", "positions": [pos("BTCUSD", "CRYPTO")]}, "broker": {}},
]
