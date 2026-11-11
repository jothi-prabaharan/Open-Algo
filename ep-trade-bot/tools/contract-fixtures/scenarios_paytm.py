"""Paytm Money scenarios: OpenAlgo requests and the Paytm Money replies they meet.

Paytm Money replies follow the shapes in OpenAlgo's broker/paytm mapping code: orders carry
display_status and txn_type letters, and every list sits under "data".
"""

AUTH_TOKEN = "paytm-access-token"
ENV = {"BROKER_API_KEY": "paytm-api-key", "BROKER_API_SECRET": "paytm-api-secret"}
SEED_API_KEY_ROW = True

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "BSE", "brexchange": "BSE", "token": "500209", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQUITY", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV26FUT", "brsymbol": "NIFTY-Nov2026-FUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE", "token": "52023", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUTIDX", "tick_size": 0.1},
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY-Nov2026-24000-CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NSE", "token": "52024", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "OPTIDX", "tick_size": 0.05},
    {"symbol": "NIFTY", "brsymbol": "NIFTY 50", "name": "NIFTY 50", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "13", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

ORDER_OK = {"body": {"status": "success", "message": "SUCCESS", "data": [{"order_no": "2001000000001", "message": "Order placed", "status": "success"}]}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds to place this order"}}


def _order(no, security_id, exch, instr, side, display, status, dtype, otype, product, qty, price, trigger, avg, remaining, when="2026-11-03 09:20:01"):
    return {
        "order_no": no, "security_id": security_id, "exchange": exch, "instrument": instr, "segment": "D" if instr != "ES" else "E",
        "txn_type": side, "display_status": display, "status": status, "display_order_type": dtype, "order_type": otype, "product": product,
        "quantity": qty, "price": price, "trigger_price": trigger, "avg_traded_price": avg, "remaining_quantity": remaining, "validity": "DAY",
        "off_mkt_flag": "N", "mkt_type": "NL", "serial_no": 1, "group_id": 0, "order_date_time": when,
    }


ORDERS = {"body": {"status": "success", "data": [
    _order("2001000000001", "3045", "NSE", "ES", "B", "Successful", "Successful", "Market", "MKT", "I", 10, 0.0, 0.0, 805.5, 0),
    _order("2001000000002", "1594", "NSE", "ES", "S", "Pending", "Pending", "Limit", "LMT", "C", 5, 1500.0, 0.0, 0.0, 5),
    _order("2001000000003", "52023", "NSE", "FUTIDX", "B", "Rejected", "Rejected", "Limit", "LMT", "M", 75, 24000.0, 0.0, 0.0, 75),
    _order("2001000000004", "52024", "NSE", "OPTIDX", "S", "Open", "Open", "Stop Loss", "SL", "M", 75, 120.0, 110.0, 0.0, 75),
    _order("2001000000005", "500209", "BSE", "ES", "B", "Cancelled", "Cancelled", "Market", "MKT", "C", 3, 0.0, 0.0, 0.0, 3),
    _order("2001000000006", "3045", "NSE", "ES", "S", "Successful", "Successful", "Limit", "LMT", "C", 4, 810.0, 0.0, 810.0, 0),
]}}
ORDERS_NONE = {"body": {"status": "success", "data": None}}


def _pos(security_id, exch, instr, product, net_qty, display, buy_avg, sell_avg, net_avg, ltp, net_val):
    return {"security_id": security_id, "exchange": exch, "instrument": instr, "product": product, "net_qty": net_qty, "display_pos_type": display,
            "buy_avg": buy_avg, "sell_avg": sell_avg, "net_avg": net_avg, "last_traded_price": ltp, "net_val": net_val, "realised_profit": 10.5, "unrealised_profit": 43.333}


POSITIONS = {"body": {"status": "success", "data": [
    _pos("3045", "NSE", "ES", "I", 10, "B", 801.1234, 0, 801.1234, 805.45, 43.33),
    _pos("52023", "NSE", "FUTIDX", "M", -75, "S", 0, 24005.5, 24005.5, 23990.0, 1162.5),
    _pos("1594", "NSE", "ES", "C", 0, "N", 1500.0, 1499.0, 0, 1499.0, -2.5),
]}}
HOLDINGS = {"body": {"status": "success", "data": {"results": [
    {"nse_security_id": "3045", "bse_security_id": "500112", "nse_symbol": "SBIN", "bse_symbol": "SBIN", "quantity": 20, "cost_price": 700.0, "last_traded_price": 805.5, "pc": 799.95},
    {"bse_security_id": "500209", "bse_symbol": "INFY", "quantity": 3, "avg_price": 1400.0, "ltp": 1499.0, "pc": 1490.0},
    {"nse_security_id": "9999", "nse_symbol": "UNKNOWNCO", "quantity": 2, "cost_price": 0, "last_traded_price": 0},
]}}}
HOLDINGS_LIST = {"body": {"status": "success", "data": []}}
FUNDS = {"body": {"status": "success", "data": {"funds_summary": {"available_cash": 99725.05, "collaterals": 1500.0, "utilised_amount": 274.95}}}}


def _live(token, last, o, h, l, c, vol, oi=0, buy=(), sell=()):
    return {"security_id": token, "last_price": last, "last_quantity": 5, "ohlc": {"open": o, "high": h, "low": l, "close": c}, "volume_traded": vol, "volume": vol, "oi": oi,
            "depth": {"buy": [{"price": p, "quantity": q} for p, q in buy], "sell": [{"price": p, "quantity": q} for p, q in sell]}}


LIVE_SBIN = {"body": {"status": "success", "data": [_live("3045", 805.45, 800.0, 810.0, 795.5, 799.95, 1234567, buy=[(805.4, 120), (805.35, 50)], sell=[(805.5, 80), (805.55, 40), (805.6, 10), (805.65, 5), (805.7, 5), (805.75, 7)])]}}
LIVE_MULTI = {"body": {"status": "success", "data": [_live("3045", 805.45, 800.0, 810.0, 795.5, 799.95, 1234567), _live("13", 24050.5, 24000.0, 24100.0, 23950.0, 24000.0, 0)]}}
LIVE_EMPTY = {"body": {"status": "success", "data": []}}

PLACE = {"symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "MARKET", "product": "MIS", "strategy": "test"}
PLACE_URL = "POST /orders/v1/place/regular"
BOOK = "GET /orders/v1/user/orders"
POSITION = "GET /orders/v1/position"

SCENARIOS = [
    {"name": "auth_invalid_key", "path": "/api/v1/funds", "request": {"apikey": "wrong-key"}},
    {"name": "placeorder_missing_fields", "path": "/api/v1/placeorder", "request": {"symbol": "SBIN"}},
    {"name": "placeorder_market", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_limit_cnc", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "INFY", "action": "SELL", "quantity": "5", "pricetype": "LIMIT", "product": "CNC", "price": "1500.5"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_futures_nrml", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "quantity": "75", "product": "NRML"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_stop_loss_with_trigger", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL", "price": "800", "trigger_price": "801.5"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_stop_loss_market", "path": "/api/v1/placeorder", "request": {**PLACE, "pricetype": "SL-M", "trigger_price": "801.5"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "placeorder_broker_refuses", "path": "/api/v1/placeorder", "request": PLACE, "broker": {PLACE_URL: ORDER_REFUSED}},
    {"name": "placeorder_unknown_symbol", "path": "/api/v1/placeorder", "request": {**PLACE, "symbol": "NOSUCH"}, "broker": {PLACE_URL: ORDER_OK}},
    {"name": "modifyorder_pending", "path": "/api/v1/modifyorder", "request": {"orderid": "2001000000002", "strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "product": "CNC", "pricetype": "LIMIT", "price": "1495.5", "quantity": "5", "disclosed_quantity": "0", "trigger_price": "0"},
     "broker": {BOOK: ORDERS, "POST /orders/v1/modify/regular": ORDER_OK}},
    {"name": "modifyorder_to_market", "path": "/api/v1/modifyorder", "request": {"orderid": "2001000000002", "strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "product": "CNC", "pricetype": "MARKET", "price": "0", "quantity": "5", "disclosed_quantity": "0", "trigger_price": "0"},
     "broker": {BOOK: ORDERS, "POST /orders/v1/modify/regular": ORDER_OK}},
    {"name": "modifyorder_not_found", "path": "/api/v1/modifyorder", "request": {"orderid": "999", "strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "product": "CNC", "pricetype": "LIMIT", "price": "1495.5", "quantity": "5", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {BOOK: ORDERS}},
    {"name": "modifyorder_already_complete", "path": "/api/v1/modifyorder", "request": {"orderid": "2001000000001", "strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "product": "MIS", "pricetype": "LIMIT", "price": "800", "quantity": "10", "disclosed_quantity": "0", "trigger_price": "0"}, "broker": {BOOK: ORDERS}},
    {"name": "modifyorder_refused", "path": "/api/v1/modifyorder", "request": {"orderid": "2001000000002", "strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "product": "CNC", "pricetype": "LIMIT", "price": "1495.5", "quantity": "5", "disclosed_quantity": "0", "trigger_price": "0"},
     "broker": {BOOK: ORDERS, "POST /orders/v1/modify/regular": ORDER_REFUSED}},
    {"name": "cancelorder_pending", "path": "/api/v1/cancelorder", "request": {"orderid": "2001000000002", "strategy": "t"}, "broker": {BOOK: ORDERS, "POST /orders/v1/cancel/regular": ORDER_OK}},
    {"name": "cancelorder_not_found", "path": "/api/v1/cancelorder", "request": {"orderid": "999", "strategy": "t"}, "broker": {BOOK: ORDERS}},
    {"name": "cancelorder_already_complete", "path": "/api/v1/cancelorder", "request": {"orderid": "2001000000001", "strategy": "t"}, "broker": {BOOK: ORDERS}},
    {"name": "cancelorder_refused", "path": "/api/v1/cancelorder", "request": {"orderid": "2001000000002", "strategy": "t"}, "broker": {BOOK: ORDERS, "POST /orders/v1/cancel/regular": ORDER_REFUSED}},
    {"name": "orderbook", "path": "/api/v1/orderbook", "request": {}, "broker": {BOOK: ORDERS}},
    {"name": "orderbook_empty", "path": "/api/v1/orderbook", "request": {}, "broker": {BOOK: ORDERS_NONE}},
    {"name": "tradebook", "path": "/api/v1/tradebook", "request": {}, "broker": {BOOK: ORDERS}},
    {"name": "positionbook", "path": "/api/v1/positionbook", "request": {}, "broker": {POSITION: POSITIONS}},
    {"name": "holdings", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /holdings/v1/get-user-holdings-data": HOLDINGS}},
    {"name": "holdings_none", "path": "/api/v1/holdings", "request": {}, "broker": {"GET /holdings/v1/get-user-holdings-data": HOLDINGS_LIST}},
    {"name": "funds", "path": "/api/v1/funds", "request": {}, "broker": {"GET /accounts/v1/funds/summary": FUNDS, POSITION: POSITIONS}},
    {"name": "openposition_long", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, "broker": {POSITION: POSITIONS}},
    {"name": "quotes", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/v1/price/live": LIVE_SBIN}},
    {"name": "quotes_index", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, "broker": {"GET /data/v1/price/live": LIVE_MULTI}},
    {"name": "quotes_no_data", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/v1/price/live": LIVE_EMPTY}},
    {"name": "depth", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"GET /data/v1/price/live": LIVE_SBIN}},
    {"name": "quotes_futures", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, "broker": {"GET /data/v1/price/live": {"body": {"status": "success", "data": [_live("52023", 24010.0, 24000.0, 24050.0, 23950.0, 23990.0, 450000, 12900000)]}}}},
    {"name": "quotes_option", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, "broker": {"GET /data/v1/price/live": {"body": {"status": "success", "data": [_live("52024", 120.5, 118.0, 125.0, 110.0, 119.0, 90000, 450000)]}}}},
    {"name": "multiquotes", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}]}, "broker": {"GET /data/v1/price/live": LIVE_MULTI}},
    {"name": "ping", "path": "/api/v1/ping", "request": {}},
    # Smart orders, close-all and cancel-all: the position and the order book are read first.
    {"name": "placesmartorder_already_at_target", "path": "/api/v1/placesmartorder", "request": {**PLACE, "position_size": "10"}, "broker": {POSITION: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "placesmartorder_exit_to_zero", "path": "/api/v1/placesmartorder", "request": {**PLACE, "quantity": "0", "position_size": "0"}, "broker": {POSITION: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "placesmartorder_add_to_long", "path": "/api/v1/placesmartorder", "request": {**PLACE, "position_size": "25"}, "broker": {POSITION: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "placesmartorder_futures_nrml_short_to_flat", "path": "/api/v1/placesmartorder", "request": {**PLACE, "symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "product": "NRML", "quantity": "0", "position_size": "0"}, "broker": {POSITION: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "closeposition", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POSITION: POSITIONS, PLACE_URL: ORDER_OK}},
    {"name": "closeposition_nothing_open", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {POSITION: {"body": {"status": "success", "data": []}}}},
    {"name": "cancelallorder", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {BOOK: ORDERS, "POST /orders/v1/cancel/regular": ORDER_OK}},
]
