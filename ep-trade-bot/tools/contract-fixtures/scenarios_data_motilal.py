"""Motilal quotes and today's daily candle. A quote is POST /rest/report/v3/getltpdata with {exchange, scripcode} (prices in paisa) and an index POST /rest/report/v3/getindexltpdata (rupees, data is a list). A dealer login is told by
MO1062 and the client code is sent from then on; the index exchange field is tried as exchangename, then exchange (MO1051). Motilal has no history endpoint: the one candle is today's, built from the quote."""

import scenarios_motilal as s
from scenarios_motilal import AUTH_TOKEN, CLIENT_ID, ENV, LTP, SEED_API_KEY_ROW, fail, ok  # noqa: F401

BROKER = "motilal"
READ_POSTS = ["/rest/report/v3/getltpdata", "/rest/report/v3/getindexltpdata"]
RECORDED_AT = "2026-11-11T10:00:00+05:30"
INDEX = "POST /rest/report/v3/getindexltpdata"
SYMBOLS = s.SYMBOLS + [
    s._sym("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "NIFTY", 1, "INDEX"),
    s._sym("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 999912, "SENSEX", 1, "INDEX"),
    s._sym("CRUDEIDX", "CRUDEIDX", "MCX_INDEX", "MCX", 77, "CRUDEIDX", 1, "INDEX"),
]


def before_each(sc):
    import datetime

    import broker.motilal.api.data as data

    import broker.motilal.api.baseurl as baseurl

    baseurl.set_client_code("VENDOR1")
    data._dealer_mode = False
    data._index_exchange_field = None
    data.BrokerData._today_ist = lambda self: datetime.date(2026, 11, 11)


def quote(ltp=80525, **extra):
    d = {"ltp": ltp, "open": 80100, "high": 80950, "low": 79900, "close": 80000, "bid": 80520, "ask": 80530, "volume": 1234567}
    d.update(extra)
    return ok(data=d)


def index_quote(code=26000, **extra):
    row = {"scripcode": code, "open": 24000.5, "high": 24150.25, "low": 23950.0, "close": 24010.75, "ltp": 24100.25}
    row.update(extra)
    return ok(data=[row])


def one(name, symbol, exchange, broker, path="/api/v1/quotes", **extra):
    return {"name": name, "path": path, "request": {"symbol": symbol, "exchange": exchange}, "broker": broker, **extra}


def hist(name, interval, start, end, broker, symbol="SBIN", exchange="NSE"):
    return {"name": name, "path": "/api/v1/history", "request": {"symbol": symbol, "exchange": exchange, "interval": interval, "start_date": start, "end_date": end}, "broker": broker, "ignore": []}


SCENARIOS = [
    one("quote_equity", "SBIN", "NSE", {LTP: quote()}),
    one("quote_future", "NIFTY25NOV26FUT", "NFO", {LTP: quote(2401050)}),
    one("quote_bse_equity", "TATAMOTORS", "BSE", {LTP: quote(49810)}),
    one("quote_zero_figures", "SBIN", "NSE", {LTP: ok(data={"ltp": 80525, "open": 0, "high": None, "close": 0})}),
    one("quote_figures_that_are_text", "SBIN", "NSE", {LTP: ok(data={"ltp": "80525", "open": "80100", "volume": "100"})}),
    one("quote_no_data", "SBIN", "NSE", {LTP: ok(data={})}),
    one("quote_null_data", "SBIN", "NSE", {LTP: ok(data=None)}),
    one("quote_refused", "SBIN", "NSE", {LTP: fail(200, "Token expired", "MO8002")}),
    one("quote_http_error", "SBIN", "NSE", {LTP: {"status": 500, "body": ""}}),
    one("quote_not_json", "SBIN", "NSE", {LTP: s.NOT_JSON}),
    one("quote_unknown_symbol", "NOPE", "NSE", {}),
    one("quote_dealer_login_sends_the_client_code", "SBIN", "NSE", {LTP: [fail(200, "Please provide client code", "MO1062"), quote()]}),
    one("quote_dealer_login_found_by_the_message", "SBIN", "NSE", {LTP: [fail(200, "You must provide the Client Code", "MO9999"), quote()]}),
    one("quote_dealer_login_refused_again", "SBIN", "NSE", {LTP: fail(200, "Please provide client code", "MO1062")}),
    one("quote_message_without_provide_is_not_a_dealer_login", "SBIN", "NSE", {LTP: fail(200, "Invalid client code", "MO2222")}),
    one("quote_nse_index", "NIFTY", "NSE_INDEX", {INDEX: index_quote()}),
    one("quote_bse_index", "SENSEX", "BSE_INDEX", {INDEX: index_quote(999912)}),
    one("quote_index_picks_the_row_with_its_code", "NIFTY", "NSE_INDEX", {INDEX: ok(data=[{"scripcode": 26009, "ltp": 1.0}, {"scripcode": 26000, "ltp": 24100.25, "open": 24000.5, "high": 24150.25, "low": 23950.0, "close": 24010.75}])}),
    one("quote_index_with_no_matching_row_takes_the_first", "NIFTY", "NSE_INDEX", {INDEX: ok(data=[{"scripcode": 26009, "ltp": 1.5}])}),
    one("quote_index_data_as_an_object", "NIFTY", "NSE_INDEX", {INDEX: ok(data={"scripcode": 26000, "ltp": 24100.25})}),
    one("quote_index_figures_that_are_not_numbers", "NIFTY", "NSE_INDEX", {INDEX: ok(data=[{"scripcode": 26000, "ltp": "abc", "open": None, "high": "24150.5"}])}),
    one("quote_index_no_rows", "NIFTY", "NSE_INDEX", {INDEX: ok(data=[])}),
    one("quote_index_wrong_field_name_tries_the_other", "NIFTY", "NSE_INDEX", {INDEX: [fail(200, "Invalid Exchange Input Parameter", "MO1051"), index_quote()]}),
    one("quote_index_both_field_names_refused", "NIFTY", "NSE_INDEX", {INDEX: fail(200, "Invalid Exchange Input Parameter", "MO1051")}),
    one("quote_index_other_error_is_not_retried", "NIFTY", "NSE_INDEX", {INDEX: [fail(200, "Token expired", "MO8002"), index_quote()]}),
    one("quote_index_dealer_login", "NIFTY", "NSE_INDEX", {INDEX: [fail(200, "Please provide client code", "MO1062"), index_quote()]}),
    one("quote_mcx_index_is_not_supported", "CRUDEIDX", "MCX_INDEX", {}),
    one("quote_index_unknown", "NOPEIDX", "NSE_INDEX", {}),
    hist("history_today", "D", "2026-11-01", "2026-11-11", {LTP: quote()}),
    hist("history_today_only", "D", "2026-11-11", "2026-11-11", {LTP: quote()}),
    hist("history_one_day_alias", "1D", "2026-11-11", "2026-11-11", {LTP: quote()}),
    hist("history_range_that_ends_before_today", "D", "2026-10-01", "2026-11-10", {LTP: quote()}),
    hist("history_range_that_starts_after_today", "D", "2026-11-12", "2026-11-20", {LTP: quote()}),
    hist("history_intraday_is_refused", "5m", "2026-11-11", "2026-11-11", {LTP: quote()}),
    hist("history_weekly_is_refused", "W", "2026-11-11", "2026-11-11", {LTP: quote()}),
    hist("history_no_price_yet", "D", "2026-11-11", "2026-11-11", {LTP: quote(0, open=0, high=0, low=0, close=0)}),
    hist("history_open_only", "D", "2026-11-11", "2026-11-11", {LTP: quote(0, open=80100, high=0, low=0)}),
    hist("history_high_below_the_close_is_raised", "D", "2026-11-11", "2026-11-11", {LTP: quote(80525, open=80100, high=0, low=80000)}),
    hist("history_no_low_uses_the_smallest_price", "D", "2026-11-11", "2026-11-11", {LTP: quote(80525, open=80100, high=80900, low=0)}),
    hist("history_quote_refused", "D", "2026-11-11", "2026-11-11", {LTP: fail(200, "Token expired", "MO8002")}),
    hist("history_index", "D", "2026-11-11", "2026-11-11", {INDEX: index_quote()}, "NIFTY", "NSE_INDEX"),
    hist("history_future", "D", "2026-11-11", "2026-11-11", {LTP: quote(2401050, volume=500)}, "NIFTY25NOV26FUT", "NFO"),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}, "broker": {}},
]
