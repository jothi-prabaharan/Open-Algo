"""RMoney (XTS) scenarios: the Wisdom Capital set against RMoney's own plugin, plus what RMoney does differently (the balance entry for everything, positions as a list)."""

import json

from scenarios_wisdom import *  # noqa: F403
from scenarios_wisdom import ORDER_OK
from scenarios_wisdom import ENV as _ENV, SCENARIOS as _WISDOM, BALANCE, POSITIONS

ENV = dict(_ENV)

_entry = BALANCE["body"]["result"]["BalanceList"][0]


def _limit(header, cash):
    return {"limitHeader": header, "limitObject": {"RMSSubLimits": {"netMarginAvailable": cash, "collateral": 10, "UnrealizedMTM": 1, "RealizedMTM": 2, "marginUtilized": 3}}}


TWO_ENTRIES = {"body": {"type": "success", "result": {"BalanceList": [_limit("CLIENT|NSE|CASH", 111.5), _limit("ALL|ALL|ALL", 222.5)]}}}
NO_ALL_ENTRY = {"body": {"type": "success", "result": {"BalanceList": [_limit("CLIENT|NSE|CASH", 111.5), _limit("CLIENT|NSE|FO", 333.5)]}}}
LIST_POSITIONS = {"body": {"type": "success", "result": [
    {"AccountID": "WC001", "ExchangeSegment": "NSECM", "ExchangeInstrumentID": 22, "ProductType": "MIS", "Quantity": "10", "BuyAveragePrice": "801.1234", "SellAveragePrice": "0", "TradingSymbol": "SBIN-EQ"},
    {"AccountID": "WC001", "ExchangeSegment": "NSEFO", "ExchangeInstrumentId": 35001, "ProductType": "NRML", "Quantity": "-75", "BuyAveragePrice": "0", "SellAveragePrice": "24005.5", "TradingSymbol": "NIFTY"},
]}}

SCENARIOS = list(_WISDOM) + [
    {"name": "funds_all_entry_preferred", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": TWO_ENTRIES}},
    {"name": "funds_no_all_entry_takes_the_first", "path": "/api/v1/funds", "request": {}, "broker": {"GET /interactive/user/balance": NO_ALL_ENTRY}},
    {"name": "positionbook_result_is_a_list", "path": "/api/v1/positionbook", "request": {}, "broker": {"GET /interactive/portfolio/positions": LIST_POSITIONS}},
    {"name": "openposition_from_a_list", "path": "/api/v1/openposition", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "product": "MIS"}, "broker": {"GET /interactive/portfolio/positions": LIST_POSITIONS}},
    {"name": "closeposition_from_a_list", "path": "/api/v1/closeposition", "request": {"strategy": "t"}, "broker": {"GET /interactive/portfolio/positions": LIST_POSITIONS, "POST /interactive/orders": ORDER_OK}},
]
