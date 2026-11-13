"""Upstox GTT: place, modify, cancel and the book, against /v3/order/gtt. The last price comes from the quotes call."""

import scenarios_upstox as u

BROKER = "upstox"
AUTH_TOKEN = u.AUTH_TOKEN
ENV = u.ENV
OPT = "NSE_FO|40001"
SYMBOLS = [
    *u.SYMBOLS,
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY 24000 CE 25 NOV 26", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE_FO", "token": OPT, "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
]


def quote(key, token, sym, ltp):
    return {"body": {"status": "success", "data": {key: u._q(token, sym, ltp, ltp, buy=[(ltp, 1)], sell=[(ltp, 1)])}}}


Q_SBIN = quote("NSE_EQ:SBIN", u.SBIN, "SBIN", 805.45)
Q_INFY = quote("NSE_EQ:INFY", u.INFY, "INFY", 90.0)
Q_FUT = quote("NSE_FO:NIFTY FUT 25 NOV 26", u.FUT, "NIFTY FUT 25 NOV 26", 23980.25)
Q_CE = quote("NSE_FO:NIFTY 24000 CE 25 NOV 26", OPT, "NIFTY 24000 CE 25 NOV 26", 7.5)
Q_ZERO = quote("NSE_EQ:SBIN", u.SBIN, "SBIN", 0)
Q_EMPTY = {"body": {"status": "success", "data": {}}}
Q_REFUSED = {"status": 401, "body": {"status": "error", "errors": [{"errorCode": "UDAPI100050", "message": "Invalid token used to access API"}]}}

QUOTE = "GET /v3/market-quote/quotes"
PLACE_URL = "POST /v3/order/gtt/place"
MODIFY_URL = "PUT /v3/order/gtt/modify"
CANCEL_URL = "DELETE /v3/order/gtt/cancel"
BOOK_URL = "GET /v3/order/gtt"

CREATED = {"body": {"status": "success", "data": {"gtt_order_ids": ["GTT-C25110300000001"]}}}
MODIFIED = {"body": {"status": "success", "data": {"gtt_order_ids": ["GTT-C25110300000001"]}}}
CANCELLED = {"body": {"status": "success", "data": {"gtt_order_ids": ["GTT-C25110300000001"]}}}


def failure(status, code, message):
    return {"status": status, "body": {"status": "error", "errors": [{"error_code": code, "message": message}]}}


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def rule(strategy, status, trigger_price, trigger_type="IMMEDIATE", transaction_type="SELL"):
    return {"strategy": strategy, "status": status, "trigger_type": trigger_type, "trigger_price": trigger_price, "transaction_type": transaction_type}


def row(gid, rules, gtt_type="SINGLE", symbol="SBIN", exchange="NSE_EQ", product="D", qty=10, created=1767430000000000, expires=1798966000000000):
    return {"gtt_order_id": gid, "type": gtt_type, "exchange": exchange, "quantity": qty, "product": product, "trading_symbol": symbol, "instrument_token": u.SBIN,
            "created_at": created, "expires_at": expires, "rules": rules}


BOOK = {"body": {"status": "success", "data": [
    row("GTT-1", [rule("ENTRY", "SCHEDULED", 795.0, "BELOW", "BUY")]),
    row("GTT-2", [rule("ENTRY", "COMPLETED", 795.0, "BELOW", "BUY")]),
    row("GTT-3", [rule("ENTRY", "TRIGGERED", 800.0, "IMMEDIATE", "BUY"), rule("TARGET", "OPEN", 830.0), rule("STOPLOSS", "INACTIVE", 780.0)], "MULTIPLE"),
    row("GTT-4", [rule("ENTRY", "CANCELLED", 795.0, "BELOW", "BUY")]),
    row("GTT-5", [rule("ENTRY", "PENDING", 24100.0, "ABOVE", "SELL")], symbol="NIFTY 24000 CE 25 NOV 26", exchange="NSE_FO", product="D", qty=75),
    row("GTT-6", [rule("ENTRY", "SCHEDULED", 10.0, "BELOW", "BUY")], symbol="UNLISTED"),
    row("GTT-7", [rule("ENTRY", "SCHEDULED", 10.0, "BELOW", "BUY")], product="I", created="2026-11-03T09:15:00Z", expires=""),
    row("GTT-8", [rule("ENTRY", "EXPIRED", 10.0, "BELOW", "BUY")]),
    row("GTT-9", [rule("ENTRY", "INACTIVE", 10.0, "BELOW", "BUY")]),
    row("GTT-10", [rule("ENTRY", "SCHEDULED", 11.0, "BELOW", "BUY")], gtt_type=None),
]}}
EMPTY_BOOK = {"body": {"status": "success", "data": []}}

P, M, C, B = "/api/v1/placegttorder", "/api/v1/modifygttorder", "/api/v1/cancelgttorder", "/api/v1/gttorderbook"


def place(name, request, quote_reply=Q_SBIN, reply=CREATED):
    return {"name": name, "path": P, "request": request, "broker": {QUOTE: quote_reply, PLACE_URL: reply}}


