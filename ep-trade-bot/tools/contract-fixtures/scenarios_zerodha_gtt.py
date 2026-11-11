"""Zerodha GTT: place, modify, cancel and the book, against Kite's /gtt/triggers."""

import scenarios_zerodha as z

BROKER = "zerodha"

SYMBOLS = [
    *z.SYMBOLS,
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY26NOV24000CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "9100001::::35550", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
    {"symbol": "CRUDEOIL18DEC30FUT", "brsymbol": "CRUDEOIL30DECFUT", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000001::::470001", "expiry": "18-DEC-30", "strike": -1.0, "lotsize": 100, "instrumenttype": "FUT", "tick_size": 1.0},
]


def quote(key, ltp):
    return {"body": {"status": "success", "data": {key: {"instrument_token": 1, "last_price": ltp, "volume": 1, "oi": 0, "ohlc": {"open": ltp, "high": ltp, "low": ltp, "close": ltp}, "depth": {"buy": [], "sell": []}}}}}


Q_SBIN = quote("NSE:SBIN", 805.45)
Q_INFY = quote("NSE:INFY", 1500.0)
Q_CE = quote("NFO:NIFTY26NOV24000CE", 7.5)
Q_CRUDE = quote("MCX:CRUDEOIL30DECFUT", 6500.0)
Q_ZERO = quote("NSE:SBIN", 0)

CREATED = {"body": {"status": "success", "data": {"trigger_id": 123456}}}
MODIFIED = {"body": {"status": "success", "data": {"trigger_id": 123456}}}
DELETED = {"body": {"status": "success", "data": {"trigger_id": 123456}}}


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def book_row(gid, status, trigger_type="single", symbol="SBIN", exchange="NSE", qty=10, values=(795.0,), orders=1):
    return {
        "id": gid, "user_id": "AB1234", "type": trigger_type, "status": status,
        "condition": {"exchange": exchange, "tradingsymbol": symbol, "trigger_values": list(values), "last_price": 806.0},
        "orders": [{"exchange": exchange, "tradingsymbol": symbol, "transaction_type": "BUY", "quantity": qty, "order_type": "LIMIT", "product": "CNC", "price": 790.0 + i} for i in range(orders)],
        "created_at": "2026-11-03 09:15:00", "updated_at": "2026-11-03 09:16:00", "expires_at": "2027-11-03 09:15:00",
    }


BOOK = {"body": {"status": "success", "data": [
    book_row(1, "disabled"),
    book_row(2, "active"),
    book_row(3, "triggered", "two-leg", values=(780.0, 830.0), orders=2),
    book_row(4, "active", "two-leg", values=(780.0, 830.0), orders=2),
    book_row(5, "active", symbol="UNLISTED"),
    book_row(6, "active", symbol="CRUDEOIL30DECFUT", exchange="MCX", qty=2, values=(6400.0,)),
]}}
EMPTY_BOOK = {"body": {"status": "success", "data": []}}

P = "/api/v1/placegttorder"
M = "/api/v1/modifygttorder"
C = "/api/v1/cancelgttorder"
B = "/api/v1/gttorderbook"
POST = "POST /gtt/triggers"
PUT = "PUT /gtt/triggers/123456"
DELETE = "DELETE /gtt/triggers/123456"


def place(name, request, quote_reply=Q_SBIN, reply=CREATED):
    return {"name": name, "path": P, "request": request, "broker": {"GET /quote": quote_reply, POST: reply}}


def modify(name, request, quote_reply=Q_SBIN, reply=MODIFIED):
    return {"name": name, "path": M, "request": {"trigger_id": "123456", **request}, "broker": {"GET /quote": quote_reply, PUT: reply}}


SCENARIOS = [
    # Place: SINGLE.
    place("place_single_stoploss_trigger", order()),
    place("place_single_target_trigger", order(action="SELL", price="819", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_both_triggers_stoploss_wins", order(triggerprice_tg="830")),
    place("place_single_nrml_future", order(exchange="NFO", symbol="NIFTY25NOV26FUT", product="NRML", quantity="75", price="23900", triggerprice_sl="23950"), quote("NFO:NIFTY26NOVFUT", 23980.25)),
    place("place_single_market_buy_protected", order(pricetype="MARKET", price="0")),
    place("place_single_market_sell_protected", order(pricetype="MARKET", action="SELL", price="0", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_market_cheap_stock", order(pricetype="MARKET", price="0", symbol="INFY"), quote("NSE:INFY", 90.0)),
    place("place_single_market_option_slab", order(pricetype="MARKET", price="0", exchange="NFO", symbol="NIFTY25NOV2624000CE", product="NRML", quantity="75", triggerprice_sl="8"), Q_CE),
    place("place_single_limit_lowercase_action", order(action="buy")),
    place("place_single_numbers_as_text", order(quantity="10", price="790.5", triggerprice_sl="795.25")),
    # Place: OCO.
    place("place_oco_limit", oco()),
    place("place_oco_market_protected_around_each_trigger", oco(pricetype="MARKET")),
    place("place_oco_market_buy_protected", oco(pricetype="MARKET", action="BUY")),
    # Place: MCX quantity in contracts.
    place("place_mcx_whole_lots", order(exchange="MCX", symbol="CRUDEOIL18DEC30FUT", product="NRML", quantity="200", price="6400", triggerprice_sl="6450"), Q_CRUDE),
    place("place_mcx_part_lot_refused", order(exchange="MCX", symbol="CRUDEOIL18DEC30FUT", product="NRML", quantity="150", price="6400", triggerprice_sl="6450"), Q_CRUDE),
    # Place: refused before any broker call.
    {"name": "place_invalid_key", "path": P, "request": order(apikey="wrong")},
    {"name": "place_missing_fields", "path": P, "request": {"strategy": "t"}},
    {"name": "place_bad_trigger_type", "path": P, "request": order(trigger_type="TRIPLE")},
    {"name": "place_product_mis_refused", "path": P, "request": order(product="MIS")},
    {"name": "place_bad_exchange", "path": P, "request": order(exchange="LSE")},
    {"name": "place_bad_action", "path": P, "request": order(action="HOLD")},
    {"name": "place_zero_quantity", "path": P, "request": order(quantity="0")},
    {"name": "place_fractional_quantity", "path": P, "request": order(quantity="1.5")},
    {"name": "place_negative_price", "path": P, "request": order(price="-1")},
    {"name": "place_bad_pricetype", "path": P, "request": order(pricetype="SL")},
    {"name": "place_single_without_trigger", "path": P, "request": order(triggerprice_sl="0")},
    {"name": "place_oco_without_target", "path": P, "request": oco(target="0")},
    {"name": "place_oco_without_stoploss_trigger", "path": P, "request": oco(triggerprice_sl="0")},
    {"name": "place_oco_stoploss_not_below_target", "path": P, "request": oco(triggerprice_sl="830", triggerprice_tg="830")},
    # Place: the broker's answers.
    place("place_broker_refuses", order(), reply={"status": 400, "body": {"status": "error", "message": "Trigger price is too close to the last price.", "error_type": "InputException"}}),
    place("place_broker_session_expired", order(), reply={"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}),
    place("place_last_price_zero", order(), Q_ZERO),
    place("place_broker_error_in_an_ok_answer", order(), reply={"body": {"status": "error", "message": "Trigger could not be created."}}),
    place("place_unknown_symbol", order(symbol="NOPE")),
    # Modify.
    modify("modify_single", order()),
    modify("modify_single_market_protected", order(pricetype="MARKET", price="0")),
    modify("modify_oco", oco()),
    modify("modify_oco_market_protected", oco(pricetype="MARKET")),
    modify("modify_mcx_whole_lots", order(exchange="MCX", symbol="CRUDEOIL18DEC30FUT", product="NRML", quantity="300", price="6400", triggerprice_sl="6450"), Q_CRUDE),
    modify("modify_mcx_part_lot_refused", order(exchange="MCX", symbol="CRUDEOIL18DEC30FUT", product="NRML", quantity="250", price="6400", triggerprice_sl="6450"), Q_CRUDE),
    modify("modify_broker_refuses_unknown_trigger", order(), reply={"status": 404, "body": {"status": "error", "message": "Trigger not found.", "error_type": "InputException"}}),
    modify("modify_last_price_zero", order(), Q_ZERO),
    modify("modify_broker_error_in_an_ok_answer", order(), reply={"body": {"status": "error", "message": "Trigger could not be changed."}}),
    modify("modify_unknown_symbol", order(symbol="NOPE")),
    {"name": "modify_missing_trigger_id", "path": M, "request": order()},
    {"name": "modify_invalid_key", "path": M, "request": {"trigger_id": "123456", **order(apikey="wrong")}},
    {"name": "modify_bad_product", "path": M, "request": {"trigger_id": "123456", **order(product="MIS")}},
    {"name": "modify_oco_stoploss_not_below_target", "path": M, "request": {"trigger_id": "123456", **oco(triggerprice_sl="900")}},
    # Cancel.
    {"name": "cancel", "path": C, "request": {"strategy": "t", "trigger_id": "123456"}, "broker": {DELETE: DELETED}},
    {"name": "cancel_broker_refuses", "path": C, "request": {"strategy": "t", "trigger_id": "123456"}, "broker": {DELETE: {"status": 404, "body": {"status": "error", "message": "Trigger not found.", "error_type": "InputException"}}}},
    {"name": "cancel_broker_error_in_an_ok_answer", "path": C, "request": {"strategy": "t", "trigger_id": "123456"}, "broker": {DELETE: {"body": {"status": "error", "message": "Trigger could not be cancelled."}}}},
    {"name": "cancel_missing_trigger_id", "path": C, "request": {"strategy": "t"}},
    {"name": "cancel_missing_strategy", "path": C, "request": {"trigger_id": "123456"}},
    {"name": "cancel_invalid_key", "path": C, "request": {"strategy": "t", "trigger_id": "123456", "apikey": "wrong"}},
    # Book.
    {"name": "book_active_only", "path": B, "request": {}, "broker": {"GET /gtt/triggers": BOOK}},
    {"name": "book_all_active_first", "path": B, "request": {"status": "all"}, "broker": {"GET /gtt/triggers": BOOK}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {"GET /gtt/triggers": EMPTY_BOOK}},
    {"name": "book_bad_status", "path": B, "request": {"status": "everything"}},
    {"name": "book_invalid_key", "path": B, "request": {"apikey": "wrong"}},
    {"name": "book_broker_refuses", "path": B, "request": {}, "broker": {"GET /gtt/triggers": {"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}}},
]
