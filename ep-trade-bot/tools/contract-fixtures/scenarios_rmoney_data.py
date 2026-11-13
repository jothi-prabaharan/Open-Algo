"""RMoney (XTS) market data: the Wisdom Capital cases with the service under /apibinarymarketdata."""

import scenarios_wisdom_data as _d
from _rekey import rekey
from scenarios_wisdom_data import AUTH_TOKEN, ENV, SYMBOLS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "rmoney"
SCENARIOS = rekey(_d.SCENARIOS)

# RMoney asks for open interest of the whole batch in a second call and merges the figures that are not zero.
import json as _json

_OI_MULTI = {"body": {"type": "success", "result": {"listQuotes": [
    _json.dumps({"ExchangeSegment": 2, "ExchangeInstrumentID": 35001, "OpenInterest": 123456}),
    _json.dumps({"ExchangeSegment": 1, "ExchangeInstrumentID": 22, "OpenInterest": 0}),
    _json.dumps({"ExchangeSegment": 1, "ExchangeInstrumentID": 99999, "OpenInterest": 5}),
]}}}
SCENARIOS += rekey([
    {"name": "multiquotes_with_open_interest_merged", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}]},
     "broker": {_d.QUOTES: [_d._reply(_d.SBIN_Q, _d.FUT_Q), _OI_MULTI]}},
    {"name": "multiquotes_open_interest_call_refused", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}]},
     "broker": {_d.QUOTES: [_d._reply(_d.FUT_Q), {"status": 400, "body": {"type": "error", "description": "busy"}}]}},
])
