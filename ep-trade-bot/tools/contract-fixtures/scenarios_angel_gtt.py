"""Angel One GTT: rules under /rest/secure/angelbroking/gtt/v1. An OCO is two rules whose ids are joined by a dash."""

import scenarios_angel as a

BROKER = "angel"
AUTH_TOKEN = a.AUTH_TOKEN
ENV = a.ENV
READ_POSTS = ["/rest/secure/angelbroking/market/v1/quote/", "/rest/secure/angelbroking/gtt/v1/ruleDetails", "/rest/secure/angelbroking/gtt/v1/ruleList"]
SEED_API_KEY_ROW = True

SYMBOLS = [
    *a.SYMBOLS,
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY25NOV2624000CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "35100", "expiry": "25NOV2026", "strike": 24000.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
]

OK = a.OK
QUOTE = a.QUOTE
BASE = "POST /rest/secure/angelbroking/gtt/v1/"
CREATE, MODIFY, CANCEL, DETAILS, LIST = BASE + "createRule", BASE + "modifyRule", BASE + "cancelRule", BASE + "ruleDetails", BASE + "ruleList"
Q_SBIN = a.QUOTE_SBIN
Q_ZERO = {"body": {**OK, "data": {"fetched": [{**a.QUOTE_SBIN["body"]["data"]["fetched"][0], "ltp": 0}], "unfetched": []}}}


def rule(rid):
    return {"body": {**OK, "data": {"id": str(rid)}}}


REFUSED = {"body": {"status": False, "message": "Invalid Product Type", "errorcode": "AB9015", "data": None}}
NOT_FOUND = {"body": {"status": False, "message": "Invalid Rule ID", "errorcode": "AB9013", "data": None}}
NO_ID = {"body": {**OK, "data": {}}}
DETAILS_OK = {"body": {**OK, "data": {"id": "5001", "symboltoken": "3045", "exchange": "NSE", "tradingsymbol": "SBIN-EQ", "status": "NEW"}}}


def order(**extra):
    base = {"strategy": "t", "trigger_type": "SINGLE", "exchange": "NSE", "symbol": "SBIN", "action": "BUY", "product": "CNC", "quantity": "10", "pricetype": "LIMIT", "price": "790", "triggerprice_sl": "795"}
    base.update(extra)
    return base


def oco(**extra):
    base = {"trigger_type": "OCO", "action": "SELL", "price": "0", "triggerprice_sl": "780", "stoploss": "779", "triggerprice_tg": "830", "target": "829"}
    base.update(extra)
    return order(**base)


def row(rid, status="ACTIVE", symbol="SBIN-EQ", exchange="NSE", side="BUY", product="DELIVERY", qty="10", price="790", trigger="795"):
    return {"id": str(rid), "tradingsymbol": symbol, "symboltoken": "3045", "exchange": exchange, "transactiontype": side, "producttype": product, "price": price, "qty": qty,
            "triggerprice": trigger, "status": status, "createddate": "2026-11-03 09:15:00", "updateddate": "2026-11-03 09:16:00", "expirydate": "2027-11-03 09:15:00"}


def page(rows):
    return {"body": {**OK, "data": rows}}


P, M, C, B = "/api/v1/placegttorder", "/api/v1/modifygttorder", "/api/v1/cancelgttorder", "/api/v1/gttorderbook"
BOOK = page([row(1), row(2, "NEW", product="MARGIN"), row(3, "SENTTOEXCHANGE", side="SELL", product="CARRYFORWARD"), row(4, "CANCELLED"), row(5, "ACTIVE", symbol="UNLISTED-EQ"), row(6, "EXPIRED", product="INTRADAY")])
FULL_PAGE = page([row(100 + i) for i in range(10)])


def place(name, request, replies=None, quote=None):
    broker = {CREATE: rule(5001)}
    if quote is not None:
        broker[QUOTE] = quote
    broker.update(replies or {})
    return {"name": name, "path": P, "request": request, "broker": broker}


def modify(name, trigger_id, request, replies=None, quote=None):
    broker = {MODIFY: rule(5001)}
    if quote is not None:
        broker[QUOTE] = quote
    broker.update(replies or {})
    return {"name": name, "path": M, "request": {"trigger_id": trigger_id, **request}, "broker": broker}


