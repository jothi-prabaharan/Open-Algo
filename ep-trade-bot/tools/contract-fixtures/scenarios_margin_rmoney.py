"""/margin on RMoney: a basket to /interactive/orders/margindetails."""

import scenarios_wisdom as w

BROKER = "rmoney"
AUTH_TOKEN, ENV = w.AUTH_TOKEN, w.ENV
SYMBOLS = w.SYMBOLS + [
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]
SEED_API_KEY_ROW = True
EP = "POST /interactive/orders/margindetails"


def reply(required=150, available=9816624.5775, shortfall=0, valid=True, message=""):
    return {"body": {"type": "success", "code": "s-calculatemargin-0001", "description": "Request sent", "result": {"brokerageDeatils": {
        "IsValid": valid, "MarginRequired": required, "MarginAvailable": available, "MarginShortfall": shortfall, "ErrorMessage": message}}}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


READ_POSTS = ["/interactive/orders/margindetails"]
SCENARIOS = [
    margin("margin_one_position", [pos()], {EP: reply()}),
    margin("margin_limit_order_with_price", [pos(pricetype="LIMIT", price="805.5")], {EP: reply(2000.5)}),
    margin("margin_stop_loss_with_trigger", [pos(pricetype="SL", price="800", trigger_price="801.5", action="sell")], {EP: reply()}),
    margin("margin_stop_loss_market", [pos(pricetype="SL-M", trigger_price="801.5")], {EP: reply()}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML")], {EP: reply(120000)}),
    margin("margin_mcx_and_currency", [pos("CRUDEOIL", "MCX", "BUY", "1", "NRML"), pos("USDINR25NOV26FUT", "CDS", "BUY", "1", "NRML")], {EP: reply(5000)}),
    margin("margin_basket_of_two", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML"), pos()], {EP: reply(130000.25)}),
    margin("margin_shortfall_and_invalid", [pos()], {EP: reply(500, 100, 400, False, "Insufficient funds")}),
    margin("margin_figures_as_text", [pos()], {EP: reply("250.5", "100", "x", True)}),
    margin("margin_broker_refuses", [pos()], {EP: {"status": 400, "body": {"type": "error", "code": "e-margin-0001", "description": "Invalid instrument"}}}),
    margin("margin_error_type_with_ok_status", [pos()], {EP: {"body": {"type": "error", "description": "Not allowed"}}}),
    margin("margin_session_expired", [pos()], {EP: {"status": 401, "body": {"type": "error", "description": "Invalid Token"}}}),
    margin("margin_no_details", [pos()], {EP: {"body": {"type": "success", "result": {}}}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos()], {EP: reply()}),
    margin("margin_index_exchange", [pos("NIFTY", "NSE_INDEX")], {EP: reply()}),
    margin("margin_no_positions", [], {}),
]
