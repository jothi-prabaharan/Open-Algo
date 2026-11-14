"""/margin on Firstock: one POST /basketMargin, the first leg flat at the top of the body and the rest in BasketList_Params; a market order or a stop-market leg is priced a slab beyond the last price."""

import scenarios_firstock as s
from scenarios_firstock import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS, V, quote  # noqa: F401

BROKER = "firstock"
EP = f"POST {V}/basketMargin"
QQ = f"POST {V}/getQuote"


def ok(traded=126783.5, **extra):
    d = {"BasketMargin": [], "MarginOnNewOrder": 126783, "PreviousMargin": 0, "TradedMargin": traded}
    d.update(extra)
    return {"body": {"status": "success", "data": d}}


def pos(symbol="NIFTY25NOV26FUT", exchange="NFO", action="SELL", quantity="75", product="NRML", pricetype="MARKET", **extra):
    return {"symbol": symbol, "exchange": exchange, "action": action, "quantity": quantity, "product": product, "pricetype": pricetype, **extra}


def margin(name, positions, broker=None, **extra):
    return {"name": name, "path": "/api/v1/margin", "request": {"positions": positions}, "broker": {} if broker is None else broker, **extra}


SCENARIOS = [
    margin("margin_one_future_priced_off_the_last_price", [pos()], {EP: ok(), QQ: quote("24005.5")}),
    margin("margin_a_limit_leg_keeps_its_price", [pos(pricetype="LIMIT", price="24005.5")], {EP: ok()}),
    margin("margin_a_stop_limit_leg", [pos(pricetype="SL", price="24000", trigger_price="24010")], {EP: ok()}),
    margin("margin_a_stop_market_leg_is_priced_off_the_last_price", [pos(pricetype="SL-M", trigger_price="24010")], {EP: ok(), QQ: quote("24005.5")}),
    margin("margin_a_market_leg_with_no_last_price_takes_its_own_price", [pos(price="24000")], {EP: ok(), QQ: quote("0")}),
    margin("margin_a_stop_market_leg_with_no_last_price_takes_its_trigger", [pos(pricetype="SL-M", trigger_price="24010")], {EP: ok(), QQ: quote("0")}),
    margin("margin_a_market_leg_when_the_quote_fails", [pos(price="24000")], {EP: ok(), QQ: {"body": {"status": "failed", "error": {"message": "no quote"}}}}),
    margin("margin_several_legs_are_the_first_flat_and_the_rest_nested", [pos(), pos("NIFTY25NOV2624000CE", "NFO", "BUY"), pos("SBIN", "NSE", "BUY", "10", "MIS")], {EP: ok(55000.5), QQ: [quote("24005.5"), quote("7.35"), quote("805.45")]}),
    margin("margin_products_are_mapped", [pos(product="MIS"), pos(product="CNC", symbol="SBIN", exchange="NSE")], {EP: ok(), QQ: [quote("24005.5"), quote("805.45")]}),
    margin("margin_the_ampersand_in_a_name_is_escaped", [pos("M&M", "NSE", "BUY", "10", "MIS", "LIMIT", price="2900")], {EP: ok()}),
    margin("margin_text_traded_margin", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "success", "data": {"TradedMargin": "1,500"}}}}),
    margin("margin_nothing_in_the_answer", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "success", "data": {}}}}),
    margin("margin_refused_with_a_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "failed", "error": {"message": "Invalid basket"}}}}),
    margin("margin_refused_with_a_top_level_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "failed", "message": "Bad basket"}}}),
    margin("margin_refused_with_the_short_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "failed", "emsg": "Short"}}}),
    margin("margin_refused_without_a_message", [pos(pricetype="LIMIT", price="1")], {EP: {"body": {"status": "failed"}}}),
    margin("margin_an_answer_that_is_not_json", [pos(pricetype="LIMIT", price="1")], {EP: {"status": 500, "body": "not json"}}),
    margin("margin_unknown_symbol_alone", [pos("NOSUCH", pricetype="LIMIT", price="1")], {EP: ok()}),
    margin("margin_unknown_symbol_among_others", [pos(pricetype="LIMIT", price="24000"), pos("NOSUCH", pricetype="LIMIT", price="1")], {EP: ok()}),
]


def patch():
    pass
