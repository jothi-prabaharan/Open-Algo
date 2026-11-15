"""IndMoney quotes, depth and several quotes: GET /market/quotes/full, ltp and mkt with scrip codes (segment, an underscore and the security id; NIDX and BIDX for indices). A quote is the full answer, else the last price and the depth read separately; a batch of many is one full call, split in halves when IndMoney refuses it for one scrip it does not know."""

import scenarios_indmoney as s
from scenarios_indmoney import AUTH_TOKEN, ENV, READ_POSTS, SEED_API_KEY_ROW, ok  # noqa: F401

BROKER = "indmoney"
SYMBOLS = list(s.SYMBOLS) + [s._sym("RELIANCE", "RELIANCE", "NSE", 2885, "RELIANCE")] + [s._sym(f"BULK{i}", f"BULK{i}", "NSE", 800000 + i, f"BULK{i}") for i in range(505)] + [s._sym("SENSEX", "SENSEX", "BSE_INDEX", 51, "SENSEX", 1, "INDEX"), s._sym("USDINR25NOV26FUT", "USDINR-FUT", "CDS", 90001, "USDINR", 1000, "FUT")]
FULL, LTP_EP, MKT = "GET /market/quotes/full", "GET /market/quotes/ltp", "GET /market/quotes/mkt"


def depth(levels=5, nested=None, aggregate=None):
    rows = [{"buy": {"price": f"{805.4 - 0.05 * i:.2f}", "quantity": 100 * (i + 1)}, "sell": {"price": f"{805.5 + 0.05 * i:.2f}", "quantity": 110 * (i + 1)}} for i in range(levels)]
    body = {"depth": rows}
    if aggregate is not None:
        body["aggregate"] = aggregate
    return {nested: body} if nested else body


def quote(code="NSE_3045", **extra):
    q = {"live_price": 805.45, "day_open": 801.0, "day_high": 809.5, "day_low": 799.0, "volume": 1234567, "prev_close": 800.0, "oi": 0, "market_depth": depth(5, aggregate={"total_buy": "5000", "total_sell": "6000"})}
    q.update(extra)
    return ok({code: q})


def hist_rows(*rows):
    return ok({"candles": list(rows)})


def q(name, symbol, exchange, broker):
    return {"name": name, "path": "/api/v1/quotes", "request": {"symbol": symbol, "exchange": exchange}, "broker": broker, "ignore": []}


def d(name, symbol, exchange, broker):
    return {"name": name, "path": "/api/v1/depth", "request": {"symbol": symbol, "exchange": exchange}, "broker": broker, "ignore": []}


def m(name, symbols, broker):
    return {"name": name, "path": "/api/v1/multiquotes", "request": {"symbols": symbols}, "broker": broker, "ignore": []}


def pair(symbol, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange}


BAD = {"status": 400, "body": {"status": "error", "message": "Invalid scrip code"}}
FAIL = {"status": 400, "body": {"status": "error", "message": "boom"}}
BULK = [pair(f"BULK{i}") for i in range(505)]


def bulk_quotes(start, end):
    return ok({f"NSE_{800000 + i}": {"live_price": 100 + i, "day_open": 99, "day_high": 101, "day_low": 98, "volume": 10, "prev_close": 99} for i in range(start, end)})


