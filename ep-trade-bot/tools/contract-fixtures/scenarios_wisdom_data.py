"""Wisdom Capital (XTS) market data: quotes, depth and several quotes in one call, against /apimarketdata/instruments/quotes."""

import json

from scenarios_wisdom import AUTH_TOKEN, ENV, SYMBOLS as _SYMBOLS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "wisdom"
SYMBOLS = _SYMBOLS + [
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
    {"symbol": "TATASTEEL", "brsymbol": "TATASTEEL", "name": "TATA STEEL", "exchange": "BSE", "brexchange": "BSECM", "token": "500470", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
]

QUOTES = "POST /apimarketdata/instruments/quotes"


def _level(price, size):
    return {"Size": size, "Price": price, "TotalOrders": 3, "BuyBackMarketMaker": 0}


def _quote(segment, iid, ltp, bid=None, ask=None, bids=(), asks=()):
    return {
        "MessageCode": 1502, "MessageVersion": 4, "ApplicationType": 0, "TokenID": 0, "ExchangeSegment": segment, "ExchangeInstrumentID": iid,
        "ExchangeTimeStamp": 1100000000, "Bids": [_level(p, s) for p, s in bids], "Asks": [_level(p, s) for p, s in asks],
        "Touchline": {"BidInfo": {"Size": 100, "Price": bid if bid is not None else ltp - 0.05, "TotalOrders": 5, "BuyBackMarketMaker": 0},
                      "AskInfo": {"Size": 200, "Price": ask if ask is not None else ltp + 0.05, "TotalOrders": 6, "BuyBackMarketMaker": 0},
                      "LastTradedPrice": ltp, "LastTradedQunatity": 25, "TotalBuyQuantity": 12345, "TotalSellQuantity": 23456, "TotalTradedQuantity": 1234567,
                      "AverageTradedPrice": ltp, "LastTradedTime": 1100000001, "LastUpdateTime": 1100000002, "PercentChange": 0.5, "Open": ltp - 3, "High": ltp + 4,
                      "Low": ltp - 5, "Close": ltp - 1.5, "TotalValueTraded": None, "BuyBackTotalBuy": 0, "BuyBackTotalSell": 0},
    }


def _reply(*quotes):
    return {"body": {"type": "success", "code": "s-quotes-0001", "description": "Get quotes successfully!", "result": {"mdp": 1502, "quotesList": [], "listQuotes": [json.dumps(q) for q in quotes]}}}


SBIN_Q = _quote(1, 22, 805.45, bids=[(805.4, 100), (805.35, 200), (805.3, 300), (805.25, 400), (805.2, 500), (805.15, 600)], asks=[(805.5, 110), (805.55, 210), (805.6, 310), (805.65, 410), (805.7, 510), (805.75, 610)])
FUT_Q = _quote(2, 35001, 24005.5, bids=[(24005.4, 75)], asks=[(24005.6, 150)])
OI = {"body": {"type": "success", "result": {"mdp": 1510, "listQuotes": [json.dumps({"MessageCode": 1510, "ExchangeSegment": 2, "ExchangeInstrumentID": 35001, "OpenInterest": 123456, "DayHighOI": 1, "DayLowOI": 1})]}}}
NO_OI = _reply()
DENIED = {"status": 400, "body": {"type": "error", "code": "e-marketdata-0006", "description": "Invalid Token", "result": {}}}

SCENARIOS = [
    {"name": "quotes_equity_without_open_interest", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTES: [_reply(SBIN_Q), NO_OI]}},
    {"name": "quotes_futures_with_open_interest", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, "broker": {QUOTES: [_reply(FUT_Q), OI]}},
    {"name": "quotes_index", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, "broker": {QUOTES: [_reply(_quote(1, 26000, 24100.25)), NO_OI]}},
    {"name": "quotes_bse", "path": "/api/v1/quotes", "request": {"symbol": "TATASTEEL", "exchange": "BSE"}, "broker": {QUOTES: [_reply(_quote(11, 500470, 150.4)), NO_OI]}},
    {"name": "quotes_mcx", "path": "/api/v1/quotes", "request": {"symbol": "CRUDEOIL", "exchange": "MCX"}, "broker": {QUOTES: [_reply(_quote(51, 50001, 6500)), NO_OI]}},
    {"name": "quotes_currency", "path": "/api/v1/quotes", "request": {"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, "broker": {QUOTES: [_reply(_quote(3, 60001, 84.5)), NO_OI]}},
    {"name": "quotes_open_interest_call_fails", "path": "/api/v1/quotes", "request": {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, "broker": {QUOTES: [_reply(FUT_Q), {"status": 500, "body": {"type": "error", "description": "busy"}}]}},
    {"name": "quotes_unknown_symbol", "path": "/api/v1/quotes", "request": {"symbol": "NOPE", "exchange": "NSE"}, "broker": {QUOTES: _reply(SBIN_Q)}},
    {"name": "quotes_market_data_refused", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTES: {"status": 400, "body": {"type": "error", "code": "e-x", "description": "Market data not enabled", "result": {}}}}},
    {"name": "quotes_empty_list", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTES: NO_OI}},
    {"name": "depth_five_levels", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTES: [_reply(SBIN_Q), NO_OI]}},
    {"name": "depth_futures_with_open_interest", "path": "/api/v1/depth", "request": {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, "broker": {QUOTES: [_reply(FUT_Q), OI]}},
    {"name": "depth_market_data_refused", "path": "/api/v1/depth", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {QUOTES: {"status": 400, "body": {"type": "error", "description": "Market data not enabled", "result": {}}}}},
    {"name": "multiquotes", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "NIFTY", "exchange": "NSE_INDEX"}]}, "broker": {QUOTES: _reply(SBIN_Q, FUT_Q, _quote(1, 26000, 24100.25))}},
    {"name": "multiquotes_one_missing_from_the_answer", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}]}, "broker": {QUOTES: _reply(SBIN_Q)}},
    {"name": "multiquotes_answer_holds_an_instrument_nobody_asked_for", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, "broker": {QUOTES: _reply(_quote(1, 1594, 1500), SBIN_Q)}},
    {"name": "multiquotes_refused", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, "broker": {QUOTES: {"status": 400, "body": {"type": "error", "description": "Market data not enabled", "result": {}}}}},
]
