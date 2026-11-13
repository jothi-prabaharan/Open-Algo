"""/margin on TradeSmart: one GetOrderMargin call per leg, the margins added up."""

import scenarios_tradesmart as s
from scenarios_tradesmart import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, BASE  # noqa: F401

BROKER = "tradesmart"
SYMBOLS = s.SYMBOLS
EP = f"POST {BASE}/GetOrderMargin"
READ_POSTS = s.READ_POSTS + [f"{BASE}/GetOrderMargin"]


def ok(**fields):
    return {"body": {"stat": "Ok", "request_time": "10:20:30 03-11-2026", **fields}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="LIMIT", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_leg", [pos(price="805.5")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_market_leg_is_not_priced", [pos(pricetype="MARKET")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_stop_market_leg", [pos(pricetype="SL-M", trigger_price="801.5")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_stop_limit_leg", [pos(pricetype="SL", price="800", trigger_price="801.5")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_sell_leg", [pos(action="SELL", price="805.5")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML", "LIMIT", price="24005")], {EP: ok(ordermargin="150000.5")}),
    margin("margin_ampersand_symbol", [pos("M&M", price="2800")], {EP: ok(ordermargin="1608.05")}),
    margin("margin_legs_are_added_up", [pos(price="805.5"), pos("INFY", action="SELL", price="1500"), pos("NIFTY25NOV26FUT", "NFO", "BUY", "75", "NRML", "LIMIT", price="24000")],
           {EP: [ok(ordermargin="100.5"), ok(ordermargin="200.25"), ok(ordermargin="300")]}),
    margin("margin_falls_back_to_margin_used", [pos(price="805.5")], {EP: ok(marginused="777.7")}),
    margin("margin_both_fields_prefers_the_order_margin", [pos(price="805.5")], {EP: ok(ordermargin="100.5", marginused="999")}),
    margin("margin_with_neither_field_is_zero", [pos(price="805.5")], {EP: ok()}),
    margin("margin_with_a_blank_field_is_zero", [pos(price="805.5")], {EP: ok(ordermargin="")}),
    margin("margin_with_text_that_is_not_a_number", [pos(price="805.5")], {EP: ok(ordermargin="abc")}),
    margin("margin_broker_refuses", [pos(price="805.5")], {EP: {"body": {"stat": "Not_Ok", "emsg": "Invalid Input : bad order"}}}),
    margin("margin_refuses_with_nothing", [pos(price="805.5")], {EP: {"body": {"stat": "Not_Ok"}}}),
    margin("margin_second_leg_refused_stops_there", [pos(price="805.5"), pos("INFY", price="1500"), pos("SBIN", price="800")], {EP: [ok(ordermargin="100"), {"body": {"stat": "Not_Ok", "emsg": "Insufficient data"}}]}),
    margin("margin_session_expired", [pos(price="805.5")], {EP: {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos(price="805.5")], {EP: ok(ordermargin="1608.05")}),
]
