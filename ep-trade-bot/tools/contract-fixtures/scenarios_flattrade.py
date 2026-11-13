"""Flattrade (Noren API) scenarios. Requests are jData forms; replies follow the Noren API documentation."""

import json

AUTH_TOKEN = "flattrade-access-token"
ENV = {"BROKER_API_KEY": "FA12345:::flattrade-api-key", "BROKER_API_SECRET": "flattrade-secret"}
SEED_API_KEY_ROW = True
BASE = "/PiConnectAPI"
# Reads are POSTs on the Noren API; they change nothing and are compared as reads.
READ_POSTS = [f"{BASE}/{n}" for n in ("OrderBook", "TradeBook", "PositionBook", "Holdings", "Limits", "GetQuotes")]


def _sym(symbol, brsymbol, exchange, brexchange, token, name, lot=1, inst="EQ", expiry="", strike=-1.0, tick=0.05):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": expiry, "strike": strike, "lotsize": lot, "instrumenttype": inst, "tick_size": tick}


SYMBOLS = [
    _sym("SBIN", "SBIN-EQ", "NSE", "NSE", "3045", "STATE BANK OF INDIA"),
    _sym("M&M", "M&M-EQ", "NSE", "NSE", "2031", "MAHINDRA"),
    _sym("INFY", "INFY-EQ", "NSE", "NSE", "1594", "INFOSYS"),
    _sym("NIFTY25NOV26FUT", "NIFTY25NOV26F", "NFO", "NFO", "35001", "NIFTY", 75, "FUTIDX", "25-NOV-26", -1.0, 0.1),
    _sym("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", "NFO", "40001", "NIFTY", 75, "OPTIDX", "25-NOV-26", 24000.0),
    _sym("CRUDEOIL", "CRUDEOIL", "MCX", "MCX", "50001", "CRUDEOIL", 100, "FUTCOM", "", -1.0, 1.0),
]

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": "800.5", "trigger_price": "0", "disclosed_quantity": "0"}

ORDER_OK = {"body": {"stat": "Ok", "request_time": "10:20:30 03-11-2026", "norenordno": "26110300000123"}}
ORDER_REFUSED = {"body": {"stat": "Not_Ok", "request_time": "10:20:30 03-11-2026", "emsg": "Rejected : Insufficient funds"}}


def quote(token="3045", exch="NSE", ltp="805.45", tick="0.05", tsym="SBIN-EQ", bid="805.4", ask="805.5"):
    q = {"stat": "Ok", "request_time": "10:20:30 03-11-2026", "exch": exch, "tsym": tsym, "token": token, "lp": ltp, "o": "802.45", "h": "809.45", "l": "800.45", "c": "803.95",
         "v": "1234567", "ltq": "25", "bp1": bid, "sp1": ask, "oi": "1500", "ti": tick}
    for i in range(1, 6):
        q[f"bp{i}"] = str(round(float(bid) - 0.05 * (i - 1), 2))
        q[f"bq{i}"] = str(100 * i)
        q[f"sp{i}"] = str(round(float(ask) + 0.05 * (i - 1), 2))
        q[f"sq{i}"] = str(110 * i)
        q[f"bo{i}"] = str(i)
        q[f"so{i}"] = str(2 * i)
    return {"body": q}


def _order(no, exch, tsym, token, side, ptype, prd, qty, prc, trg, status, when, **extra):
    o = {"stat": "Ok", "norenordno": no, "uid": "FA12345", "actid": "FA12345", "exch": exch, "tsym": tsym, "qty": str(qty), "trantype": side, "prctyp": ptype, "ret": "DAY",
         "token": token, "prd": prd, "status": status, "norentm": when, "prc": str(prc), "trgprc": str(trg) if trg else None}
    if not trg:
        del o["trgprc"]
    o.update(extra)
    return o