SCENARIOS = [
    # Place.
    place("place_single_stoploss_trigger", order()),
    place("place_single_target_trigger", order(action="SELL", price="819", triggerprice_sl="0", triggerprice_tg="820")),
    place("place_single_both_triggers_stoploss_wins", order(triggerprice_tg="830")),
    place("place_single_nrml_sent_as_margin", order(product="NRML")),
    place("place_single_decimal_prices", order(price="790.55", triggerprice_sl="795.25")),
    place("place_single_market_buy_protected", order(pricetype="MARKET", price="0"), quote=Q_SBIN),
    place("place_single_market_sell_protected", order(pricetype="MARKET", action="SELL", price="0", triggerprice_sl="0", triggerprice_tg="820"), quote=Q_SBIN),
    place("place_single_market_without_last_price", order(pricetype="MARKET", price="0"), quote=Q_ZERO),
    place("place_oco_two_rules", oco(), {CREATE: [rule(5001), rule(5002)]}),
    place("place_oco_market_protected_around_each_trigger", oco(pricetype="MARKET"), {CREATE: [rule(5001), rule(5002)]}),
    place("place_oco_second_rule_refused_first_cancelled", oco(), {CREATE: [rule(5001), REFUSED], CANCEL: rule(5001)}),
    place("place_oco_first_rule_refused", oco(), {CREATE: REFUSED}),
    place("place_oco_no_rule_id_on_second_leg", oco(), {CREATE: [rule(5001), NO_ID], CANCEL: rule(5001)}),
    place("place_single_refused", order(), {CREATE: REFUSED}),
    place("place_single_no_rule_id", order(), {CREATE: NO_ID}),
    place("place_single_session_expired", order(), {CREATE: {"body": {"status": False, "message": "Invalid Token", "errorcode": "AB9005", "data": None}}}),
    place("place_single_unknown_symbol", order(symbol="NOPE")),
    # Place: refused before any broker call.
    {"name": "place_invalid_key", "path": P, "request": order(apikey="wrong")},
    {"name": "place_missing_fields", "path": P, "request": {"strategy": "t"}},
    {"name": "place_product_mis_refused", "path": P, "request": order(product="MIS")},
    {"name": "place_oco_stoploss_not_below_target", "path": P, "request": oco(triggerprice_sl="830", triggerprice_tg="830")},
    {"name": "place_single_without_trigger", "path": P, "request": order(triggerprice_sl="0")},
    # Modify.
    modify("modify_single", "5001", order()),
    modify("modify_single_market_protected", "5001", order(pricetype="MARKET", price="0"), quote=Q_SBIN),
    modify("modify_oco_two_rules", "5001-5002", oco(), {MODIFY: [rule(5001), rule(5002)]}),
    modify("modify_oco_market_protected", "5001-5002", oco(pricetype="MARKET"), {MODIFY: [rule(5001), rule(5002)]}),
    modify("modify_oco_second_rule_refused", "5001-5002", oco(), {MODIFY: [rule(5001), REFUSED]}),
    modify("modify_oco_first_rule_refused", "5001-5002", oco(), {MODIFY: REFUSED}),
    modify("modify_single_unknown_rule", "9999", order(), {MODIFY: NOT_FOUND}),
    modify("modify_single_with_oco_id", "5001-5002", order()),
    modify("modify_oco_with_single_id", "5001", oco()),
    modify("modify_market_without_last_price", "5001", order(pricetype="MARKET", price="0"), quote=Q_ZERO),
    {"name": "modify_missing_trigger_id", "path": M, "request": order()},
    {"name": "modify_invalid_key", "path": M, "request": {"trigger_id": "5001", **order(apikey="wrong")}},
    # Cancel.
    {"name": "cancel_single", "path": C, "request": {"strategy": "t", "trigger_id": "5001"}, "broker": {DETAILS: DETAILS_OK, CANCEL: rule(5001)}},
    {"name": "cancel_oco_both_rules", "path": C, "request": {"strategy": "t", "trigger_id": "5001-5002"}, "broker": {DETAILS: DETAILS_OK, CANCEL: [rule(5001), rule(5002)]}},
    {"name": "cancel_oco_second_rule_refused", "path": C, "request": {"strategy": "t", "trigger_id": "5001-5002"}, "broker": {DETAILS: DETAILS_OK, CANCEL: [rule(5001), REFUSED]}},
    {"name": "cancel_oco_second_rule_unknown", "path": C, "request": {"strategy": "t", "trigger_id": "5001-5002"}, "broker": {DETAILS: [DETAILS_OK, NOT_FOUND], CANCEL: rule(5001)}},
    {"name": "cancel_unknown_rule", "path": C, "request": {"strategy": "t", "trigger_id": "9999"}, "broker": {DETAILS: NOT_FOUND}},
    {"name": "cancel_refused", "path": C, "request": {"strategy": "t", "trigger_id": "5001"}, "broker": {DETAILS: DETAILS_OK, CANCEL: REFUSED}},
    {"name": "cancel_missing_trigger_id", "path": C, "request": {"strategy": "t"}},
    {"name": "cancel_invalid_key", "path": C, "request": {"strategy": "t", "trigger_id": "5001", "apikey": "wrong"}},
    # Book.
    {"name": "book_active_only", "path": B, "request": {}, "broker": {LIST: BOOK}},
    {"name": "book_status_all_is_still_active_only", "path": B, "request": {"status": "all"}, "broker": {LIST: BOOK}},
    {"name": "book_empty", "path": B, "request": {}, "broker": {LIST: page([])}},
    {"name": "book_walks_pages_until_a_short_one", "path": B, "request": {}, "broker": {LIST: [FULL_PAGE, page([row(200)])]}},
    {"name": "book_single_object_page", "path": B, "request": {}, "broker": {LIST: {"body": {**OK, "data": row(7)}}}},
    {"name": "book_second_page_fails_keeps_the_first", "path": B, "request": {}, "broker": {LIST: [FULL_PAGE, REFUSED]}},
    {"name": "book_first_page_fails", "path": B, "request": {}, "broker": {LIST: REFUSED}},
    {"name": "book_bad_status", "path": B, "request": {"status": "everything"}},
    {"name": "book_invalid_key", "path": B, "request": {"apikey": "wrong"}},
]
