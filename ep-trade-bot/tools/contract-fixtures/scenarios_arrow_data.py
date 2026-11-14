"""Arrow quotes, depth, several quotes and history: POST /info/quote/full (prices in paise), POST /info/quotes/full for batches of 100, indices under the INDEX exchange with a search for the
name Arrow accepts, and candles from the historical host."""

import scenarios_arrow as s
from scenarios_arrow import AUTH_TOKEN, ENV, SEED_API_KEY_ROW, req  # noqa: F401

BROKER = "arrow"


def idx(symbol, brsymbol, exchange, token):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0,
            "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05}


SYMBOLS = s.SYMBOLS + [
    idx("INDIAVIX", "India VIX", "NSE_INDEX", 26100), idx("NIFTYIT", "Nifty IT", "NSE_INDEX", 26101), idx("BANKNIFTY", "Nifty Bank", "NSE_INDEX", 26102),
    idx("FINNIFTY", "Nifty Fin Service", "NSE_INDEX", 26103), idx("SENSEX", "SENSEX", "BSE_INDEX", 26104), idx("MIDCPNIFTY", "NIFTY MID SELECT", "NSE_INDEX", 26105),
    idx("NIFTYNXT50", "Nifty Next 50", "NSE_INDEX", 26106), idx("SMLCAP", "SMLCAP", "BSE_INDEX", 26107), idx("NIFTYMEDIA", "Nifty Media", "NSE_INDEX", 26108),
    idx("NIFTYPSU", "Nifty PSU", "NSE_INDEX", 26109), idx("NIFTYAUTO", "Nifty Auto", "NSE_INDEX", 26110),
    idx("MCXCOMDEX", "MCX COMDEX", "MCX_INDEX", 26111), idx("NIFTYREALTY", "Nifty Realty", "NSE_INDEX", 26112),
    {"symbol": "CRUDEOIL25NOV26FUT", "brsymbol": "CRUDEOIL25NOV26FUT", "name": "CRUDEOIL", "exchange": "NCO", "brexchange": "NCO", "token": "95001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 100, "instrumenttype": "FUT", "tick_size": 1.0},
    {"symbol": "USDINR25NOV26FUT", "brsymbol": "USDINR25NOV26FUT", "name": "USDINR", "exchange": "CDS", "brexchange": "CDS", "token": "90001", "expiry": "25-NOV-26", "strike": -1.0, "lotsize": 1000, "instrumenttype": "FUT", "tick_size": 0.25},
    {"symbol": "SENSEX26NOVFUT", "brsymbol": "SENSEX26NOVFUT", "name": "SENSEX", "exchange": "BFO", "brexchange": "BFO", "token": "60001", "expiry": "26-NOV-26", "strike": -1.0, "lotsize": 20, "instrumenttype": "FUT", "tick_size": 0.1},
]
SYMBOLS += [{"symbol": f"BULK{i}", "brsymbol": f"BULK{i}-EQ", "name": f"BULK{i}", "exchange": "NSE", "brexchange": "NSE", "token": str(3000 + i), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05} for i in range(130)]

QB, QUOTE_EP = "POST /info/quote/full", "POST /info/quote/ltp"
MB = "POST /info/quotes/full"


def lv(price, qty, orders=1):
    return {"price": price, "quantity": qty, "orders": orders}


def q(ltp=80545, **extra):
    d = {"ltp": ltp, "open": 80245, "high": 80945, "low": 80045, "close": 80395, "volume": 1234567, "oi": 1500, "ltq": 25, "totalBuyQty": 5000, "totalSellQty": 4000,
         "bids": [lv(80540 - 5 * i, 100 * (i + 1)) for i in range(5)], "asks": [lv(80550 + 5 * i, 110 * (i + 1)) for i in range(5)]}
    d.update(extra)
    return d


def ok(**data):
    return {"body": {"status": "success", "data": data}}


SBIN = ok(**q())
NO_BOOK = ok(**q(bids=[], asks=[]))
SHORT = ok(**q(bids=[lv(80540, 100)], asks=None))
SEVEN = ok(**q(bids=[lv(80540 - i, 10) for i in range(7)], asks=[lv(80550 + i, 10) for i in range(7)]))
TEXT = ok(**{"ltp": "80545", "open": "x", "high": None, "low": "80045", "close": "", "volume": "12", "oi": 0, "bids": [{"price": "80540", "quantity": "5"}], "asks": [{}]})
EMPTY = {"body": {"status": "success", "data": {}}}
NO_STATUS = {"body": {"data": q()}}
ERR_STATUS = {"body": {"status": "error", "message": "Quote request failed here"}}
ERR_NO_MESSAGE = {"body": {"status": "error"}}
R400 = {"status": 400, "body": {"status": "error", "message": "bad symbol"}}
R401 = {"status": 401, "body": {"status": "error", "message": "bad token"}}
R500 = {"status": 500, "body": {"status": "error", "message": "down"}}

SCENARIOS = [
    req("quotes_equity", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN}),
    req("quotes_futures", "/api/v1/quotes", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {QB: ok(**q(2400550))}),
    req("quotes_mcx_goes_as_mcxfo", "/api/v1/quotes", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {QB: ok(**q(7500000))}),
    req("quotes_bfo", "/api/v1/quotes", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {QB: SBIN}),
    req("quotes_no_book_is_zero", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_BOOK}),
    req("quotes_short_book", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT}),
    req("quotes_text_and_empty_numbers_are_zero", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: TEXT}),
    req("quotes_empty_answer", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: EMPTY}),
    req("quotes_answer_without_a_status", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_STATUS}),
    req("quotes_error_status_in_an_ok_answer", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: ERR_STATUS}),
    req("quotes_error_status_without_a_message", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: ERR_NO_MESSAGE}),
    req("quotes_refused", "/api/v1/quotes", {"symbol": "SBIN", "exchange": "NSE"}, {QB: R401}),
    req("quotes_exchange_arrow_does_not_serve", "/api/v1/quotes", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, {QB: SBIN}),
    req("quotes_index_first_name_accepted", "/api/v1/quotes", {"symbol": "BANKNIFTY", "exchange": "NSE_INDEX"}, {QB: ok(**q(5000000))}),
    req("quotes_index_second_name_after_a_400", "/api/v1/quotes", {"symbol": "INDIAVIX", "exchange": "NSE_INDEX"}, {QB: [R400, ok(**q(1500))]}),
    req("quotes_index_third_name_after_two_400s", "/api/v1/quotes", {"symbol": "NIFTYIT", "exchange": "NSE_INDEX"}, {QB: [R400, R400, ok(**q(3500000))]}),
    req("quotes_bse_index", "/api/v1/quotes", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {QB: ok(**q(7900000))}),
    req("quotes_index_every_name_refused", "/api/v1/quotes", {"symbol": "NIFTYMEDIA", "exchange": "NSE_INDEX"}, {QB: [R400, R400, R400]}),
    req("quotes_index_a_different_error_stops_the_search", "/api/v1/quotes", {"symbol": "NIFTYPSU", "exchange": "NSE_INDEX"}, {QB: [R500, ok(**q(1))]}),
    req("quotes_index_error_status_in_an_ok_answer_stops_the_search", "/api/v1/quotes", {"symbol": "NIFTYAUTO", "exchange": "NSE_INDEX"}, {QB: [ERR_STATUS, ok(**q(1))]}),
    req("quotes_mcx_index_goes_under_the_index_exchange", "/api/v1/quotes", {"symbol": "MCXCOMDEX", "exchange": "MCX_INDEX"}, {QB: ok(**q(1234500))}),
    req("quotes_nse_commodities_arrow_does_not_serve", "/api/v1/quotes", {"symbol": "CRUDEOIL25NOV26FUT", "exchange": "NCO"}, {QB: SBIN}),
    req("quotes_unknown_symbol", "/api/v1/quotes", {"symbol": "NOPE", "exchange": "NSE"}, {QB: SBIN}),
    req("depth_five_levels", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SBIN}),
    req("depth_seven_levels_are_cut_to_five", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SEVEN}),
    req("depth_short_book_is_padded", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: SHORT}),
    req("depth_no_book", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: NO_BOOK}),
    req("depth_text_numbers", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: TEXT}),
    req("depth_refused", "/api/v1/depth", {"symbol": "SBIN", "exchange": "NSE"}, {QB: R401}),
    req("depth_unsupported_exchange", "/api/v1/depth", {"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, {QB: SBIN}),
]


def row(token, **extra):
    return {"token": token, **q(), **extra}


def batch(*rows):
    return {"body": {"status": "success", "data": list(rows)}}


MULTI = {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "INFY", "exchange": "NSE"}, {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}]}
SCENARIOS += [
    req("multiquotes_cash_and_futures", "/api/v1/multiquotes", MULTI, {MB: batch(row(1001), row(1002, ltp=150000), row(50001, ltp=2400550))}),
    req("multiquotes_one_missing_from_the_answer", "/api/v1/multiquotes", {"symbols": MULTI["symbols"][:1] + MULTI["symbols"][2:3]}, {MB: batch(row(1001))}),
    req("multiquotes_a_failing_batch_leaves_errors_not_nothing", "/api/v1/multiquotes", {"symbols": MULTI["symbols"][:1] + MULTI["symbols"][2:3]}, {MB: R500}),
    req("multiquotes_the_same_instrument_twice_is_one_entry", "/api/v1/multiquotes", {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {MB: batch(row(1001))}),
    req("multiquotes_mcx_goes_as_mcxfo", "/api/v1/multiquotes", {"symbols": [{"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {MB: batch(row(80001), row(1001))}),
    req("multiquotes_exchange_arrow_does_not_serve", "/api/v1/multiquotes", {"symbols": [{"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}, {"symbol": "SBIN", "exchange": "NSE"}]}, {MB: batch(row(1001))}),
    req("multiquotes_only_unserved_exchanges_send_nothing", "/api/v1/multiquotes", {"symbols": [{"symbol": "USDINR25NOV26FUT", "exchange": "CDS"}]}, {}),
    req("multiquotes_an_index_is_probed_for_its_name_first", "/api/v1/multiquotes", {"symbols": [{"symbol": "FINNIFTY", "exchange": "NSE_INDEX"}, {"symbol": "SBIN", "exchange": "NSE"}]},
        {QUOTE_EP: ok(**q(2000000)), MB: batch(row(26103, ltp=2000000), row(1001))}),
    req("multiquotes_an_index_arrow_does_not_serve_is_an_error_entry", "/api/v1/multiquotes", {"symbols": [{"symbol": "MIDCPNIFTY", "exchange": "NSE_INDEX"}, {"symbol": "SBIN", "exchange": "NSE"}]},
        {QUOTE_EP: [R400, R400, R400], MB: batch(row(1001))}),
    req("multiquotes_an_index_arrow_does_not_serve_asked_twice_probes_once", "/api/v1/multiquotes", {"symbols": [{"symbol": "NIFTYREALTY", "exchange": "NSE_INDEX"}, {"symbol": "NIFTYREALTY", "exchange": "NSE_INDEX"}, {"symbol": "SBIN", "exchange": "NSE"}]},
        {QUOTE_EP: [R400, R400, R400], MB: batch(row(1001))}),
    req("multiquotes_more_than_a_hundred_go_in_batches", "/api/v1/multiquotes", {"symbols": [{"symbol": f"BULK{i}", "exchange": "NSE"} for i in range(130)]},
        {MB: [batch(*[row(3000 + i, ltp=100000 + i) for i in range(100)]), batch(*[row(3000 + i, ltp=100000 + i) for i in range(100, 130)])]}),
]


def bar(ts, o=80000, h=80200, l=79900, c=80100, v=1000, *oi):
    return [ts, o, h, l, c, v, *oi]


HB = f"GET /candle/nse/1001/5min"


def hist(name, request, broker, **extra):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "5m", "start_date": "2026-11-02", "end_date": "2026-11-02"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": broker, **extra}


def lst(*rows):
    return {"body": list(rows)}


M5 = lst(bar("2026-11-02T09:15:00+0530"), bar("2026-11-02T09:20:00+0530", 80100, 80300, 80000, 80250, 2000), bar("2026-11-02T09:25:00+0530", 80250, 80400, 80150, 80300, 3000))
UNSORTED = lst(bar("2026-11-02T09:20:00+0530", c=2), bar("2026-11-02T09:15:00+0530", c=1), bar("2026-11-02T09:15:00+0530", c=9))
DAILY = lst(bar("2026-10-29T00:00:00+0530"), bar("2026-10-30T00:00:00+0530", 79900, 80500, 79500, 80395, 9000))
WITH_OI = lst(bar("2026-11-02T09:15:00+0530", 80000, 80200, 79900, 80100, 1000, 1500), bar("2026-11-02T09:20:00+0530", 80100, 80300, 80000, 80250, 2000, 1600))
NAIVE = lst(bar("2026-11-02T09:15:00"))
SCENARIOS += [
    hist("history_5m_equity", {}, {HB: M5}),
    hist("history_is_sorted_and_keeps_the_first_of_a_timestamp", {}, {HB: UNSORTED}),
    hist("history_empty", {}, {HB: lst()}),
    hist("history_timestamps_without_an_offset_are_read_as_utc", {}, {HB: NAIVE}),
    hist("history_futures_ask_for_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO"}, {f"GET /candle/nfo/50001/5min": WITH_OI}),
    hist("history_bfo_asks_for_open_interest", {"symbol": "SENSEX26NOVFUT", "exchange": "BFO"}, {f"GET /candle/bfo/60001/5min": WITH_OI}),
    hist("history_mcx", {"symbol": "GOLDM25NOV26FUT", "exchange": "MCX"}, {f"GET /candle/mcx/80001/5min": M5}),
    hist("history_index_uses_the_cash_exchange_path", {"symbol": "BANKNIFTY", "exchange": "NSE_INDEX"}, {f"GET /candle/nse/26102/5min": M5}),
    hist("history_bse_index", {"symbol": "SENSEX", "exchange": "BSE_INDEX"}, {f"GET /candle/bse/26104/5min": M5}),
    hist("history_1m", {"interval": "1m"}, {f"GET /candle/nse/1001/min": M5}),
    hist("history_3m", {"interval": "3m"}, {f"GET /candle/nse/1001/3min": M5}),
    hist("history_10m", {"interval": "10m"}, {f"GET /candle/nse/1001/10min": M5}),
    hist("history_15m", {"interval": "15m"}, {f"GET /candle/nse/1001/15min": M5}),
    hist("history_30m", {"interval": "30m"}, {f"GET /candle/nse/1001/30min": M5}),
    hist("history_1h", {"interval": "1h"}, {f"GET /candle/nse/1001/hour": M5}),
    hist("history_2h", {"interval": "2h"}, {f"GET /candle/nse/1001/2hours": M5}),
    hist("history_3h", {"interval": "3h"}, {f"GET /candle/nse/1001/3hours": M5}),
    hist("history_4h", {"interval": "4h"}, {f"GET /candle/nse/1001/4hours": M5}),
    hist("history_daily_is_shifted_to_the_india_date_at_midnight_utc", {"interval": "D", "start_date": "2026-10-29", "end_date": "2026-10-30"}, {f"GET /candle/nse/1001/day": DAILY}),
    hist("history_weekly", {"interval": "W", "start_date": "2026-10-26", "end_date": "2026-10-30"}, {f"GET /candle/nse/1001/week": DAILY}),
    hist("history_monthly", {"interval": "M", "start_date": "2026-10-01", "end_date": "2026-10-30"}, {f"GET /candle/nse/1001/month": DAILY}),
    hist("history_intraday_goes_in_chunks_of_sixty_days", {"start_date": "2026-09-01", "end_date": "2026-11-15"}, {HB: [M5, lst(bar("2026-11-02T09:30:00+0530"))]}),
    hist("history_daily_goes_in_chunks_of_two_thousand_days", {"interval": "D", "start_date": "2019-01-01", "end_date": "2026-10-30"}, {f"GET /candle/nse/1001/day": [DAILY, lst(bar("2026-10-31T00:00:00+0530")), lst(), lst()]}),
    hist("history_an_error_envelope_is_a_failure", {}, {HB: {"body": {"message": "no data for this range"}}}),
    hist("history_an_error_envelope_without_a_message", {}, {HB: {"body": {"x": 1}}}),
    hist("history_refused", {}, {HB: R401}),
    hist("history_a_later_chunk_fails", {"start_date": "2026-09-01", "end_date": "2026-11-15"}, {HB: [M5, R500]}),
    hist("history_weekly_goes_in_chunks_of_two_thousand_days", {"interval": "W", "start_date": "2019-01-01", "end_date": "2026-10-30"}, {f"GET /candle/nse/1001/week": [DAILY, lst(bar("2026-10-31T00:00:00+0530"))]}),
    hist("history_interval_arrow_lacks", {"interval": "20m"}, {HB: M5}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}, {HB: M5}),
    {"name": "intervals", "path": "/api/v1/intervals", "request": {}},
]
