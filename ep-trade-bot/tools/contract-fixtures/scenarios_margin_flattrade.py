"""/margin on Flattrade: a basket to GetBasketMargin, with market and stop-market legs priced and protected as on orders."""

import scenarios_flattrade as s
from scenarios_flattrade import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, BASE, quote, _sym  # noqa: F401

BROKER = "flattrade"
SYMBOLS = s.SYMBOLS + [_sym("NOTICK", "NOTICK-EQ", "NSE", "NSE", "7777", "NOTICK", tick=0)]
EP = f"POST {BASE}/GetBasketMargin"
Q = f"POST {BASE}/GetQuotes"
READ_POSTS = s.READ_POSTS + [f"{BASE}/GetBasketMargin"]
OK = {"body": {"stat": "Ok", "request_time": "10:20:30 03-11-2026", "marginused": "1608.05", "remarks": "Successfully Completed"}}
NO_QUOTE = {"body": {"stat": "Not_Ok", "emsg": "no quote"}}


def ok(**fields):
    return {"body": {"stat": "Ok", "request_time": "10:20:30 03-11-2026", **fields}}


def pos(symbol="SBIN", exchange="NSE", action="BUY", quantity="10", product="MIS", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


def no_tick():
    q = quote(tsym="NOTICK-EQ", token="7777")
    del q["body"]["ti"]
    return q


SCENARIOS = [
    margin("margin_limit_order", [pos(pricetype="LIMIT", price="805.5")], {EP: OK}),
    margin("margin_market_priced_from_a_quote", [pos()], {Q: quote(), EP: OK}),
    margin("margin_sell_market_priced_from_a_quote", [pos(action="SELL")], {Q: quote(), EP: OK}),
    margin("margin_market_quote_without_a_tick_uses_the_master_tick", [pos()], {Q: quote(tick="0"), EP: OK}),
    margin("margin_market_quote_tick_differs_from_the_master", [pos()], {Q: quote(tick="5"), EP: OK}),
    margin("margin_sell_market_penny_price_protects_to_nothing", [pos(action="SELL", price="0.05")], {Q: quote(ltp="0.05"), EP: OK}),
    margin("margin_market_no_tick_anywhere_is_unprotected", [pos("NOTICK")], {Q: no_tick(), EP: OK}),
    margin("margin_market_without_a_quote_keeps_the_price_given", [pos(price="800")], {Q: NO_QUOTE, EP: OK}),
    margin("margin_market_quote_has_no_price", [pos(price="801.5")], {Q: quote(ltp="0"), EP: OK}),
    margin("margin_market_no_quote_no_price_is_refused", [pos()], {Q: NO_QUOTE, EP: OK}),
    margin("margin_stop_market_priced_off_the_trigger", [pos(pricetype="SL-M", trigger_price="801.5")], {Q: quote(), EP: OK}),
    margin("margin_sell_stop_market_priced_off_the_trigger", [pos(action="SELL", pricetype="SL-M", trigger_price="801.5")], {Q: quote(), EP: OK}),
    margin("margin_stop_market_without_a_trigger_uses_the_last_price", [pos(pricetype="SL-M")], {Q: quote(), EP: OK}),
    margin("margin_stop_market_without_a_quote_uses_the_trigger", [pos(pricetype="SL-M", trigger_price="801.5")], {Q: NO_QUOTE, EP: OK}),
    margin("margin_stop_market_nothing_to_price_from_is_refused", [pos(pricetype="SL-M")], {Q: NO_QUOTE, EP: OK}),
    margin("margin_stop_limit", [pos(pricetype="SL", price="800", trigger_price="801.5")], {EP: OK}),
    margin("margin_futures_nrml", [pos("NIFTY25NOV26FUT", "NFO", "SELL", "75", "NRML", "LIMIT", price="24005")], {EP: OK}),
    margin("margin_ampersand_symbol", [pos("M&M", pricetype="LIMIT", price="2800")], {EP: OK}),
    margin("margin_basket_of_three", [pos(pricetype="LIMIT", price="805.5"), pos("INFY", action="SELL", pricetype="LIMIT", price="1500"), pos("NIFTY25NOV26FUT", "NFO", "BUY", "75", "NRML", "LIMIT", price="24000")], {EP: OK}),
    margin("margin_uses_the_margin_after_the_trade", [pos(pricetype="LIMIT", price="805.5")], {EP: ok(marginused="2000.5", marginusedtrade="1200.25")}),
    margin("margin_blank_margin_after_the_trade_uses_the_total", [pos(pricetype="LIMIT", price="805.5")], {EP: ok(marginused="2000.5", marginusedtrade="")}),
    margin("margin_broker_refuses", [pos(pricetype="LIMIT", price="805.5")], {EP: {"body": {"stat": "Not_Ok", "emsg": "Invalid Input : bad basket"}}}),
    margin("margin_refuses_with_remarks_only", [pos(pricetype="LIMIT", price="805.5")], {EP: {"body": {"stat": "Not_Ok", "remarks": "Insufficient data"}}}),
    margin("margin_refuses_with_nothing", [pos(pricetype="LIMIT", price="805.5")], {EP: {"body": {"stat": "Not_Ok"}}}),
    margin("margin_session_expired", [pos(pricetype="LIMIT", price="805.5")], {EP: {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH")], {}),
    margin("margin_unknown_symbol_among_others", [pos("NOSUCH"), pos(pricetype="LIMIT", price="805.5")], {EP: OK}),
]
