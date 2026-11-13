"""Fyers GTT: place, modify, cancel and the book, against /api/v3/gtt. A leg has only a price, a trigger and a quantity, so a MARKET request becomes a
protected LIMIT. An OCO is two legs of one trigger: the one above the last price first."""

import scenarios_fyers as f

BROKER = "fyers"
AUTH_TOKEN = f.AUTH_TOKEN
ENV = f.ENV
SYMBOLS = [
    *f.SYMBOLS,
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NSE:NIFTY26NOV24000CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "101126112540001", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
]

SYNC = "/api/v3/gtt/orders/sync"
BOOK_PATH = "/api/v3/gtt/orders"
POST, PATCH, DELETE, BOOK_URL = f"POST {SYNC}", f"PATCH {SYNC}", f"DELETE {SYNC}", f"GET {BOOK_PATH}"
DEPTH_URL = "GET /data/depth"


def depth(symbol, ltp):
    return {"body": {"s": "ok", "message": "", "d": {symbol: {"ltp": ltp, "bids": [], "ask": [], "tick_Size": 0.05, "oi": 0}}}}


D_SBIN = depth("NSE:SBIN-EQ", 805.45)
D_INFY = depth("NSE:INFY-EQ", 90.0)
D_CE = depth("NSE:NIFTY26NOV24000CE", 7.5)
D_ZERO = depth("NSE:SBIN-EQ", 0)
D_REFUSED = {"status": 401, "body": {"s": "error", "code": -16, "message": "Could not authenticate the user"}}

CREATED = {"body": {"s": "ok", "code": 1101, "message": "GTT order placed", "id": "G1001"}}
MODIFIED = {"body": {"s": "ok", "code": 1102, "message": "GTT order modified", "id": "G1001"}}
CANCELLED = {"body": {"s": "ok", "code": 1103, "message": "GTT order cancelled", "id": "G1001"}}


def failure(status, message, code=-50):
    return {"status": status, "body": {"s": "error", "code": code, "message": message}}


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def row(gid, status=6, oco_ind=1, symbol="NSE:SBIN-EQ", exchange=10, segment=10, side=1, product="CNC", qty=10, price=790.0, trigger=795.0, qty2=0, price2=0.0, trigger2=0.0, ltp=806.0):
    return {"id": gid, "ord_status": status, "gtt_oco_ind": oco_ind, "exchange": exchange, "segment": segment, "symbol": symbol, "tran_side": side, "product_type": product,
            "qty": qty, "price_limit": price, "price_trigger": trigger, "qty2": qty2, "price2_limit": price2, "price2_trigger": trigger2, "ltp": ltp, "create_time": "2026-11-03 09:15:00"}


BOOK = {"body": {"s": "ok", "code": 200, "orderBook": [
    row("G1"),
    row("G2", status=2),
    row("G3", oco_ind=2, side=-1, price=829.0, trigger=830.0, qty2=10, price2=779.0, trigger2=780.0),
    row("G4", status=1),
    row("G5", status=4, symbol="NSE:NIFTY26NOVFUT", segment=11, product="MARGIN", qty=75, price=0.0, trigger=24100.0),
    row("G6", symbol="NSE:UNKNOWN-EQ"),
    row("G7", status=5),
    row("G8", status=3),
    row("G9", product="INTRADAY"),
    row("G10", exchange=12, segment=10, product="MTF"),
    row("G11", oco_ind=7),
]}}
EMPTY_BOOK = {"body": {"s": "ok", "code": 200, "orderBook": []}}

P, M, C, B = "/api/v1/placegttorder", "/api/v1/modifygttorder", "/api/v1/cancelgttorder", "/api/v1/gttorderbook"


def place(name, request, depth_reply=D_SBIN, reply=CREATED):
    return {"name": name, "path": P, "request": request, "broker": {DEPTH_URL: depth_reply, POST: reply}}


def modify(name, request, depth_reply=D_SBIN, reply=MODIFIED):
    return {"name": name, "path": M, "request": {"trigger_id": "G1001", **request}, "broker": {DEPTH_URL: depth_reply, PATCH: reply}}


