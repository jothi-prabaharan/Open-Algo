"""Angel One scenarios: OpenAlgo requests and the SmartAPI replies they meet.

SmartAPI replies follow the shapes in Angel One's SmartAPI documentation, including
its habit of sending numbers as strings.
"""

AUTH_TOKEN = "angel-jwt-token"
ENV = {"BROKER_API_KEY": "angel-private-key", "BROKER_API_SECRET": "angel-secret"}
READ_POSTS = ["/rest/secure/angelbroking/market/v1/quote/"]

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN-EQ", "name": "SBIN", "exchange": "NSE", "brexchange": "NSE", "token": "3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY-EQ", "name": "INFY", "exchange": "NSE", "brexchange": "NSE", "token": "1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY25NOV26FUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "35001", "expiry": "25NOV2026", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "99926000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "AMXIDX", "tick_size": 0.05},
]

OK = {"status": True, "message": "SUCCESS", "errorcode": ""}
ORDER_OK = {"body": {**OK, "data": {"script": "SBIN-EQ", "orderid": "201103000000111", "uniqueorderid": "a1b2c3"}}}
ORDER_REFUSED = {"body": {"status": False, "message": "Order rejected: insufficient margin", "errorcode": "AB4008", "data": ""}}


def _order(oid, exch, token, sym, side, otype, product, qty, price, avg, trigger, status, updated, variety="NORMAL"):
    return {"variety": variety, "ordertype": otype, "producttype": product, "duration": "DAY", "price": price, "triggerprice": trigger,
            "quantity": qty, "disclosedquantity": "0", "squareoff": 0, "stoploss": 0, "trailingstoploss": 0, "tradingsymbol": sym,
            "transactiontype": side, "exchange": exch, "symboltoken": token, "ordertag": "", "instrumenttype": "", "strikeprice": -1,
            "optiontype": "", "expirydate": "", "lotsize": "1", "cancelsize": "0", "averageprice": avg, "filledshares": "0",
            "unfilledshares": qty, "orderid": oid, "text": "", "status": status, "orderstatus": status, "updatetime": updated,
            "exchtime": updated, "exchorderupdatetime": updated, "fillid": "", "filltime": "", "parentorderid": "", "uniqueorderid": "u" + oid}


