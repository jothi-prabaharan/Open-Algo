"""Rupeezy GTT: place, modify, cancel and the book, against /v2/trading/orders/gtt. Single and OCO; Vortex names OCO legs by role."""

import scenarios_rupeezy as r

BROKER = "rupeezy"
AUTH_TOKEN = r.AUTH_TOKEN
ENV = r.ENV
SEED_API_KEY_ROW = True
SYMBOLS = r.SYMBOLS

G = "/v2/trading/orders/gtt"
PLACE_URL, BOOK_URL = f"POST {G}", f"GET {G}"
MODIFY_URL, CANCEL_URL = f"PUT {G}/GTT1", f"DELETE {G}/GTT1"
CREATED = {"body": {"status": "success", "data": {"order_id": "GTT1"}}}
OK = {"body": {"status": "success"}}
REFUSED = {"status": 400, "body": {"status": "error", "code": "invalid", "message": "Trigger price is not valid"}}


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def leg(lid, trigger, price=0.0, variety="RL", status="active", side="SELL", qty=10, **extra):
    d = {"id": lid, "trigger_price": trigger, "price": price, "variety": variety, "status": status, "transaction_type": side, "quantity": qty,
         "created_at": "2026-11-03 10:00:00", "updated_at": "2026-11-03 10:05:00", "expires_at": "2027-11-03 10:00:00"}
    d.update(extra)
    return d


def gtt(gid, legs, kind="single", ticker="NSE:SBIN", exchange="NSE_EQ", product="DELIVERY", side="SELL", lot=1, **extra):
    d = {"id": gid, "ticker": ticker, "exchange": exchange, "product": product, "transaction_type": side, "trigger_type": kind, "lot_size": lot, "orders": legs}
    d.update(extra)
    return d


SINGLE = gtt("GTT1", [leg("L1", 795.0, 790.0, side="BUY")])
OCOGTT = gtt("GTT1", [leg("L2", 830.0, 829.0), leg("L1", 780.0, 779.0, trail={"id": "T1", "trail_jump_point": 2, "trail_jump_type": "points"})], "oco")
BOOK_ONE = lambda g: {"body": {"status": "success", "data": [g]}}

P, M, C, B = "/api/v1/placegttorder", "/api/v1/modifygttorder", "/api/v1/cancelgttorder", "/api/v1/gttorderbook"


def place(name, request, reply=CREATED):
    return {"name": name, "path": P, "request": request, "broker": {PLACE_URL: reply}}


def modify(name, request, book=None, reply=OK):
    return {"name": name, "path": M, "request": {"trigger_id": "GTT1", **request}, "broker": {BOOK_URL: book or BOOK_ONE(SINGLE), MODIFY_URL: reply}}


SCENARIOS = [
    place("place_single", order()),
    place("place_single_market", order(pricetype="MARKET", price="0")),
    place("place_single_target_side_trigger", order(triggerprice_sl="0", triggerprice_tg="820", action="SELL")),
    place("place_single_mis", order(product="MIS")),
    place("place_oco_sell", oco()),
    place("place_oco_buy", oco(action="BUY")),
    place("place_oco_market", oco(pricetype="MARKET")),
    place("place_mcx_lots", order(exchange="MCX", symbol="GOLDM25NOV26FUT", product="NRML", quantity="20", price="75000", triggerprice_sl="75100")),
    place("place_mcx_not_whole_lots", order(exchange="MCX", symbol="GOLDM25NOV26FUT", product="NRML", quantity="15", price="75000", triggerprice_sl="75100")),
    place("place_broker_refuses", order(), REFUSED),
    place("place_broker_refuses_without_a_message", order(), {"status": 500, "body": {"x": 1}}),
    place("place_ok_without_an_id", order(), {"body": {"status": "success", "data": {}}}),
    modify("modify_single", order()),
    modify("modify_single_market", order(pricetype="MARKET", price="0")),
    modify("modify_oco_keeps_the_trailing_stop", oco(), BOOK_ONE(OCOGTT)),
    modify("modify_single_to_oco_is_refused", oco()),
    modify("modify_oco_to_single_is_refused", order(), BOOK_ONE(OCOGTT)),
    modify("modify_not_in_the_book", order(), {"body": {"status": "success", "data": []}}),
    modify("modify_book_unreadable", order(), {"status": 401, "body": {"status": "error", "message": "Session expired"}}),
    modify("modify_broker_refuses", order(), reply=REFUSED),
    modify("modify_mcx_not_whole_lots", order(exchange="MCX", symbol="GOLDM25NOV26FUT", product="NRML", quantity="15"), BOOK_ONE(gtt("GTT1", [leg("L1", 75100.0, 75000.0)], ticker="MCX:GOLDM25NOV26FUT", exchange="MCX_FO", lot=10))),
    {"name": "modify_missing_trigger_id", "path": M, "request": order(), "broker": {BOOK_URL: BOOK_ONE(SINGLE)}},
    {"name": "cancel", "path": C, "request": {"strategy": "t", "trigger_id": "GTT1"}, "broker": {CANCEL_URL: OK}},
    {"name": "cancel_broker_refuses", "path": C, "request": {"strategy": "t", "trigger_id": "GTT1"}, "broker": {CANCEL_URL: {"status": 404, "body": {"status": "error", "message": "GTT not found"}}}},
    {"name": "book_active_only", "path": B, "request": {}, "broker": {BOOK_URL: {"body": {"status": "success", "data": [
        SINGLE, OCOGTT,
        gtt("GTT2", [leg("L3", 800.0, status="triggered"), leg("L4", 780.0, status="cancelled")], "oco"),
        gtt("GTT3", [leg("L5", 800.0, status="cancelled")]),
        gtt("GTT4", [leg("L6", 75100.0, 0.0, "RL-MKT", qty=2)], ticker="MCX:GOLDM25NOV26FUT", exchange="MCX_FO", product="INTRADAY", lot=10),
        gtt("GTT5", [leg("L7", 10.0)], ticker="NSE:UNLISTED"),
        gtt("GTT6", [], symbol="SBIN", ticker=""),
    ]}}}},
    {"name": "book_all", "path": B, "request": {"status": "all"}, "broker": {BOOK_URL: {"body": {"status": "success", "data": [
        SINGLE, OCOGTT,
        gtt("GTT2", [leg("L3", 800.0, status="triggered"), leg("L4", 780.0, status="cancelled")], "oco"),
        gtt("GTT3", [leg("L5", 800.0, status="cancelled")]),
        gtt("GTT4", [leg("L6", 75100.0, 0.0, "RL-MKT", qty=2)], ticker="MCX:GOLDM25NOV26FUT", exchange="MCX_FO", product="INTRADAY", lot=10),
        gtt("GTT5", [leg("L7", 10.0)], ticker="NSE:UNLISTED"),
        gtt("GTT6", [], symbol="SBIN", ticker=""),
    ]}}}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {BOOK_URL: {"body": {"status": "success", "data": []}}}},
    {"name": "book_broker_refuses", "path": B, "request": {}, "broker": {BOOK_URL: {"status": 401, "body": {"status": "error", "message": "Session expired"}}}},
]