SCENARIOS = [
    # Place: SINGLE.
    place("place_single_limit", order()),
    place("place_single_target_trigger", order(action="SELL", price="819", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_both_triggers_stoploss_wins", order(triggerprice_tg="830")),
    place("place_single_nrml_future", order(exchange="NFO", symbol="NIFTY25NOV26FUT", product="NRML", quantity="75", price="23900", triggerprice_sl="23950")),
    place("place_single_market_buy_protected", order(pricetype="MARKET", price="0")),
    place("place_single_market_sell_protected", order(pricetype="MARKET", action="SELL", price="0", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_market_cheap_stock", order(pricetype="MARKET", price="0", symbol="INFY"), D_INFY),
    place("place_single_market_option_slab", order(pricetype="MARKET", price="0", exchange="NFO", symbol="NIFTY25NOV2624000CE", product="NRML", quantity="75", triggerprice_sl="8"), D_CE),
    place("place_single_market_without_a_last_price_sends_the_given_price", order(pricetype="MARKET", price="0"), D_ZERO),
    place("place_single_market_quote_refused_sends_the_given_price", order(pricetype="MARKET", price="0"), D_REFUSED),
    place("place_single_strategy_name_is_cleaned", order(strategy="my-strat_1 !")),
    place("place_single_strategy_name_is_cut_to_twenty", order(strategy="abcdefghijklmnopqrstuvwxyz")),
    place("place_single_strategy_name_with_only_punctuation", order(strategy="--")),
    # Place: OCO. The trigger above the last price is leg one.
    place("place_oco_limit", oco()),
    place("place_oco_buy", oco(action="BUY")),
    place("place_oco_market_protected_around_each_trigger", oco(pricetype="MARKET")),
    place("place_oco_market_buy_protected", oco(pricetype="MARKET", action="BUY")),
    # Place: the broker's answers.
    place("place_broker_refuses", order(), reply=failure(400, "Trigger price is too close to the last price.")),
    place("place_broker_session_expired", order(), reply=failure(401, "Could not authenticate the user", -16)),
    place("place_ok_answer_without_an_id", order(), reply={"body": {"s": "ok", "code": 1101, "message": "done"}}),
    place("place_error_status_in_an_ok_answer", order(), reply={"body": {"s": "error", "code": -50, "message": "Rejected in an ok answer"}}),
    place("place_answer_with_a_code_and_no_status", order(), reply={"body": {"code": 1101, "id": "G1002", "message": "no status field"}}),
    place("place_unknown_symbol", order(symbol="NOPE")),
    # Modify.
    modify("modify_single", order()),
    modify("modify_single_market_protected", order(pricetype="MARKET", price="0")),
    modify("modify_oco", oco()),
    modify("modify_oco_market_protected", oco(pricetype="MARKET")),
    modify("modify_oco_legs_given_the_wrong_way_round_are_swapped", oco(triggerprice_sl="830", stoploss="829", triggerprice_tg="780", target="779")),
    modify("modify_broker_refuses", order(), reply=failure(400, "Cannot modify a triggered order")),
    modify("modify_refused_without_a_reason", order(), reply={"status": 400, "body": {"s": "error"}}),
    modify("modify_error_status_in_an_ok_answer", order(), reply={"body": {"s": "error", "message": "Rejected in an ok answer"}}),
    modify("modify_answer_without_an_id_keeps_the_given_id", order(), reply={"body": {"s": "ok", "code": 1102}}),
    modify("modify_unknown_symbol", order(symbol="NOPE")),
    # Cancel.
    {"name": "cancel", "path": C, "request": {"strategy": "t", "trigger_id": "G1001"}, "broker": {DELETE: CANCELLED}},
    {"name": "cancel_broker_refuses", "path": C, "request": {"strategy": "t", "trigger_id": "G1001"}, "broker": {DELETE: failure(404, "GTT order not found")}},
    {"name": "cancel_refused_without_a_reason", "path": C, "request": {"strategy": "t", "trigger_id": "G1001"}, "broker": {DELETE: {"status": 400, "body": {"s": "error"}}}},
    {"name": "cancel_answer_without_an_id_keeps_the_given_id", "path": C, "request": {"strategy": "t", "trigger_id": "G1001"}, "broker": {DELETE: {"body": {"s": "ok", "code": 1103}}}},
    # Book.
    {"name": "book_active_only", "path": B, "request": {}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_all_active_first", "path": B, "request": {"status": "all"}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {BOOK_URL: EMPTY_BOOK}},
    {"name": "book_broker_refuses", "path": B, "request": {}, "broker": {BOOK_URL: failure(401, "Could not authenticate the user", -16)}},
]