ORDERS = {"body": {**OK, "data": [
    _order("2001", "NSE", "3045", "SBIN-EQ", "BUY", "LIMIT", "INTRADAY", "10", 800.5, 0, 0, "open", "03-Nov-2026 09:20:01"),
    _order("2002", "NSE", "1594", "INFY-EQ", "SELL", "MARKET", "DELIVERY", "5", 0, 1499.95, 0, "complete", "03-Nov-2026 09:21:11"),
    _order("2003", "NFO", "35001", "NIFTY25NOV26FUT", "BUY", "STOPLOSS_LIMIT", "CARRYFORWARD", "75", 24010, 0, 24000, "trigger pending", "03-Nov-2026 09:22:00", "STOPLOSS"),
    _order("2004", "NSE", "3045", "SBIN-EQ", "SELL", "STOPLOSS_MARKET", "INTRADAY", "1", 0, 0, 790.123, "rejected", "03-Nov-2026 09:23:00", "STOPLOSS"),
    _order("2005", "NSE", "88888", "UNKNOWN-EQ", "BUY", "LIMIT", "DELIVERY", "2", 10, 0, 0, "cancelled", "03-Nov-2026 09:24:00"),
]}}
TRADES = {"body": {**OK, "data": [
    {"exchange": "NSE", "producttype": "DELIVERY", "tradingsymbol": "INFY-EQ", "instrumenttype": "", "symbolgroup": "EQ", "strikeprice": "-1", "optiontype": "", "expirydate": "", "marketlot": "1", "precision": "2", "multiplier": "-1", "tradevalue": 7499.75, "transactiontype": "SELL", "fillprice": 1499.95, "fillsize": 5, "orderid": "2002", "fillid": "t1", "filltime": "09:21:12"},
    {"exchange": "NFO", "producttype": "CARRYFORWARD", "tradingsymbol": "NIFTY25NOV26FUT", "instrumenttype": "FUTIDX", "symbolgroup": "XX", "strikeprice": "-1", "optiontype": "", "expirydate": "25NOV2026", "marketlot": "75", "precision": "2", "multiplier": "-1", "tradevalue": 1800416.625, "transactiontype": "BUY", "fillprice": 24005.555, "fillsize": 75, "orderid": "2006", "fillid": "t2", "filltime": "09:25:01"},
]}}
POSITIONS = {"body": {**OK, "data": [
    {"exchange": "NSE", "symboltoken": "3045", "producttype": "INTRADAY", "tradingsymbol": "SBIN-EQ", "symbolname": "SBIN", "instrumenttype": "", "netqty": "10", "avgnetprice": "801.12", "netprice": "801.12", "ltp": "805.45", "pnl": "43.33", "realised": "0", "unrealised": "43.33", "buyqty": "10", "sellqty": "0"},
    {"exchange": "NFO", "symboltoken": "35001", "producttype": "CARRYFORWARD", "tradingsymbol": "NIFTY25NOV26FUT", "symbolname": "NIFTY", "instrumenttype": "FUTIDX", "netqty": "-75", "avgnetprice": "24005.50", "netprice": "24005.50", "ltp": "23990.00", "pnl": "1162.50", "realised": "0", "unrealised": "1162.50", "buyqty": "0", "sellqty": "75"},
    {"exchange": "NSE", "symboltoken": "1594", "producttype": "DELIVERY", "tradingsymbol": "INFY-EQ", "symbolname": "INFY", "instrumenttype": "", "netqty": "0", "avgnetprice": "0.00", "netprice": "0", "ltp": "1499.00", "pnl": "-2.50", "realised": "-2.50", "unrealised": "0", "buyqty": "5", "sellqty": "5"},
]}}
HOLDINGS = {"body": {**OK, "data": {
    "holdings": [
        {"tradingsymbol": "INFY-EQ", "exchange": "NSE", "isin": "INE009A01021", "t1quantity": 0, "realisedquantity": 20, "quantity": 20, "authorisedquantity": 0, "product": "DELIVERY", "collateralquantity": None, "collateraltype": None, "haircut": 0, "averageprice": 1400.0, "ltp": 1499.0, "symboltoken": "1594", "close": 1490.0, "profitandloss": 1980, "pnlpercentage": 7.07},
        {"tradingsymbol": "OLDCO-EQ", "exchange": "NSE", "isin": "INE000000000", "t1quantity": 0, "realisedquantity": 3, "quantity": 3, "authorisedquantity": 0, "product": "DELIVERY", "collateralquantity": None, "collateraltype": None, "haircut": 0, "averageprice": 12.5, "ltp": 11.25, "symboltoken": "55555", "close": 11.0, "profitandloss": -3.75, "pnlpercentage": -10.0},
    ],
    "totalholding": {"totalholdingvalue": 30013.75, "totalinvvalue": 28037.5, "totalprofitandloss": 1976.25, "totalpnlpercentage": 7.05},
}}}
RMS = {"body": {**OK, "data": {"net": "99725.05", "availablecash": "101500.00", "availableintradaypayin": "0", "availablelimitmargin": "0", "collateral": "0", "m2munrealized": "0", "m2mrealized": "0", "utiliseddebits": "274.95", "utilisedspan": "0", "utilisedoptionpremium": "0", "utilisedholdingsales": "0", "utilisedexposure": "0", "utilisedturnover": "0", "utilisedpayout": "100000.00"}}}


def _q(token, sym, exch, ltp, close, buy=(), sell=(), oi=0):
    return {"exchange": exch, "tradingSymbol": sym, "symbolToken": token, "ltp": ltp, "open": close, "high": round(ltp * 1.01, 2), "low": round(ltp * 0.99, 2),
            "close": close, "lastTradeQty": 5, "tradeVolume": 1234567, "opnInterest": oi, "totBuyQuan": 170, "totSellQuan": 97,
            "depth": {"buy": [{"price": p, "quantity": q, "orders": 1} for p, q in buy], "sell": [{"price": p, "quantity": q, "orders": 1} for p, q in sell]}}