SCENARIOS = [
    q("quote_full", "SBIN", "NSE", {FULL: quote()}),
    q("quote_full_without_depth", "SBIN", "NSE", {FULL: quote(market_depth=None)}),
    q("quote_full_depth_nested_under_the_scrip", "SBIN", "NSE", {FULL: quote(market_depth=depth(5, nested="NSE_3045"))}),
    q("quote_full_depth_with_one_level", "SBIN", "NSE", {FULL: quote(market_depth=depth(1))}),
    q("quote_full_with_the_ltp_key", "SBIN", "NSE", {FULL: ok({"NSE_3045": {"ltp": 806.5, "day_open": "1,801.5"}})}),
    q("quote_full_numbers_as_text", "SBIN", "NSE", {FULL: quote(live_price="1,805.45", day_open="n/a", volume="1,234", oi="")}),
    q("quote_full_the_open_interest_of_a_future", "NIFTY25NOV26FUT", "NFO", {FULL: quote("NFO_49500", oi=7000000)}),
    q("quote_full_open_interest_by_its_other_name", "NIFTY25NOV26FUT", "NFO", {FULL: quote("NFO_49500", open_interest=123)}),
    q("quote_bse", "TATAMOTORS", "BSE", {FULL: quote("BSE_500570")}),
    q("quote_an_index", "NIFTY", "NSE_INDEX", {FULL: quote("NIDX_26000")}),
    q("quote_a_bse_index", "SENSEX", "BSE_INDEX", {FULL: quote("BIDX_51")}),
    q("quote_a_currency_future", "USDINR25NOV26FUT", "CDS", {FULL: quote("CDS_90001")}),
    q("quote_mcx", "GOLDM25NOV26FUT", "MCX", {FULL: quote("MCX_80001")}),
    q("quote_falls_back_when_the_full_call_fails", "SBIN", "NSE", {FULL: FAIL, LTP_EP: ok({"NSE_3045": {"live_price": 805.45}}), MKT: ok({"NSE_3045": {"market_depth": depth(5)}})}),
    q("quote_falls_back_when_the_full_answer_has_no_prices", "SBIN", "NSE", {FULL: ok({"NSE_3045": {"volume": 5}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}}), MKT: ok({"NSE_3045": {"market_depth": depth(5)}})}),
    q("quote_falls_back_when_the_full_data_is_empty", "SBIN", "NSE", {FULL: ok({}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}}), MKT: ok({})}),
    q("quote_fallback_with_the_last_price_only", "SBIN", "NSE", {FULL: FAIL, LTP_EP: ok({"NSE_3045": {"live_price": 805.45}}), MKT: FAIL}),
    q("quote_fallback_with_the_depth_only", "SBIN", "NSE", {FULL: FAIL, LTP_EP: FAIL, MKT: ok({"NSE_3045": {"market_depth": depth(5)}})}),
    q("quote_everything_fails_is_zeros", "SBIN", "NSE", {FULL: FAIL, LTP_EP: FAIL, MKT: FAIL}),
    q("quote_http_500", "SBIN", "NSE", {FULL: {"status": 500, "body": {"x": 1}}, LTP_EP: {"status": 500, "body": {"x": 1}}, MKT: {"status": 500, "body": {"x": 1}}}),
    q("quote_not_json", "SBIN", "NSE", {FULL: {"status": 200, "body": "<html>"}, LTP_EP: {"status": 200, "body": "<html>"}, MKT: {"status": 200, "body": "<html>"}}),
    q("quote_success_flag_true", "SBIN", "NSE", {FULL: {"body": {"success": True, "data": {"NSE_3045": {"live_price": 805.45}}}}}),
    d("depth_full_and_mkt", "SBIN", "NSE", {FULL: quote(), MKT: ok({"NSE_3045": {"market_depth": depth(5, aggregate={"total_buy": "5,000", "total_sell": "6000"})}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_totals_missing_are_summed", "SBIN", "NSE", {FULL: quote(), MKT: ok({"NSE_3045": {"market_depth": depth(5)}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_with_two_levels_is_padded", "SBIN", "NSE", {FULL: quote(), MKT: ok({"NSE_3045": {"market_depth": depth(2)}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_with_seven_levels_keeps_five", "SBIN", "NSE", {FULL: quote(), MKT: ok({"NSE_3045": {"market_depth": depth(7)}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_nested_under_the_scrip", "SBIN", "NSE", {FULL: quote(), MKT: ok({"NSE_3045": {"market_depth": depth(5, nested="NSE_3045")}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_without_the_full_quote_takes_the_last_price", "SBIN", "NSE", {FULL: FAIL, MKT: ok({"NSE_3045": {"market_depth": depth(5)}}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_without_any_price_takes_the_best_bid", "SBIN", "NSE", {FULL: FAIL, MKT: ok({"NSE_3045": {"market_depth": depth(5)}}), LTP_EP: FAIL}),
    d("depth_empty_mkt_takes_the_full_quote", "SBIN", "NSE", {FULL: quote(), MKT: ok({}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_empty_mkt_and_no_full_takes_the_last_price", "SBIN", "NSE", {FULL: FAIL, MKT: ok({}), LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_empty_everything", "SBIN", "NSE", {FULL: FAIL, MKT: ok({}), LTP_EP: FAIL}),
    d("depth_mkt_fails", "SBIN", "NSE", {FULL: quote(), MKT: FAIL, LTP_EP: ok({"NSE_3045": {"live_price": 805.45}})}),
    d("depth_of_a_future", "NIFTY25NOV26FUT", "NFO", {FULL: quote("NFO_49500", oi=7000000), MKT: ok({"NFO_49500": {"market_depth": depth(5)}}), LTP_EP: ok({"NFO_49500": {"live_price": 24100}})}),
    d("depth_of_an_index", "NIFTY", "NSE_INDEX", {FULL: quote("NIDX_26000"), MKT: ok({"NIDX_26000": {"market_depth": depth(5)}}), LTP_EP: ok({"NIDX_26000": {"live_price": 24100}})}),
    m("several_quotes", [pair("SBIN"), pair("INFY")], {FULL: ok({"NSE_3045": {"live_price": 805.45, "day_open": 801, "day_high": 809.5, "day_low": 799, "volume": 5, "prev_close": 800, "oi": 0}, "NSE_1594": {"ltp": 1500.5, "day_open": 1490}})}),
    m("several_quotes_one_missing", [pair("SBIN"), pair("INFY")], {FULL: ok({"NSE_3045": {"live_price": 805.45}})}),
    m("several_quotes_one_without_prices", [pair("SBIN"), pair("INFY")], {FULL: ok({"NSE_3045": {"live_price": 805.45}, "NSE_1594": {"volume": 5}})}),
    m("several_quotes_on_several_exchanges", [pair("SBIN"), pair("NIFTY25NOV26FUT", "NFO"), pair("TATAMOTORS", "BSE"), pair("NIFTY", "NSE_INDEX"), pair("GOLDM25NOV26FUT", "MCX")],
      {FULL: ok({"NSE_3045": {"live_price": 805.45}, "NFO_49500": {"live_price": 24100, "oi": 7}, "BSE_500570": {"live_price": 500}, "NIDX_26000": {"live_price": 24000}, "MCX_80001": {"live_price": 75000}})}),
    m("several_quotes_a_bad_scrip_splits_the_batch", [pair("SBIN"), pair("INFY"), pair("RELIANCE")], {FULL: [BAD, ok({"NSE_3045": {"live_price": 805.45}}), BAD, BAD, BAD]}),
    m("several_quotes_the_bad_scrip_is_remembered", [pair("SBIN"), pair("INFY")], {FULL: [BAD, ok({"NSE_3045": {"live_price": 805.45}}), BAD]}),
    m("several_quotes_a_failure_that_is_not_a_bad_scrip_ends_the_search", [pair("SBIN"), pair("INFY")], {FULL: FAIL}),
    m("several_quotes_http_500", [pair("SBIN"), pair("INFY")], {FULL: {"status": 500, "body": {"x": 1}}}),
    m("several_quotes_with_nothing_coming_back", [pair("SBIN"), pair("INFY")], {FULL: ok({})}),
    m("five_hundred_and_five_quotes_go_in_two_batches", BULK, {FULL: [bulk_quotes(0, 500), bulk_quotes(500, 505)]}),
]


def before_each(sc):
    import importlib

    importlib.import_module("broker.indmoney.api.data")._BAD_SCRIP_CODES.clear()
    importlib.import_module("broker.indmoney.api.rate_limiter").time.sleep = lambda *a, **k: None
