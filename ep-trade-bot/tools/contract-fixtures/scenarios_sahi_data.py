"""Sahi quotes, depth and several quotes: GET /v1/marketdata/quotes (prices in paise), the day's open, high, low and previous close from the daily candles, and
the option chain for option contracts. The clock is stopped at 2026-11-13 11:00 India time."""

from datetime import datetime as _datetime

import scenarios_sahi as s
from scenarios_sahi import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, req  # noqa: F401

BROKER = "sahi"
RECORDED_AT = "2026-11-13T11:00:00+05:30"
FREEZE_CLOCK = False
SYMBOLS = s.SYMBOLS + [
    {"symbol": "NIFTY25NOV2624000CE", "brsymbol": "NIFTY25NOV2624000CE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "70001", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
    {"symbol": "NIFTY25NOV2624000PE", "brsymbol": "NIFTY25NOV2624000PE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "70002", "expiry": "25-NOV-26", "strike": 24000.0, "lotsize": 75, "instrumenttype": "PE", "tick_size": 0.05},
    {"symbol": "NOEXPIRYCE", "brsymbol": "NOEXPIRYCE", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "70003", "expiry": "", "strike": 1.0, "lotsize": 75, "instrumenttype": "CE", "tick_size": 0.05},
    {"symbol": "SENSEX26NOV7800CE", "brsymbol": "SENSEX26NOV7800CE", "name": "SENSEX", "exchange": "BFO", "brexchange": "BFO", "token": "70004", "expiry": "26-NOV-26", "strike": 78000.0, "lotsize": 20, "instrumenttype": "CE", "tick_size": 0.05},
    {"symbol": "SENSEX", "brsymbol": "SENSEX", "name": "SENSEX", "exchange": "BSE_INDEX", "brexchange": "BSE_INDEX", "token": "26001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
]

NOW_MS = 1794547800000
TODAY, YESTERDAY, DAY_BEFORE = 1794541500000, 1794455100000, 1794368700000
QB, CB, OB = "GET /v1/marketdata/quotes", "GET /v1/marketdata/candles", "GET /v1/marketdata/optionchain"


def lv(price, qty):
    return {"price": price, "qty": qty}


def quote(ltp=80545, volume=1234567, oi=1500, buys=None, sells=None, **extra):
    d = {"last_trade_price": ltp, "volume_today": volume, "open_interest": oi,
         "best_buys": buys if buys is not None else [lv(80540 - 5 * i, 100 * (i + 1)) for i in range(5)],
         "best_sells": sells if sells is not None else [lv(80550 + 5 * i, 110 * (i + 1)) for i in range(5)]}
    d.update(extra)
    return d


def qr(**quotes):
    return {"body": {"data": {str(k): v for k, v in quotes.items()}}}


def candle(ts, o, h, l, c, v=1000):
    return [ts, o, h, l, c, v]


SBIN = qr(**{"1001": quote()})
DAILY_LIVE = {"body": {"candles": [candle(DAY_BEFORE, 79000, 80100, 78800, 79900), candle(YESTERDAY, 79900, 80500, 79500, 80395), candle(TODAY, 80245, 80945, 80045, 80500)]}}
DAILY_PRE_OPEN = {"body": {"candles": [candle(DAY_BEFORE, 79000, 80100, 78800, 79900), candle(YESTERDAY, 79900, 80500, 79500, 80395)]}}
DAILY_ONLY_TODAY = {"body": {"candles": [candle(TODAY, 80245, 80945, 80045, 80500)]}}
DAILY_NONE = {"body": {"candles": []}}
DAILY_FAILS = {"status": 500, "body": {"message": "down"}}
REFUSED_401 = {"status": 401, "body": {"message": "x"}}
REFUSED_403 = {"status": 403, "body": {"message": "x"}}
REFUSED_MSG = {"status": 400, "body": {"message": "Too many instruments"}}
REFUSED_BARE = {"status": 502, "body": {"x": 1}}
EMPTY = {"body": {"data": {}}}

SHORT = qr(**{"1001": quote(buys=[lv(80540, 100)], sells=[])})
NO_BOOK = qr(**{"1001": quote(buys=[], sells=[])})
NULL_FIELDS = qr(**{"1001": {"last_trade_price": 80545, "volume_today": None, "open_interest": None}})
TWENTY = qr(**{"1001": quote(buys=[lv(80540 - i, 10) for i in range(20)], sells=[lv(80550 + i, 10) for i in range(20)])})


def leg(iid, ltp, bid, ask, prev, oi=900, vol=5000):
    return {"instrument_id": iid, "last_trade_price": ltp, "top_bid_price": bid, "top_ask_price": ask, "prev_close_price": prev, "open_interest": oi, "volume_today": vol}


CHAIN = {"body": {"strikes": [{"strike": 24000, "ce": leg(70001, 7500, 7450, 7550, 7000), "pe": leg(70002, 4500, 4450, 4550, 5000)}, {"strike": 24100, "ce": leg(79999, 1, 1, 1, 1), "pe": None}]}}
CHAIN_BSE = {"body": {"strikes": [{"strike": 78000, "ce": leg(70004, 120000, 119000, 121000, 100000)}]}}
CHAIN_EMPTY = {"body": {"strikes": []}}
OPTION_QUOTES = qr(**{"70001": quote(7500), "70002": quote(4500)})

SCENARIOS = [
    req("quotes_equity_with_the_days_candles", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_LIVE}),
    req("quotes_before_the_open_has_no_day_figures", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_PRE_OPEN}),
    req("quotes_first_day_of_candles_has_no_previous_close", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_ONLY_TODAY}),
    req("quotes_no_candles", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_NONE}),
    req("quotes_candles_fail_so_the_day_figures_are_zero", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_FAILS}),
    req("quotes_short_book", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT, CB: DAILY_LIVE}),
    req("quotes_no_book", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_BOOK, CB: DAILY_LIVE}),
    req("quotes_missing_volume_and_open_interest", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NULL_FIELDS, CB: DAILY_LIVE}),
    req("quotes_futures", "/api/v1/quotes", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {QB: qr(**{"50001": quote(2400550)}), CB: DAILY_LIVE}),
    req("quotes_no_quote_for_the_instrument", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: EMPTY, CB: DAILY_LIVE}),
    req("quotes_session_expired", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED_401, CB: DAILY_LIVE}),
    req("quotes_key_without_market_data", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED_403, CB: DAILY_LIVE}),
    req("quotes_refused_with_a_message", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED_MSG, CB: DAILY_LIVE}),
    req("quotes_refused_without_a_message", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED_BARE, CB: DAILY_LIVE}),
    req("quotes_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_LIVE}),
    req("depth_five_levels", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_LIVE}),
    req("depth_twenty_levels_are_cut_to_five_but_totalled_in_full", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: TWENTY, CB: DAILY_LIVE}),
    req("depth_short_book_is_padded", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT, CB: DAILY_LIVE}),
    req("depth_no_book", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_BOOK, CB: DAILY_LIVE}),
    req("depth_before_the_open", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN, CB: DAILY_PRE_OPEN}),
    req("depth_no_quote_for_the_instrument", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: EMPTY, CB: DAILY_LIVE}),
    req("depth_refused", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: REFUSED_401, CB: DAILY_LIVE}),
    req("multiquotes_cash_and_futures", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}]},
        {QB: qr(**{"1001": quote(), "1002": quote(150000), "50001": quote(2400550)})}),
    req("multiquotes_one_missing_from_the_answer", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}]}, {QB: SBIN}),
    req("multiquotes_the_same_instrument_twice", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {QB: SBIN}),
    req("multiquotes_refused", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, {QB: REFUSED_401}),
    req("multiquotes_options_come_from_the_option_chain", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, {"symbol": "NIFTY25NOV2624000PE", "exchange": "NFO"}]},
        {OB: CHAIN, "GET /v1/marketdata/quotes": OPTION_QUOTES}),
    req("multiquotes_options_and_cash_together", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {OB: CHAIN, QB: SBIN}),
    req("multiquotes_a_bse_option_reads_the_bse_index", "/api/v1/multiquotes", {"symbols": [{"symbol": "SENSEX26NOV7800CE", "exchange": "BFO"}]}, {OB: CHAIN_BSE}),
    req("multiquotes_option_chain_without_the_strike_falls_back_to_quotes", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}]}, {OB: CHAIN_EMPTY, QB: qr(**{"70001": quote(7500)})}),
    req("multiquotes_option_chain_refused_falls_back_to_quotes", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTY25NOV2624000CE", "exchange": "NFO"}]}, {OB: REFUSED_401, QB: qr(**{"70001": quote(7500)})}),
    req("multiquotes_an_option_without_an_expiry_uses_quotes", "/api/v1/multiquotes", {"symbols": [{"symbol": "NOEXPIRYCE", "exchange": "NFO"}]}, {QB: EMPTY}),
    req("multiquotes_ids_are_asked_in_ascending_order", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "SBIN", "exchange": "NSE"}]},
        {QB: qr(**{"1001": quote(), "1002": quote(150000), "50001": quote(2400550)})}),
    req("multiquotes_more_than_a_hundred_go_in_batches", "/api/v1/multiquotes", {"symbols": [{"symbol": f"BULK{i}", "exchange": "NSE"} for i in range(130)]},
        {QB: [qr(**{str(3000 + i): quote(100000 + i) for i in range(100)}), qr(**{str(3000 + i): quote(100000 + i) for i in range(100, 130)})]}),
]


def patch():
    import importlib

    module = importlib.import_module("broker.sahi.api.data")
    fixed = _datetime.fromisoformat(RECORDED_AT)

    class _Frozen(_datetime):
        @classmethod
        def now(cls, tz=None):
            return fixed if tz is None else fixed.astimezone(tz)

    module.datetime = _Frozen
