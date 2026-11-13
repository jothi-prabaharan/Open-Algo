"""Dhan GTT: Forever Orders. A SINGLE is one leg, an OCO is two legs of one order; a modify changes one leg at a time."""

import scenarios_dhan as d

BROKER = "dhan"
AUTH_TOKEN = d.AUTH_TOKEN
ENV = d.ENV
SYMBOLS = d.SYMBOLS
CLIENT_ID = d.CLIENT_ID

def patch_clients(client):
    """Forever Orders go through a client of their own; the recorder's replaces it."""
    import broker.dhan.api.gtt_api as gtt_api

    class _Holder:
        def get(self):
            return client

    gtt_api._dhan_gtt_client = _Holder()


FOREVER = "/v2/forever/orders"
PLACE_URL = f"POST {FOREVER}"
BOOK_URL = f"GET {FOREVER}"
PUT_URL = f"PUT {FOREVER}/9001"
DELETE_URL = f"DELETE {FOREVER}/9001"

CREATED = {"body": {"orderId": "9001", "orderStatus": "TRANSIT"}}
CHANGED = {"body": {"orderId": "9001", "orderStatus": "CONFIRM"}}
CANCELLED = {"body": {"orderId": "9001", "orderStatus": "CANCELLED"}}


def failure(status, code, message):
    return {"status": status, "body": {"errorType": "Order_Error", "errorCode": code, "errorMessage": message}}


def leg(oid, leg_name, status, price, trigger, side="BUY", flag="SINGLE", symbol="SBIN", segment="NSE_EQ", product="CNC", qty=10):
    return {"dhanClientId": CLIENT_ID, "orderId": oid, "orderStatus": status, "transactionType": side, "exchangeSegment": segment, "productType": product,
            "orderType": flag, "tradingSymbol": symbol, "securityId": "3045", "quantity": qty, "price": price, "triggerPrice": trigger, "legName": leg_name,
            "createTime": "2026-11-03 09:15:00", "updateTime": "2026-11-03 09:16:00"}


BOOK = {"body": [
    leg("1", "ENTRY_LEG", "CONFIRM", 790.0, 795.0),
    leg("2", "ENTRY_LEG", "TRADED", 790.0, 795.0),
    leg("3", "TARGET_LEG", "PENDING", 829.0, 830.0, "SELL", "OCO"),
    leg("3", "STOP_LOSS_LEG", "PENDING", 779.0, 780.0, "SELL", "OCO"),
    leg("4", "ENTRY_LEG", "TRANSIT", 0, 24100.0, "BUY", "SINGLE", "NIFTY-Nov2026-FUT", "NSE_FNO", "MARGIN", 75),
    leg("5", "ENTRY_LEG", "CANCELLED", 790.0, 795.0),
    leg("6", "ENTRY_LEG", "PENDING", 10.0, 11.0, "BUY", "SINGLE", "UNLISTED"),
    leg("7", "ENTRY_LEG", "EXPIRED", 790.0, 795.0),
    {**leg("", "ENTRY_LEG", "PENDING", 1.0, 2.0)},
]}
EMPTY_BOOK = {"body": []}

P, M, C, B = "/api/v1/placegttorder", "/api/v1/modifygttorder", "/api/v1/cancelgttorder", "/api/v1/gttorderbook"


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def place(name, request, reply=CREATED):
    return {"name": name, "path": P, "request": request, "broker": {PLACE_URL: reply}}


def modify(name, request, replies=None, book=None):
    broker = {PUT_URL: CHANGED}
    if book is not None:
        broker[BOOK_URL] = book
    broker.update(replies or {})
    return {"name": name, "path": M, "request": {"trigger_id": "9001", **request}, "broker": broker}


STORED_ENTRY = {"body": [leg("9001", "ENTRY_LEG", "CONFIRM", 790.0, 795.0)]}
STORED_STOP = {"body": [leg("9001", "STOP_LOSS_LEG", "CONFIRM", 790.0, 795.0)]}
STORED_LOWER = {"body": [leg("9001", "target_leg", "CONFIRM", 790.0, 795.0)]}
STORED_NONE = {"body": [leg("5555", "TARGET_LEG", "CONFIRM", 790.0, 795.0), leg("9001", "STOP_LOSS_LEG", "CONFIRM", 790.0, 795.0)]}
STORED_ELSEWHERE = {"body": [leg("5555", "TARGET_LEG", "CONFIRM", 790.0, 795.0)]}
STORED_BLANK_LEG = {"body": [leg("9001", "", "CONFIRM", 790.0, 795.0)]}