QUOTE_SBIN = {"body": {**OK, "data": {"fetched": [_q("3045", "SBIN-EQ", "NSE", 805.45, 799.95, buy=[(805.4, 120), (805.35, 50)], sell=[(805.5, 80), (805.55, 10), (805.6, 7)])], "unfetched": []}}}
QUOTE_INDEX = {"body": {**OK, "data": {"fetched": [_q("99926000", "Nifty 50", "NSE", 24050.5, 24000.0)], "unfetched": []}}}

PLACE = {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS"}
QUOTE = "POST /rest/secure/angelbroking/market/v1/quote/"
PLACE_URL = "POST /rest/secure/angelbroking/order/v1/placeOrder"
BOOK = "GET /rest/secure/angelbroking/order/v1/getOrderBook"
POS = "GET /rest/secure/angelbroking/order/v1/getPosition"

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": 5, "pricetype": "LIMIT", "product": "CNC", "price": 1500.5, "disclosed_quantity": "2"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_sl_futures", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "pricetype": "SL", "product": "NRML", "price": "24010", "trigger_price": "24000"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_slm", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL-M", "trigger_price": "801.05"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_REFUSED}},
    {"name": "placeorder_ambiguous_reply", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: {"status": 502, "body": {}}, BOOK: ORDERS}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOPE"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "modifyorder", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "2001", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"POST /rest/secure/angelbroking/order/v1/modifyOrder": {"body": {**OK, "data": {"orderid": "2001", "uniqueorderid": "u2001"}}}}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "orderid": "2002", "action": "BUY", "quantity": "12", "pricetype": "LIMIT", "product": "MIS", "price": "801", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {"POST /rest/secure/angelbroking/order/v1/modifyOrder": {"body": {"status": False, "message": "Order is already complete", "errorcode": "AB4006", "data": None}}}},
    {"name": "cancelorder", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "2001"}, "broker": {"POST /rest/secure/angelbroking/order/v1/cancelOrder": {"body": {**OK, "data": {"orderid": "2001", "uniqueorderid": "u2001"}}}}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"strategy": "t", "orderid": "2002"}, "broker": {"POST /rest/secure/angelbroking/order/v1/cancelOrder": {"body": {"status": False, "message": "Order is already complete", "errorcode": "AB4006", "data": None}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {BOOK: ORDERS, "POST /rest/secure/angelbroking/order/v1/cancelOrder": {"body": {**OK, "data": {"orderid": "x"}}}}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "closeposition_one_exit_refused", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POS: POSITIONS, PLACE_URL: [ORDER_OK, ORDER_REFUSED]}},
    {"name": "placesmartorder_reduce", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "4"}, "broker": {POS: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {BOOK: ORDERS}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {"GET /rest/secure/angelbroking/order/v1/getTradeBook": TRADES}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {POS: POSITIONS}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /rest/secure/angelbroking/portfolio/v1/getAllHolding": HOLDINGS}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /rest/secure/angelbroking/user/v1/getRMS": RMS, POS: POSITIONS}},
    {"name": "funds_token_expired", "path": "/api/v1/funds", "request": {}, "broker": {"GET /rest/secure/angelbroking/user/v1/getRMS": {"status": 401, "body": {"status": False, "message": "Invalid Token", "errorcode": "AG8001", "data": None}}, POS: POSITIONS}},
    {"name": "orderstatus_complete", "path": "/api/v1/orderstatus", "request": {"strategy": "t", "orderid": "2002"}, "broker": {BOOK: ORDERS, "GET /rest/secure/angelbroking/order/v1/getTradeBook": TRADES}},
    {"name": "openposition_short", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML"}, "broker": {POS: POSITIONS}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: QUOTE_SBIN}},
    {"name": "quotes_index", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, "broker": {QUOTE: QUOTE_INDEX}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: QUOTE_SBIN}},
    {"name": "quotes_refused", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTE: {"body": {"status": False, "message": "Invalid Token", "errorcode": "AG8001", "data": None}}}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
]
