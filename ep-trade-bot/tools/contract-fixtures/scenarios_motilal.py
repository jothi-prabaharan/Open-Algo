"""Motilal Oswal: orders, books, positions, holdings and funds. Every call is a POST and answers {"status": "SUCCESS"|"FAILURE", "message", "errorcode", ...}. The session is "AuthToken:::accesstoken" and the common headers carry ApiKey,
vendorinfo and the optional apisecretkey. A quantity goes out in lots (quantityinlot); a derivative with no lot size in the master contract is refused. Prices of the trade book are scaled by 10**precision; funds are read from the margin
detail report by row number (srno)."""

AUTH_TOKEN = "motilal-auth-token:::motilal-access-token"
ENV = {"BROKER_API_KEY": "motilal-api-key", "BROKER_API_SECRET": "motilal-api-secret", "BROKER_VENDOR_CODE": "VENDOR1"}
SEED_API_KEY_ROW = True
READ_POSTS = ["/rest/book/v5/getorderbook", "/rest/book/v4/gettradebook", "/rest/book/v4/getposition", "/rest/book/v5/getorderdetailbyuniqueorderid", "/rest/report/v3/getdpholding", "/rest/report/v3/getreportmargindetail",
              "/rest/report/v3/getltpdata"]
CLIENT_ID = "VENDOR1"

PLACE_URL, MODIFY_URL, CANCEL_URL = "POST /rest/trans/v2/placeorder", "POST /rest/trans/v5/modifyorder", "POST /rest/trans/v2/cancelorder"
OB, TB, POS, DETAIL, HOLD, MARGIN = ("POST /rest/book/v5/getorderbook", "POST /rest/book/v4/gettradebook", "POST /rest/book/v4/getposition", "POST /rest/book/v5/getorderdetailbyuniqueorderid",
                                     "POST /rest/report/v3/getdpholding", "POST /rest/report/v3/getreportmargindetail")


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN EQ", "NSE", "NSE", 3045, "STATE BANK OF INDIA"),
    _sym("INFY", "INFY EQ", "NSE", "NSE", 1594, "INFOSYS"),
    _sym("TATAMOTORS", "TATAMOTORS EQ", "BSE", "BSE", 500570, "TATA MOTORS"),
    _sym("NIFTY25NOV26FUT", "NIFTY 25-Nov-2026", "NFO", "NSEFO", 49500, "NIFTY", 75, "FUT", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY 25-Nov-2026 CE 24000", "NFO", "NSEFO", 49599, "NIFTY", 75, "CE", "25-NOV-26", 24000.0, 0.05),
    _sym("SENSEX26NOVFUT", "SENSEX 26-Nov-2026", "BFO", "BSEFO", 60001, "SENSEX", 20, "FUT", "26-NOV-26", -1.0, 0.1),
    _sym("GOLDM25NOV26FUT", "GOLDM 25-Nov-2026", "MCX", "MCX", 80001, "GOLDM", 10, "FUT", "25-NOV-26", -1.0, 1.0),
    _sym("USDINR25NOV26FUT", "USDINR 25-Nov-2026", "CDS", "NSECD", 90001, "USDINR", 1000, "FUT", "25-NOV-26", -1.0, 0.0025),
    _sym("NOLOTFUT", "NOLOT 25-Nov-2026", "NFO", "NSEFO", 49777, "NOLOT", 0, "FUT", "25-NOV-26", -1.0, 0.05),
    _sym("NOLOTEQ", "NOLOTEQ EQ", "NSE", "NSE", 3999, "NO LOT EQ", 0),
]