ORDERS = {"body": [
    _order("1001", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 10, "800.50", 0, "OPEN", "10:20:01 03-11-2026"),
    _order("1002", "NSE", "INFY-EQ", "1594", "S", "MKT", "C", 5, "0.00", 0, "COMPLETE", "10:21:11 03-11-2026", avgprc="1499.95"),
    _order("1003", "NFO", "NIFTY25NOV26F", "35001", "B", "SL-LMT", "M", 75, "24010.00", "24000.00", "TRIGGER_PENDING", "10:22:00 03-11-2026"),
    _order("1004", "NSE", "SBIN-EQ", "3045", "S", "SL-MKT", "I", 1, "0.00", "790.50", "REJECTED", "10:23:00 03-11-2026", rejreason="RMS:Margin Exceeds"),
    _order("1005", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 2, "700.00", 0, "CANCELED", "10:24:00 03-11-2026"),
    _order("1006", "NSE", "UNLISTED-EQ", "99999", "B", "LMT", "M", 1, "10.00", 0, "PENDING", "10:25:00 03-11-2026"),
    _order("1007", "MCX", "CRUDEOIL", "50001", "B", "LMT", "M", 1, "6500.00", 0, "OPEN", "10:26:00 03-11-2026"),
    _order("1008", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 1, "700.00", 0, "Open Pending", "10:27:00 03-11-2026"),
    _order("1012", "NSE", "SBIN-EQ", "3045", "S", "LMT", "I", 1, "900.00", 0, "CANCELLED", "10:28:00 03-11-2026"),
    _order("1013", "NFO", "NIFTY25NOV26F", "35001", "S", "LMT", "C", 75, "24100.00", 0, "COMPLETE", "10:29:00 03-11-2026"),
]}
ORDERS_PRICES = {"body": [
    _order("2001", "NSE", "SBIN-EQ", "3045", "B", "MKT", "I", 10, "0.00", 0, "COMPLETE", "10:20:01 03-11-2026", instname="EQ", avgprc="805.45", fillshares="10"),
    _order("2002", "NSE", "SBIN-EQ", "3045", "B", "MKT", "I", 10, "0.00", 0, "OPEN", "10:20:02 03-11-2026", rprc="806.10", fillshares="4"),
    _order("2003", "NSE", "SBIN-EQ", "3045", "S", "SL-MKT", "I", 10, "0.00", "790.5", "OPEN", "10:20:03 03-11-2026", rprc="789.00"),
    _order("2004", "NSE", "SBIN-EQ", "3045", "B", "MKT", "I", 10, "0.00", 0, "OPEN", "10:20:04 03-11-2026", rprc="0"),
    _order("2005", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 10, "700.00", 0, "OPEN", "10:20:05 03-11-2026", instname="EQ", avgprc="0.00", rprc="1"),
    _order("2006", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 10, "700.00", 0, "OPEN", "10:20:06 03-11-2026", fillshares="12", avgprc="x"),
]}
LIMITED = {"body": {"stat": "Not_Ok", "emsg": "Invalid Input : Order Recieved 11 in a current second exceeds Limit 10 for user"}}
NEGATIVE_REPORTED = {"body": [
    _order("2007", "NSE", "SBIN-EQ", "3045", "B", "SL-MKT", "I", 10, "0.00", "790.5", "OPEN", "10:20:07 03-11-2026", rprc="-1"),
    _order("2008", "NSE", "SBIN-EQ", "3045", "B", "SL-MKT", "I", 10, "0.00", "790.5", "OPEN", "10:20:08 03-11-2026", rprc="791.25"),
    _order("2009", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 10, "800.00", 0, "COMPLETE", "10:20:09 03-11-2026", instname="EQ", avgprc="805.45", fillshares="10"),
    _order("2010", "NSE", "SBIN-EQ", "3045", "B", "LMT", "I", 10, "800.00", 0, "OPEN", "10:20:10 03-11-2026", instname="EQ", avgprc="0.00", fillshares="0"),
]}
NO_ORDERS = {"body": {"stat": "Not_Ok", "request_time": "10:20:30 03-11-2026", "emsg": "Error Occurred : 5 \"no data\""}}
TRADES_ROUNDING = {"body": [
    {"stat": "Ok", "norenordno": "1011", "uid": "FA12345", "actid": "FA12345", "exch": "NSE", "prctyp": "LMT", "ret": "DAY", "prd": "I", "flid": "9004", "fltm": "10:28:01 03-11-2026",
     "trantype": "B", "tsym": "SBIN-EQ", "qty": "3", "token": "3045", "fillshares": "3", "flqty": "3", "prc": "10.00", "flprc": "10.005", "norentm": "10:28:00 03-11-2026", "avgprc": "10.005"},
]}
TRADES = {"body": [
    {"stat": "Ok", "norenordno": "1002", "uid": "FA12345", "actid": "FA12345", "exch": "NSE", "prctyp": "MKT", "ret": "DAY", "prd": "C", "flid": "9001", "fltm": "10:21:12 03-11-2026",
     "trantype": "S", "tsym": "INFY-EQ", "qty": "5", "token": "1594", "fillshares": "5", "flqty": "5", "prc": "0.00", "flprc": "1499.95", "norentm": "10:21:11 03-11-2026", "avgprc": "1499.95"},
    {"stat": "Ok", "norenordno": "1009", "uid": "FA12345", "actid": "FA12345", "exch": "NFO", "prctyp": "LMT", "ret": "DAY", "prd": "M", "flid": "9002", "fltm": "10:25:01 03-11-2026",
     "trantype": "B", "tsym": "NIFTY25NOV26F", "qty": "75", "token": "35001", "fillshares": "75", "flqty": "75", "prc": "24005.55", "flprc": "24005.555", "norentm": "10:25:00 03-11-2026", "avgprc": "24005.555"},
    {"stat": "Ok", "norenordno": "1010", "uid": "FA12345", "actid": "FA12345", "exch": "NSE", "prctyp": "LMT", "ret": "DAY", "prd": "I", "flid": "9003", "fltm": "10:27:01 03-11-2026",
     "trantype": "B", "tsym": "UNKNOWN-EQ", "qty": "3", "token": "88888", "fillshares": "3", "flqty": "3", "prc": "10.00", "flprc": "10.00", "norentm": "10:27:00 03-11-2026", "avgprc": "10.00"},
]}