SCENARIOS = [
    # Place.
    place("place_single_limit", order()),
    place("place_single_target_trigger", order(action="SELL", price="819", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_market", order(pricetype="MARKET", price="0")),
    place("place_single_nrml_future", order(exchange="NFO", symbol="NIFTY25NOV26FUT", product="NRML", quantity="75", price="23900", triggerprice_sl="23950")),
    place("place_single_strategy_name_is_cut_to_thirty_characters", order(strategy="a-strategy-name-that-is-much-longer-than-thirty")),
    place("place_single_mis_product", order(product="MIS")),
    place("place_oco_limit", oco()),
    place("place_oco_market", oco(pricetype="MARKET")),
    place("place_broker_refuses", order(), failure(400, "DH-905", "Invalid trigger price")),
    place("place_broker_session_expired", order(), failure(401, "DH-901", "Invalid access token")),
    place("place_ok_answer_without_an_order_id", order(), {"body": {"orderStatus": "TRANSIT"}}),
    place("place_created_status", order(), {"status": 201, "body": {"orderId": "9001", "orderStatus": "TRANSIT"}}),
    place("place_unknown_symbol", order(symbol="NOPE")),
    # Modify: a SINGLE changes the leg Dhan stored it under.
    modify("modify_single_entry_leg", order(), book=STORED_ENTRY),
    modify("modify_single_stored_as_a_stop_loss_leg", order(), book=STORED_STOP),
    modify("modify_single_leg_name_in_lower_case", order(), book=STORED_LOWER),
    modify("modify_single_not_in_the_book_uses_the_entry_leg", order(), book=STORED_ELSEWHERE),
    modify("modify_single_picks_its_own_orders_leg_among_others", order(), book=STORED_NONE),
    modify("modify_single_leg_without_a_name_uses_the_entry_leg", order(), book=STORED_BLANK_LEG),
    modify("modify_single_book_unreadable_uses_the_entry_leg", order(), {BOOK_URL: failure(500, "DH-900", "down")}),
    modify("modify_single_limit_with_zero_price_becomes_market", order(price="0", pricetype="LIMIT"), book=STORED_ENTRY),
    modify("modify_single_market", order(pricetype="MARKET", price="0"), book=STORED_ENTRY),
    modify("modify_oco_changes_the_stop_then_the_target", oco()),
    modify("modify_oco_market", oco(pricetype="MARKET")),
    modify("modify_oco_stop_leg_refused_stops_there", oco(), {PUT_URL: failure(400, "DH-905", "Trigger too close")}),
    modify("modify_single_refused", order(), {PUT_URL: failure(400, "DH-905", "Trigger too close")}, book=STORED_ENTRY),
    modify("modify_answer_without_an_order_id", order(), {PUT_URL: {"body": {"orderStatus": "CONFIRM"}}}, book=STORED_ENTRY),
    modify("modify_refused_with_a_flat_message", order(), {PUT_URL: {"status": 400, "body": {"message": "Bad request"}}}, book=STORED_ENTRY),
    modify("modify_refused_without_any_reason", order(), {PUT_URL: {"status": 400, "body": {"x": 1}}}, book=STORED_ENTRY),
    modify("modify_unknown_symbol", order(symbol="NOPE"), book=STORED_ENTRY),
    # Cancel.
    {"name": "cancel", "path": C, "request": {"strategy": "t", "trigger_id": "9001"}, "broker": {DELETE_URL: CANCELLED}},
    {"name": "cancel_broker_refuses", "path": C, "request": {"strategy": "t", "trigger_id": "9001"}, "broker": {DELETE_URL: failure(404, "DH-905", "Order not found")}},
    {"name": "cancel_ok_answer_without_an_order_id", "path": C, "request": {"strategy": "t", "trigger_id": "9001"}, "broker": {DELETE_URL: {"body": {"orderStatus": "CANCELLED"}}}},
    {"name": "cancel_refused_with_a_flat_message", "path": C, "request": {"strategy": "t", "trigger_id": "9001"}, "broker": {DELETE_URL: {"status": 400, "body": {"message": "Bad request"}}}},
    {"name": "cancel_refused_without_any_reason", "path": C, "request": {"strategy": "t", "trigger_id": "9001"}, "broker": {DELETE_URL: {"status": 400, "body": {"x": 1}}}},
    # Book.
    {"name": "book_active_only", "path": B, "request": {}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_all_active_first", "path": B, "request": {"status": "all"}, "broker": {BOOK_URL: BOOK}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {BOOK_URL: EMPTY_BOOK}},
    {"name": "book_broker_refuses", "path": B, "request": {}, "broker": {BOOK_URL: failure(401, "DH-901", "Invalid access token")}},
]