PLACE = {"strategy": "My Strategy_v2", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


def ok(**extra):
    return {"body": {"status": "SUCCESS", "message": "Success", "errorcode": "", **extra}}


def fail(status=200, message="Bad request", code="MO1000"):
    return {"status": status, "body": {"status": "FAILURE", "message": message, "errorcode": code}}


NOT_JSON = {"status": 200, "body": "<html>"}
OK_ORDER = ok(uniqueorderid="2611030001234567")


def place(name, request, reply=None):
    return req(name, "/api/v1/placeorder", request, {PLACE_URL: reply or OK_ORDER})


LTP = "POST /rest/report/v3/getltpdata"


def quote_data(ltp, **extra):
    return ok(data={"ltp": ltp, "open": ltp - 100, "high": ltp + 200, "low": ltp - 300, "close": ltp - 50, "bid": ltp - 5, "ask": ltp + 5, "volume": 123456, **extra})


def market(name, request, reply):
    return req(name, "/api/v1/placeorder", request, {PLACE_URL: OK_ORDER, LTP: reply})


def order(oid, symbol, token, side, qty, otype="Limit", product="VALUEPLUS", exchange="NSE", status="Confirm", price=0, trigger=0, average=0, **extra):
    o = {"uniqueorderid": oid, "symbol": symbol, "symboltoken": token, "exchange": exchange, "buyorsell": side, "ordertype": otype, "producttype": product, "orderstatus": status, "orderqty": qty, "price": price,
         "triggerprice": trigger, "averageprice": average, "qtytradedtoday": 0, "lastmodifiedtime": "03-Nov-2026 10:15:31", "entrydatetime": "03-Nov-2026 10:15:30", "recordinserttime": "03-Nov-2026 10:15:30"}
    o.update(extra)
    return o


ORDERS = [order("2611030000001", "SBIN", 3045, "BUY", 10, price=800.5), order("2611030000002", "INFY", 1594, "SELL", 5, "Market", "DELIVERY", status="Traded", average=1499.95),
          order("2611030000003", "TATAMOTORS", 500570, "BUY", 2, "Limit", "DELIVERY", "BSE", "Cancel", price=489), order("2611030000004", "SBIN", 3045, "SELL", 1, "Stoploss", status="Sent", price=790, trigger=791.25),
          order("2611030000005", "SBIN", 3045, "BUY", 3, "Stoploss", status="Rejected", trigger=0), order("2611030000006", "SBIN", 3045, "BUY", 6, status="Partial", price=799),
          order("2611030000007", "NIFTY", 49500, "BUY", 1, "Limit", "NORMAL", "NSEFO", "Confirm", price=24005, lastmodifiedtime="0"),
          order("2611030000008", "SBIN", 3045, "BUY", 1, "Limit", status="Error"), order("2611030000009", "SBIN", 3045, "BUY", 1, "Limit", status="Weird"),
          order("2611030000010", "UNLISTED", 9999, "BUY", 1, "Limit", "BTST", status="Unknown", price=10)]
BOOK = {OB: ok(data=ORDERS)}
WORKING = {OB: ok(data=[ORDERS[0], ORDERS[3], ORDERS[5], ORDERS[1], ORDERS[6]])}
DETAIL_ROW = {"uniqueorderid": "2611030000001", "lastmodifiedtime": "03-Nov-2026 10:15:31", "qtytradedtoday": 0, "ordertype": "C", "booktype": "Limit"}
CHANGE = {"strategy": "t", "orderid": "2611030000001", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "product": "MIS", "pricetype": "LIMIT", "price": "801.25", "quantity": "12", "trigger_price": "0", "disclosed_quantity": "0"}
OK_MODIFY = ok()
OK_CANCEL = ok()


def mod(name, request, reply, detail=None, book=None):
    return req(name, "/api/v1/modifyorder", request, {DETAIL: detail or ok(data=[DETAIL_ROW]), **(book or {}), MODIFY_URL: reply})


def cancel(name, oid, reply, book=None):
    return req(name, "/api/v1/cancelorder", {"strategy": "t", "orderid": oid}, {**(book or {}), CANCEL_URL: reply})


def trade(oid, symbol, token, side, qty, price, value, exchange="NSE", product="DELIVERY", **extra):
    t = {"uniqueorderid": oid, "symbol": symbol, "symboltoken": token, "exchange": exchange, "buyorsell": side, "producttype": product, "tradeqty": qty, "tradeprice": price, "tradevalue": value,
         "tradetime": "03-Nov-2026 10:21:12", "precision": 2}
    t.update(extra)
    return t


TRADES = [trade("2611030000002", "INFY", 1594, "SELL", 5, 149995, 749975), trade("2611030000007", "NIFTY", 49500, "BUY", 75, 2400050, 180003750, "NSEFO", "NORMAL"),
          trade("2611030000011", "SBIN", 3045, "BUY", 10, 80025, 800250, product="VALUEPLUS", precision=0), trade("2611030000012", "SBIN", 3045, "SELL", 10, 80100, 801000, product="VALUEPLUS", precision=None),
          trade("2611030000013", "SBIN", 3045, "BUY", 2, 805, 1610, precision=0), trade("2611030000014", "SBIN", 3045, "BUY", 1, 8050000, 8050000, precision=4),
          trade("2611030000015", "UNLISTED", 9999, "BUY", 1, 1000, 1000, product="SELLFROMDP", precision=12)]
for _row in TRADES:
    if _row["precision"] is None:
        del _row["precision"]


def position(symbol, token, exchange, product, buy_qty, sell_qty, buy_amount, sell_amount, ltp=0.0, mtm=0.0, booked=0.0, **extra):
    p = {"symbol": symbol, "symboltoken": token, "exchange": exchange, "productname": product, "buyquantity": buy_qty, "sellquantity": sell_qty, "buyamount": buy_amount, "sellamount": sell_amount, "LTP": ltp,
         "marktomarket": mtm, "bookedprofitloss": booked}
    p.update(extra)
    return p


POSITIONS = [position("SBIN", 3045, "NSE", "VALUEPLUS", 12, 2, 9600.0, 1602.0, 805.25, 52.5, 12.5), position("INFY", 1594, "NSE", "DELIVERY", 5, 5, 7500.0, 7510.0, 1500.0, 0.0, 10.0),
             position("TATAMOTORS", 500570, "BSE", "DELIVERY", 0, 4, 0.0, 2000.0, 498.1, 7.6, 0.0), position("NIFTY", 49500, "NSEFO", "NORMAL", 0, 75, 0.0, 1800000.0, 24010.5, -787.5, 0.0),
             position("SENSEX", 60001, "BSEFO", "NORMAL", 20, 0, 1600000.0, 0.0, 80100, 2000, 0)]
POS_OK = {POS: ok(data=POSITIONS)}
POS_CYCLE = {POS: [ok(data=POSITIONS)] * 8}
FLAT = {POS: [ok(data=[position("SBIN", 3045, "NSE", "VALUEPLUS", 5, 5, 4000.0, 4010.0)])] * 8}
NO_POS = {POS: [ok(data=None)] * 8}


def holding(name, qty, avg, nse=0, bse=0, **extra):
    h = {"scripname": name, "dpquantity": qty, "buyavgprice": avg, "nsesymboltoken": nse, "bsescripcode": bse}
    h.update(extra)
    return h


HOLDINGS = {HOLD: ok(data=[holding("SBIN EQ", 10, 700.0, 3045), holding("INFY EQ", 5, 1500.0, 1594, LTP=1499.5), holding("TATAMOTORS EQ", 3, 90.0, 0, 500570, LTP=95.5), holding("UNLISTED EQ", 2, 10.0, 9999),
                           holding("NOTOKEN EQ", 1, 5.5), holding("ZERO EQ", 4, 0, 3045, LTP=100)])}


def row(srno, amount, particulars="x"):
    return {"srno": srno, "particulars": particulars, "amount": amount}


MARGIN_ROWS = [row(100, 49833896.66), row(102, 49833896.66), row(201, 50000000), row(220, 474919.06), row(300, 622432.38), row(301, 674.75), row(321, 600000.0), row(400, -18590), row(402, 100.5),
               row(422, 20.25), row(403, 50.0), row(423, 5.25), row(600, -18590), row(700, 12)]


def funds(name, rows, **extra):
    return req(name, "/api/v1/funds", {}, {MARGIN: ok(data=rows)}, **extra)


def smart(name, size, broker, qty="0", **extra):
    return req(name, "/api/v1/placesmartorder", {**PLACE, "quantity": qty, "position_size": size, **extra}, broker)


SCENARIOS = [
    place("placeorder_limit_mis", PLACE),
    place("placeorder_cnc", {**PLACE, "product": "CNC"}),
    place("placeorder_nrml_on_cash", {**PLACE, "product": "NRML"}),
    place("placeorder_future_is_in_lots", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "150", "product": "NRML", "price": "24005"}),
    place("placeorder_mis_on_a_future_is_normal", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "price": "24005"}),
    place("placeorder_cnc_on_a_future_is_normal", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "CNC", "price": "24005"}),
    place("placeorder_an_option", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "price": "100.5"}),
    place("placeorder_bse_equity", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "price": "500"}),
    place("placeorder_bse_future", {**PLACE, "symbol": "SENSEX26NOVFUT", "exchange": "BFO", "quantity": "40", "product": "NRML", "price": "80000"}),
    place("placeorder_mcx", {**PLACE, "symbol": "GOLDM25NOV26FUT", "exchange": "MCX", "quantity": "20", "product": "NRML", "price": "75000"}),
    place("placeorder_currency", {**PLACE, "symbol": "USDINR25NOV26FUT", "exchange": "CDS", "quantity": "2000", "product": "NRML", "price": "83.5"}),
    place("placeorder_quantity_not_a_multiple_of_the_lot", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "80", "price": "24005"}),
    place("placeorder_derivative_with_no_lot_size_is_refused", {**PLACE, "symbol": "NOLOTFUT", "exchange": "NFO", "quantity": "75", "price": "24005"}),
    place("placeorder_cash_with_no_lot_size_uses_one", {**PLACE, "symbol": "NOLOTEQ", "quantity": "7"}),
    place("placeorder_stop_limit", {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}),
    place("placeorder_stop_market_becomes_stop_limit", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_sell_becomes_stop_limit", {**PLACE, "action": "SELL", "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}),
    place("placeorder_stop_market_option_uses_option_slab", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "pricetype": "SL-M", "price": "0", "trigger_price": "8.0"}),
    place("placeorder_stop_market_with_no_trigger", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "0"}),
    place("placeorder_market_goes_as_market_when_no_price_is_known", {**PLACE, "pricetype": "MARKET", "price": "0"}),
    market("placeorder_market_is_protected_buy", {**PLACE, "pricetype": "MARKET", "price": "0"}, quote_data(80525)),
    market("placeorder_market_is_protected_sell", {**PLACE, "action": "SELL", "pricetype": "MARKET", "price": "0"}, quote_data(80525)),
    market("placeorder_market_cheap_share_uses_the_wider_slab", {**PLACE, "pricetype": "MARKET", "price": "0"}, quote_data(5025)),
    market("placeorder_market_mid_priced_share", {**PLACE, "pricetype": "MARKET", "price": "0"}, quote_data(25025)),
    market("placeorder_market_option_uses_the_option_slab", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "pricetype": "MARKET", "price": "0"}, quote_data(15050)),
    market("placeorder_market_on_a_future", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML", "pricetype": "MARKET", "price": "0"}, quote_data(2401050)),
    market("placeorder_market_with_no_price_goes_as_market", {**PLACE, "pricetype": "MARKET", "price": "0"}, quote_data(0, open=0, high=0, low=0, close=0, bid=0, ask=0)),
    market("placeorder_market_quote_refused_goes_as_market", {**PLACE, "pricetype": "MARKET", "price": "0"}, fail(200, "Token expired", "MO8002")),
    market("placeorder_market_quote_empty_goes_as_market", {**PLACE, "pricetype": "MARKET", "price": "0"}, ok(data={})),
    market("placeorder_market_on_bse", {**PLACE, "symbol": "TATAMOTORS", "exchange": "BSE", "product": "CNC", "pricetype": "MARKET", "price": "0"}, quote_data(49810)),
    place("placeorder_sell", {**PLACE, "action": "SELL"}),
    place("placeorder_disclosed_quantity", {**PLACE, "disclosed_quantity": "3"}),
    place("placeorder_price_is_not_rounded", {**PLACE, "price": "800.555"}),
    place("placeorder_unknown_symbol", {**PLACE, "symbol": "NOPE"}),
    place("placeorder_quantity_zero", {**PLACE, "quantity": "0"}),
    place("placeorder_price_type_unknown", {**PLACE, "pricetype": "ICEBERG"}),
    place("placeorder_product_unknown", {**PLACE, "product": "BO"}),
    place("placeorder_refused_with_a_message_and_code", PLACE, fail(200, "Insufficient funds", "MO1234")),
    place("placeorder_refused_with_no_code", PLACE, {"body": {"status": "FAILURE", "message": "Bad quantity"}}),
    place("placeorder_refused_with_no_message", PLACE, {"body": {"status": "FAILURE"}}),
    place("placeorder_http_error_with_a_failure_body", PLACE, fail(500, "Down", "MO5000")),
    place("placeorder_http_error_with_no_body", PLACE, {"status": 500, "body": ""}),
    place("placeorder_success_with_no_order_id", PLACE, ok()),
    place("placeorder_not_json", PLACE, NOT_JSON),
    place("placeorder_status_in_lower_case", PLACE, {"body": {"status": "success", "uniqueorderid": "2611030009999"}}),
    mod("modifyorder_limit", CHANGE, OK_MODIFY),
    mod("modifyorder_stop_limit", {**CHANGE, "pricetype": "SL", "price": "798", "trigger_price": "799"}, OK_MODIFY),
    mod("modifyorder_stop_market_stays_a_stop", {**CHANGE, "pricetype": "SL-M", "price": "0", "trigger_price": "799"}, OK_MODIFY),
    mod("modifyorder_market", {**CHANGE, "pricetype": "MARKET", "price": "0"}, OK_MODIFY),
    mod("modifyorder_future_in_lots", {**CHANGE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "150", "price": "24010"}, OK_MODIFY),
    mod("modifyorder_quantity_not_a_multiple_of_the_lot", {**CHANGE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "100", "price": "24010"}, OK_MODIFY),
    mod("modifyorder_derivative_with_no_lot_size", {**CHANGE, "symbol": "NOLOTFUT", "exchange": "NFO", "quantity": "75"}, OK_MODIFY),
    mod("modifyorder_disclosed_quantity", {**CHANGE, "disclosed_quantity": "2"}, OK_MODIFY),
    mod("modifyorder_never_modified_uses_insert_time", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "lastmodifiedtime": "0", "recordinserttime": "03-Nov-2026 10:15:30"}])),
    mod("modifyorder_never_modified_and_no_insert_time_uses_entry_time", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "lastmodifiedtime": "0", "recordinserttime": "0", "entrydatetime": "03-Nov-2026 10:15:29"}])),
    mod("modifyorder_no_time_at_all", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "lastmodifiedtime": "0", "recordinserttime": "", "entrydatetime": None}])),
    mod("modifyorder_traded_quantity_is_sent", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "qtytradedtoday": 4}])),
    mod("modifyorder_traded_quantity_is_not_a_number", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "qtytradedtoday": "abc"}])),
    mod("modifyorder_detail_row_with_no_id_is_taken", CHANGE, OK_MODIFY, ok(data=[{k: v for k, v in DETAIL_ROW.items() if k != "uniqueorderid"}])),
    mod("modifyorder_detail_for_another_order_falls_back_to_the_book", CHANGE, OK_MODIFY, ok(data=[{**DETAIL_ROW, "uniqueorderid": "OTHER"}]), BOOK),
    mod("modifyorder_detail_refused_falls_back_to_the_book", CHANGE, OK_MODIFY, fail(200, "No such order", "MO2000"), BOOK),
    mod("modifyorder_not_in_the_book", CHANGE, OK_MODIFY, fail(200, "No such order", "MO2000"), {OB: ok(data=[ORDERS[1]])}),
    mod("modifyorder_book_empty", CHANGE, OK_MODIFY, fail(200, "No such order", "MO2000"), {OB: ok(data=[])}),
    mod("modifyorder_book_refused", CHANGE, OK_MODIFY, fail(200, "No such order", "MO2000"), {OB: fail(200, "Token expired", "MO8002")}),
    mod("modifyorder_refused_with_a_message_and_code", CHANGE, fail(200, "Order is not open", "MO1089")),
    mod("modifyorder_refused_with_no_message", CHANGE, {"body": {"status": "FAILURE"}}),
    mod("modifyorder_http_error", CHANGE, {"status": 500, "body": ""}),
    mod("modifyorder_not_json", CHANGE, NOT_JSON),
    cancel("cancelorder_ok", "2611030000001", OK_CANCEL),
    cancel("cancelorder_refused_with_a_message_and_code", "2611030000001", fail(200, "Order already complete", "MO1100")),
    cancel("cancelorder_refused_with_no_message", "2611030000001", {"body": {"status": "FAILURE"}}),
    cancel("cancelorder_http_error_with_a_failure_body", "2611030000001", fail(500, "Down", "MO5000")),
    cancel("cancelorder_http_error_with_no_body", "2611030000001", {"status": 500, "body": ""}),
    cancel("cancelorder_not_json", "2611030000001", NOT_JSON),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {**WORKING, CANCEL_URL: OK_CANCEL}),
    req("cancelallorder_one_refused", "/api/v1/cancelallorder", {"strategy": "t"}, {**WORKING, CANCEL_URL: [OK_CANCEL, fail(200, "Already done", "MO1100"), OK_CANCEL, OK_CANCEL, OK_CANCEL]}),
    req("cancelallorder_nothing_working", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ok(data=[ORDERS[1], ORDERS[2]])}),
    req("cancelallorder_empty_book", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: ok(data=[])}),
    req("cancelallorder_book_failed", "/api/v1/cancelallorder", {"strategy": "t"}, {OB: fail(200, "Token expired", "MO8002")}),
    req("orderbook", "/api/v1/orderbook", {}, BOOK),
    req("orderbook_empty", "/api/v1/orderbook", {}, {OB: ok(data=[])}),
    req("orderbook_null_data", "/api/v1/orderbook", {}, {OB: ok(data=None)}),
    req("orderbook_scaled_by_precision", "/api/v1/orderbook", {}, {OB: ok(data=[order("2611030000020", "SBIN", 3045, "BUY", 10, "Limit", status="Traded", price=80050, average=80025, triggerprice=0, precision=2)])}),
    req("orderbook_precision_out_of_range_reads_as_two", "/api/v1/orderbook", {}, {OB: ok(data=[order("2611030000021", "SBIN", 3045, "BUY", 10, "Limit", price=80050, precision=9), order("2611030000022", "SBIN", 3045, "BUY", 10, "Limit", price=80050, precision=-1)])}),
    req("orderbook_precision_zero_is_not_scaled", "/api/v1/orderbook", {}, {OB: ok(data=[order("2611030000023", "SBIN", 3045, "BUY", 10, "Limit", price=800, precision=0)])}),
    req("orderbook_executed_shows_the_average_price", "/api/v1/orderbook", {}, {OB: ok(data=[order("2611030000024", "SBIN", 3045, "BUY", 10, "Limit", status="Traded", price=800, average=799.5)])}),
    req("orderbook_prices_that_are_not_numbers", "/api/v1/orderbook", {}, {OB: ok(data=[order("2611030000025", "SBIN", 3045, "BUY", 10, "Limit", price="abc", trigger="x", average=None, orderqty="7")])}),
    req("orderbook_failed_with_a_message", "/api/v1/orderbook", {}, {OB: fail(200, "Token expired", "MO8002")}),
    req("orderbook_failed_with_no_message", "/api/v1/orderbook", {}, {OB: {"body": {"status": "FAILURE"}}}),
    req("orderbook_http_error", "/api/v1/orderbook", {}, {OB: {"status": 500, "body": ""}}),
    req("orderbook_not_json", "/api/v1/orderbook", {}, {OB: NOT_JSON}),
    req("tradebook", "/api/v1/tradebook", {}, {TB: ok(data=TRADES)}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {TB: ok(data=[])}),
    req("tradebook_null_data", "/api/v1/tradebook", {}, {TB: ok(data=None)}),
    req("tradebook_failed", "/api/v1/tradebook", {}, {TB: fail(200, "Token expired", "MO8002")}),
    req("tradebook_http_error", "/api/v1/tradebook", {}, {TB: {"status": 500, "body": ""}}),
    req("tradebook_not_json", "/api/v1/tradebook", {}, {TB: NOT_JSON}),
    req("positionbook", "/api/v1/positionbook", {}, POS_OK),
    req("positionbook_flat", "/api/v1/positionbook", {}, FLAT),
    req("positionbook_null_data", "/api/v1/positionbook", {}, NO_POS),
    req("positionbook_values_that_are_not_numbers", "/api/v1/positionbook", {}, {POS: ok(data=[position("SBIN", 3045, "NSE", "VALUEPLUS", "7", "2", "abc", None, "x", "1.5", None)])}),
    req("positionbook_failed", "/api/v1/positionbook", {}, {POS: fail(200, "Token expired", "MO8002")}),
    req("positionbook_not_json", "/api/v1/positionbook", {}, {POS: NOT_JSON}),
    req("holdings", "/api/v1/holdings", {}, HOLDINGS),
    req("holdings_empty", "/api/v1/holdings", {}, {HOLD: ok(data=[])}),
    req("holdings_null_data", "/api/v1/holdings", {}, {HOLD: ok(data=None)}),
    req("holdings_failed", "/api/v1/holdings", {}, {HOLD: fail(200, "Token expired", "MO8002")}),
    req("holdings_not_json", "/api/v1/holdings", {}, {HOLD: NOT_JSON}),
    req("holdings_values_that_are_not_numbers", "/api/v1/holdings", {}, {HOLD: ok(data=[holding("SBIN EQ", "abc", "x", 3045, LTP="y")])}),
    funds("funds", MARGIN_ROWS),
    funds("funds_cash_row_missing_is_derived_from_the_net_row", [r for r in MARGIN_ROWS if r["srno"] != 201]),
    funds("funds_cash_row_missing_and_combined_mtm_missing", [r for r in MARGIN_ROWS if r["srno"] not in (201, 600)]),
    funds("funds_cash_and_net_rows_missing", [r for r in MARGIN_ROWS if r["srno"] not in (201, 102)]),
    funds("funds_usage_total_missing_is_summed_from_the_segments", [r for r in MARGIN_ROWS if r["srno"] != 300]),
    funds("funds_usage_total_and_segments_missing", [r for r in MARGIN_ROWS if r["srno"] not in (300, 301, 321)]),
    funds("funds_no_split_rows_uses_the_combined_mtm", [r for r in MARGIN_ROWS if r["srno"] not in (402, 403, 422, 423)]),
    funds("funds_no_split_rows_and_no_600_uses_the_400_row", [r for r in MARGIN_ROWS if r["srno"] not in (402, 403, 422, 423, 600)]),
    funds("funds_no_split_rows_and_no_mtm", [r for r in MARGIN_ROWS if r["srno"] not in (402, 403, 422, 423, 600, 400)]),
    funds("funds_only_realised_rows", [r for r in MARGIN_ROWS if r["srno"] not in (402, 422)]),
    funds("funds_a_repeated_row_is_taken_once_but_a_split_row_is_summed", MARGIN_ROWS + [row(201, 1), row(402, 1000)]),
    funds("funds_amounts_that_are_text", [row(201, "1500.5"), row(220, "abc"), row(300, None)]),
    funds("funds_a_row_number_that_is_text_is_not_matched", [row("201", 99), row(220, 5)]),
    funds("funds_a_row_that_is_not_an_object", MARGIN_ROWS + ["x"]),
    funds("funds_empty_rows", []),
    req("funds_null_data", "/api/v1/funds", {}, {MARGIN: ok(data=None)}),
    req("funds_failed", "/api/v1/funds", {}, {MARGIN: fail(200, "Token expired", "MO8002")}),
    req("funds_http_error", "/api/v1/funds", {}, {MARGIN: {"status": 500, "body": ""}}),
    req("funds_not_json", "/api/v1/funds", {}, {MARGIN: NOT_JSON}),
    req("openposition_long", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, POS_CYCLE),
    req("openposition_short_in_a_future", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, POS_CYCLE),
    req("openposition_not_held_is_zero", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "CNC"}, POS_CYCLE),
    req("openposition_book_failed", "/api/v1/openposition", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, {POS: fail(200, "Token expired", "MO8002")}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, FLAT),
    req("closeposition_no_positions", "/api/v1/closeposition", {"strategy": "t"}, NO_POS),
    req("closeposition_book_failed", "/api/v1/closeposition", {"strategy": "t"}, {POS: fail(200, "Token expired", "MO8002")}),
    req("closeposition_an_order_refused", "/api/v1/closeposition", {"strategy": "t"}, {POS: ok(data=[POSITIONS[0]]), PLACE_URL: fail(200, "No margin", "MO1234")}),
    req("closeposition_an_unlisted_symbol", "/api/v1/closeposition", {"strategy": "t"}, {POS: ok(data=[position("UNLISTED", 9999, "NSE", "VALUEPLUS", 5, 0, 50.0, 0.0)]), PLACE_URL: OK_ORDER}),
    smart("placesmartorder_open_a_position", "5", {**FLAT, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_short_a_position", "-5", {**FLAT, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_reduce", "4", {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_increase", "15", {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_flip_from_long_to_short", "-3", {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_close_a_long", "0", {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_close_a_short", "0", {**POS_CYCLE, PLACE_URL: OK_ORDER}, symbol="TATAMOTORS", exchange="BSE", product="CNC"),
    smart("placesmartorder_fresh_order_when_flat", "0", {**FLAT, PLACE_URL: OK_ORDER}, qty="10"),
    smart("placesmartorder_nothing_to_do", "8", {**POS_CYCLE, PLACE_URL: OK_ORDER}),
    smart("placesmartorder_a_future_in_lots", "75", {**POS_CYCLE, PLACE_URL: OK_ORDER}, symbol="NIFTY25NOV26FUT", exchange="NFO", product="NRML", qty="75"),
    smart("placesmartorder_book_failed_is_refused", "5", {POS: fail(200, "Token expired", "MO8002"), PLACE_URL: OK_ORDER}),
]