def _pos(exch, tsym, token, prd, net, avg, lp, rpnl="0.00", urmtom="0.00", **extra):
    p = {"stat": "Ok", "uid": "FA12345", "actid": "FA12345", "exch": exch, "tsym": tsym, "token": token, "prd": prd, "netqty": str(net), "netavgprc": str(avg),
         "lp": str(lp), "rpnl": rpnl, "urmtom": urmtom, "daybuyavgprc": "0.00", "totbuyavgprc": "0.00"}
    p.update(extra)
    return p


POSITIONS = {"body": [
    _pos("NSE", "SBIN-EQ", "3045", "I", 10, "801.1234", "805.45", "0.00", "43.33"),
    _pos("NFO", "NIFTY25NOV26F", "35001", "M", -75, "24005.50", "24000.00", "5.5", "412.5"),
    _pos("NSE", "INFY-EQ", "1594", "C", 0, "0.00", "1500.00", "-2.5", "0.00", daybuyavgprc="1500.25"),
    _pos("NSE", "OLD-EQ", "7777", "I", 3, "12.00", "0.00", "1.00", "0.00"),
    _pos("MCX", "CRUDEOIL", "50001", "M", 1, "6500.00", "6510.00", "0.00", "0.00"),
    _pos("NFO", "NIFTY25NOV2624000CE", "40001", "M", -50, "120.00", "110.00", "2.00", "0.00"),
    _pos("NFO", "NIFTY25NOV26F", "35001", "C", 0, "0.00", "24010.00", "1.00", "0.00", totbuyavgprc="24000.00"),
]}
FACTOR_POSITIONS = {"body": [_pos("MCX", "CRUDEOIL", "50001", "M", 10, "100.00", "110.00", "0.00", "0.00", prcftr="0.5")]}
NO_POSITIONS = {"body": {"stat": "Not_Ok", "request_time": "10:20:30 03-11-2026", "emsg": "Error Occurred : 5 \"no data\""}}
HOLDINGS = {"body": [
    {"stat": "Ok", "exch_tsym": [{"exch": "NSE", "token": "1594", "tsym": "INFY-EQ", "pp": "2", "ti": "0.05", "ls": "1"}, {"exch": "BSE", "token": "500209", "tsym": "INFY", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "20", "dpqty": "20", "npoadqty": "0", "colqty": "0", "benqty": "0", "unplgdqty": "0", "brkcolqty": "0", "btstqty": "0", "btstcolqty": "0", "usedqty": "0", "upldprc": "1400.00", "s_prdt_ali": "CNC"},
    {"stat": "Ok", "exch_tsym": [{"exch": "BSE", "token": "500325", "tsym": "BSEONLY", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "4", "dpqty": "4", "npoadqty": "0", "colqty": "0", "benqty": "0", "unplgdqty": "0", "brkcolqty": "0", "btstqty": "0", "btstcolqty": "0", "usedqty": "0", "upldprc": "250.00", "s_prdt_ali": "CNC"},
    {"stat": "Ok", "exch_tsym": [{"exch": "NSE", "token": "7777", "tsym": "OLD-EQ", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "5", "dpqty": "2", "npoadqty": "6", "colqty": "0", "benqty": "1", "unplgdqty": "1", "brkcolqty": "2", "btstqty": "1", "btstcolqty": "0", "usedqty": "2", "upldprc": "12.50", "s_prdt_ali": "CNC"},
]}
LIMITS = {"body": {"stat": "Ok", "request_time": "10:20:30 03-11-2026", "cash": "100000.50", "payin": "1500.25", "marginused": "274.95", "brkcollamt": "1500.00", "rpnl": "-30.00", "unmtom": "-12.50"}}


def req(name, path, request, broker=None, **extra):
    return {"name": name, "path": path, "request": request, "broker": {} if broker is None else broker, **extra}


PLACE_EP = f"POST {BASE}/PlaceOrder"
Q = f"POST {BASE}/GetQuotes"
SCENARIOS = [
    req("auth_invalid_key", "/api/v1/funds", {"apikey": "wrong-key"}),
    req("placeorder_limit", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_OK}),
    req("placeorder_limit_cnc", "/api/v1/placeorder", {**PLACE, "symbol": "INFY", "action": "SELL", "product": "CNC", "price": "1500.5", "disclosed_quantity": "2"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_sl_limit", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_ampersand_symbol", "/api/v1/placeorder", {**PLACE, "symbol": "M&M"}, {PLACE_EP: ORDER_OK}),
    req("placeorder_market_becomes_a_protected_limit_buy", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {Q: quote(), PLACE_EP: ORDER_OK}),
    req("placeorder_market_becomes_a_protected_limit_sell", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0", "action": "SELL"}, {Q: quote(), PLACE_EP: ORDER_OK}),
    req("placeorder_market_option_uses_the_option_slab", "/api/v1/placeorder", {**PLACE, "symbol": "NIFTY25NOV2624000CE", "exchange": "NFO", "quantity": "75", "pricetype": "MARKET", "product": "NRML", "price": "0"},
        {Q: quote("40001", "NFO", "120.3", "0.05", "NIFTY25NOV2624000CE"), PLACE_EP: ORDER_OK}),
    req("placeorder_market_cheap_share", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {Q: quote(ltp="42.35"), PLACE_EP: ORDER_OK}),
    req("placeorder_market_quote_has_no_price", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {Q: quote(ltp="0"), PLACE_EP: ORDER_OK}),
    req("placeorder_market_quote_refused", "/api/v1/placeorder", {**PLACE, "pricetype": "MARKET", "price": "0"}, {Q: {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}, PLACE_EP: ORDER_OK}),
    req("placeorder_stop_market_becomes_a_stop_limit", "/api/v1/placeorder", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"}, {Q: quote(), PLACE_EP: ORDER_OK}),
    req("placeorder_stop_market_without_a_quote_is_priced_off_the_trigger", "/api/v1/placeorder", {**PLACE, "pricetype": "SL-M", "price": "0", "trigger_price": "801.5"},
        {Q: {"body": {"stat": "Not_Ok", "emsg": "no quote"}}, PLACE_EP: ORDER_OK}),
    req("placeorder_broker_refuses", "/api/v1/placeorder", PLACE, {PLACE_EP: ORDER_REFUSED}),
    req("placeorder_unknown_symbol", "/api/v1/placeorder", {**PLACE, "symbol": "NOPE"}, {PLACE_EP: ORDER_OK}),
    req("modifyorder_limit", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"POST {BASE}/ModifyOrder": ORDER_OK}),
    req("modifyorder_stop_limit", "/api/v1/modifyorder", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "orderid": "1003", "action": "BUY", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24020", "disclosed_quantity": "5", "trigger_price": "24005"},
        {f"POST {BASE}/ModifyOrder": ORDER_OK}),
    req("modifyorder_ampersand_symbol", "/api/v1/modifyorder", {"strategy": "t", "symbol": "M&M", "exchange": "NSE", "orderid": "1011", "action": "BUY", "quantity": "1", "pricetype": "LIMIT", "product": "MIS", "price": "2800", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"POST {BASE}/ModifyOrder": ORDER_OK}),
    req("modifyorder_refused", "/api/v1/modifyorder", {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "1002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"},
        {f"POST {BASE}/ModifyOrder": ORDER_REFUSED}),
    req("cancelorder", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1001"}, {f"POST {BASE}/CancelOrder": {"body": {"stat": "Ok", "result": "1001"}}}),
    req("cancelorder_refused", "/api/v1/cancelorder", {"strategy": "t", "orderid": "1002"}, {f"POST {BASE}/CancelOrder": {"body": {"stat": "Not_Ok", "emsg": "Order not found"}}}),
    req("cancelallorder", "/api/v1/cancelallorder", {"strategy": "t"}, {f"POST {BASE}/OrderBook": ORDERS, f"POST {BASE}/CancelOrder": {"body": {"stat": "Ok", "result": "x"}}}),
    req("cancelallorder_book_empty", "/api/v1/cancelallorder", {"strategy": "t"}, {f"POST {BASE}/OrderBook": NO_ORDERS}),
    req("closeposition", "/api/v1/closeposition", {"strategy": "t"}, {f"POST {BASE}/PositionBook": POSITIONS, PLACE_EP: ORDER_OK, Q: {"body": {"stat": "Not_Ok", "emsg": "no quote"}}}),
    req("closeposition_nothing_open", "/api/v1/closeposition", {"strategy": "t"}, {f"POST {BASE}/PositionBook": NO_POSITIONS}),
    req("placesmartorder_reduce", "/api/v1/placesmartorder", {**PLACE, "quantity": "0", "position_size": "4"}, {f"POST {BASE}/PositionBook": POSITIONS, PLACE_EP: ORDER_OK}),
    req("orderbook", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": ORDERS}),
    req("orderbook_average_and_reported_prices", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": ORDERS_PRICES}),
    req("orderbook_empty", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": NO_ORDERS}),
    req("tradebook", "/api/v1/tradebook", {}, {f"POST {BASE}/TradeBook": TRADES}),
    req("tradebook_rounding", "/api/v1/tradebook", {}, {f"POST {BASE}/TradeBook": TRADES_ROUNDING}),
    req("positionbook_price_factor", "/api/v1/positionbook", {}, {f"POST {BASE}/PositionBook": FACTOR_POSITIONS}),
    req("orderbook_negative_reported_price_is_ignored", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": NEGATIVE_REPORTED}),
    req("orderbook_rate_limited_then_answered", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": [LIMITED, ORDERS]}),
    req("orderbook_rate_limited_every_time", "/api/v1/orderbook", {}, {f"POST {BASE}/OrderBook": LIMITED}),
    req("placeorder_rate_limited_is_not_asked_again", "/api/v1/placeorder", PLACE, {PLACE_EP: LIMITED}),
    req("tradebook_empty", "/api/v1/tradebook", {}, {f"POST {BASE}/TradeBook": NO_ORDERS}),
    req("positionbook", "/api/v1/positionbook", {}, {f"POST {BASE}/PositionBook": POSITIONS}),
    req("positionbook_empty", "/api/v1/positionbook", {}, {f"POST {BASE}/PositionBook": NO_POSITIONS}),
    req("holdings", "/api/v1/holdings", {}, {f"POST {BASE}/Holdings": HOLDINGS}),
    req("holdings_empty", "/api/v1/holdings", {}, {f"POST {BASE}/Holdings": NO_ORDERS}),
    req("funds", "/api/v1/funds", {}, {f"POST {BASE}/Limits": LIMITS, f"POST {BASE}/PositionBook": POSITIONS}),
    req("funds_positions_book_empty", "/api/v1/funds", {}, {f"POST {BASE}/Limits": LIMITS, f"POST {BASE}/PositionBook": NO_POSITIONS}),
    req("funds_refused", "/api/v1/funds", {}, {f"POST {BASE}/Limits": {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}}),
    req("orderstatus_complete", "/api/v1/orderstatus", {"strategy": "t", "orderid": "1002"}, {f"POST {BASE}/OrderBook": ORDERS, f"POST {BASE}/TradeBook": TRADES}),
    req("openposition_short", "/api/v1/openposition", {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, {f"POST {BASE}/PositionBook": POSITIONS}),
]
