"""Shoonya quotes, depth and several quotes (POST /PiConnectAPI/GetQuotes)."""

import scenarios_flattrade as s
from scenarios_flattrade import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, Q, quote, req  # noqa: F401

BROKER = "flattrade"
SYMBOLS = s.SYMBOLS + [
    {"symbol": "NIFTY", "brsymbol": "Nifty 50", "name": "NIFTY", "exchange": "NSE_INDEX", "brexchange": "NSE_INDEX", "token": "26000", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
    {"symbol": "TATASTEEL", "brsymbol": "TATASTEEL-EQ", "name": "TATA STEEL", "exchange": "BSE", "brexchange": "BSE", "token": "500470", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
]

WRONG = quote("26000", "NSE", "24100.25", "0.05", "Nifty 50")
NO_C = {"body": {k: v for k, v in quote()["body"].items() if k != "c"}}

SCENARIOS = [
    req("quotes_equity", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: quote()}),
    req("quotes_futures", "/api/v1/quotes", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {Q: quote("35001", "NFO", "24005.5", "0.1", "NIFTY25NOV26F", "24005.4", "24005.6")}),
    req("quotes_index_asks_the_cash_segment", "/api/v1/quotes", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {Q: quote("26000", "NSE", "24100.25", "0.05", "Nifty 50")}),
    req("quotes_bse", "/api/v1/quotes", {"symbol": "TATASTEEL", "exchange": "BSE"}, {Q: quote("500470", "BSE", "150.4", "0.05", "TATASTEEL-EQ")}),
    req("quotes_without_previous_close", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: NO_C}),
    req("quotes_other_instruments_answer_is_asked_again", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: [WRONG, quote()]}),
    req("quotes_other_instruments_answer_every_time", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: WRONG}),
    req("quotes_reply_names_no_token", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: {"body": {k: v for k, v in quote()["body"].items() if k != "token"}}}),
    req("quotes_same_token_other_exchange", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: quote("3045", "BSE")}),
    req("quotes_refused", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: {"body": {"stat": "Not_Ok", "emsg": "Error Occurred : 5 \"no data\""}}}),
    req("quotes_session_expired", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {Q: {"body": {"stat": "Not_Ok", "emsg": "Session Expired : Invalid Session Key"}}}),
    req("quotes_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}, {Q: quote()}),
    req("depth_five_levels", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {Q: quote()}),
    req("depth_index", "/api/v1/depth", {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {Q: quote("26000", "NSE", "24100.25", "0.05", "Nifty 50")}),
    req("depth_refused", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {Q: {"body": {"stat": "Not_Ok", "emsg": "no data"}}}),
    req("multiquotes", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "TATASTEEL", "exchange": "BSE"}]},
        {Q: [quote(), quote("500470", "BSE", "150.4", "0.05", "TATASTEEL-EQ")]}),
    req("multiquotes_one_refused", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "TATASTEEL", "exchange": "BSE"}]},
        {Q: [quote(), {"body": {"stat": "Not_Ok", "emsg": "no data"}}]}),
    req("multiquotes_other_instrument_every_time", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, {Q: WRONG}),
]


def patch_clients(client):
    # Several quotes are fetched with httpx.post directly, on threads (never through the shared client), so the recorder stands in for it too.
    import httpx

    httpx.post = lambda url, **kw: client.post(url, **{k: v for k, v in kw.items() if k != "timeout"})


def patch():
    import importlib

    importlib.import_module("broker.flattrade.api.data").USE_ASYNC = False