def modify(name, request, quote_reply=Q_SBIN, reply=MODIFIED):
    return {"name": name, "path": M, "request": {"trigger_id": "GTT-C25110300000001", **request}, "broker": {QUOTE: quote_reply, MODIFY_URL: reply}}


SCENARIOS = [
    # Place: SINGLE. The entry direction comes from the trigger against the last price.
    place("place_single_trigger_below_last_price", order()),
    place("place_single_trigger_above_last_price", order(triggerprice_sl="0", triggerprice_tg="820", action="SELL")),
    place("place_single_trigger_equal_to_last_price", order(triggerprice_sl="805.45")),
    place("place_single_limit_price_is_dropped", order(price="700")),
    place("place_single_market_protected", order(pricetype="MARKET", price="0")),
    place("place_single_market_cheap_stock", order(pricetype="MARKET", price="0", symbol="INFY"), Q_INFY),
    place("place_single_nrml_future", order(exchange="NFO", symbol="NIFTY25NOV26FUT", product="NRML", quantity="75", price="23900", triggerprice_sl="23950"), Q_FUT),
    place("place_single_market_option_slab", order(pricetype="MARKET", price="0", exchange="NFO", symbol="NIFTY25NOV2624000CE", product="NRML", quantity="75", triggerprice_sl="8"), Q_CE),
    place("place_single_without_a_last_price_falls_back_to_the_stoploss_side", order(), Q_EMPTY),
    place("place_single_without_a_last_price_falls_back_to_the_target_side", order(triggerprice_sl="0", triggerprice_tg="820", action="SELL"), Q_EMPTY),
    place("place_single_last_price_zero", order(), Q_ZERO),
    place("place_single_quote_refused", order(), Q_REFUSED),
    # Place: OCO becomes a bracket that opens the entry first.
    place("place_oco_sell_exit_opens_a_long_entry", oco()),
    place("place_oco_buy_exit_opens_a_short_entry", oco(action="BUY")),
    place("place_oco_market_protected_around_each_trigger", oco(pricetype="MARKET")),
    place("place_oco_without_a_last_price_is_refused", oco(), Q_EMPTY),
    # Place: the broker's answers.
    place("place_broker_refuses", order(), reply=failure(400, "UDAPI1141", "Entry rule is mandatory")),
    place("place_broker_refuses_with_two_reasons", order(), reply={"status": 400, "body": {"status": "error", "errors": [{"error_code": "UDAPI1137", "message": "Rules needed"}, {"error_code": "", "message": "Second reason"}]}}),
    place("place_broker_session_expired", order(), reply=failure(401, "UDAPI100050", "Invalid token used to access API")),
    place("place_broker_gateway_message", order(), reply={"status": 502, "body": {"message": "Bad gateway"}}),
    place("place_ok_answer_without_an_id", order(), reply={"body": {"status": "success", "data": {}}}),
    place("place_ok_answer_with_a_single_id_field", order(), reply={"body": {"status": "success", "data": {"gtt_order_id": "GTT-ONE"}}}),
    place("place_error_status_in_an_ok_answer", order(), reply={"body": {"status": "error", "errors": [{"error_code": "UDAPI1", "message": "Rejected in an ok answer"}]}}),
    place("place_unknown_symbol", order(symbol="NOPE")),
    # Modify.
    modify("modify_single", order()),
    modify("modify_single_market_has_no_protection", order(pricetype="MARKET", price="0")),
    modify("modify_oco", oco()),
    modify("modify_oco_without_a_last_price_is_refused", oco(), Q_EMPTY),
    modify("modify_broker_refuses", order(), reply=failure(400, "UDAPI1150", "Quantity cannot be changed")),
    modify("modify_error_status_in_an_ok_answer", order(), reply={"body": {"status": "error", "errors": [{"error_code": "UDAPI2", "message": "Rejected in an ok answer"}]}}),
    modify("modify_without_an_id_in_the_answer_keeps_the_given_id", order(), reply={"body": {"status": "success", "data": {}}}),
    modify("modify_unknown_symbol", order(symbol="NOPE")),
    {"name": "modify_missing_trigger_id", "path": M, "request": order(), "broker": {QUOTE: Q_SBIN}},
    # Cancel.
    {"name": "cancel", "path": C, "request": {"strategy": "t", "trigger_id": "GTT-C25110300000001"}, "broker": {CANCEL_URL: CANCELLED}},
    {"name": "cancel_broker_refuses", "path": C, "request": {"strategy": "t", "trigger_id": "GTT-C25110300000001"}, "broker": {CANCEL_URL: failure(404, "UDAPI1160", "GTT not found")}},
    {"name": "cancel_ok_answer_without_an_id", "path": C, "request": {"strategy": "t", "trigger_id": "GTT-X"}, "broker": {CANCEL_URL: {"body": {"status": "success", "data": {}}}}},
    # Book.
    {"name": "book_active_only", "path": B, "request": {}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_all_active_first", "path": B, "request": {"status": "all"}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {BOOK_URL: EMPTY_BOOK}},
    {"name": "book_broker_refuses", "path": B, "request": {}, "broker": {BOOK_URL: failure(401, "UDAPI100050", "Invalid token used to access API")}},
]
