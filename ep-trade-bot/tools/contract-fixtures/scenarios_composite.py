"""Composite endpoints recorded on Zerodha: symbol master reads, option symbol
resolution, and the order endpoints that place several orders (basket, split,
options, options multi-order).

These endpoints are the same for every broker apart from the broker's own
quote and place-order calls, which the Zerodha fixtures already cover.

Expiries are far in the future (2030) or long past (2024), so the
"unexpired only" filters give the same answer whenever the tests run.
"""

BROKER = "zerodha"
# The Greeks depend on time to expiry, so the clock the option maths reads is pinned to this instant (Wednesday 11 November 2026, 10:00 India time) and the replay's clock is pinned to the same.
RECORDED_AT = "2026-11-11T10:00:00+05:30"
FREEZE_CLOCK = True


def _opt(strike, kind, expiry="30-DEC-30", code="30DEC30", br="30DEC", series=9):
    s = f"{strike:g}"
    return {
        "symbol": f"NIFTY{code}{s}{kind}", "brsymbol": f"NIFTY{br}{s}{kind}", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO",
        # A token is unique on its exchange, as at every broker.
        "token": f"{series}{int(strike)}{1 if kind == 'CE' else 2}::::{series}{int(strike)}", "expiry": expiry, "strike": float(strike),
        "lotsize": 75, "instrumenttype": kind, "tick_size": 0.05,
    }


SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "779521::::3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "408065::::1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY", "brsymbol": "NIFTY 50", "name": "NIFTY 50", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "256265::::1001", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
    {"symbol": "NIFTYNXT50", "brsymbol": "NIFTY NEXT 50", "name": "NIFTY NEXT 50", "exchange": "NSE_INDEX", "brexchange": "NSE", "token": "270857::::1057", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "INDEX", "tick_size": 0.05},
    {"symbol": "NIFTY30DEC30FUT", "brsymbol": "NIFTY30DECFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "13300001::::51801", "expiry": "30-DEC-30", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUT", "tick_size": 0.1},
    {"symbol": "NIFTY26DEC24FUT", "brsymbol": "NIFTY24DECFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "13200001::::51701", "expiry": "26-DEC-24", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUT", "tick_size": 0.1},
    *[_opt(k, t) for k in (23800, 23900, 24000, 24100, 24200) for t in ("CE", "PE")],
    _opt(24000, "CE", expiry="26-DEC-24", code="26DEC24", br="24DEC", series=8),
    {"symbol": "CRUDEOIL18DEC30FUT", "brsymbol": "CRUDEOIL30DECFUT", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000001::::470001", "expiry": "18-DEC-30", "strike": -1.0, "lotsize": 100, "instrumenttype": "FUT", "tick_size": 1.0},
    {"symbol": "CRUDEOIL19NOV30FUT", "brsymbol": "CRUDEOIL30NOVFUT", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000002::::470002", "expiry": "19-NOV-30", "strike": -1.0, "lotsize": 100, "instrumenttype": "FUT", "tick_size": 1.0},
    # A smaller contract expiring first, and an expired one: neither may price CRUDEOIL options.
    {"symbol": "CRUDEOILM17NOV30FUT", "brsymbol": "CRUDEOILM30NOVFUT", "name": "CRUDEOILM", "exchange": "MCX", "brexchange": "MCX", "token": "70000007::::470007", "expiry": "17-NOV-30", "strike": -1.0, "lotsize": 10, "instrumenttype": "FUT", "tick_size": 1.0},
    {"symbol": "CRUDEOIL18OCT24FUT", "brsymbol": "CRUDEOIL24OCTFUT", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000008::::470008", "expiry": "18-OCT-24", "strike": -1.0, "lotsize": 100, "instrumenttype": "FUT", "tick_size": 1.0},
    {"symbol": "CRUDEOILM18DEC30FUT", "brsymbol": "CRUDEOILM30DECFUT", "name": "CRUDEOILM", "exchange": "MCX", "brexchange": "MCX", "token": "70000003::::470003", "expiry": "18-DEC-30", "strike": -1.0, "lotsize": 10, "instrumenttype": "FUT", "tick_size": 1.0},
    {"symbol": "CRUDEOIL18DEC246000CE", "brsymbol": "CRUDEOIL24DEC6000CE", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000004::::470004", "expiry": "18-DEC-24", "strike": 6000.0, "lotsize": 100, "instrumenttype": "CE", "tick_size": 0.1},
    {"symbol": "CRUDEOIL18NOV306000CE", "brsymbol": "CRUDEOIL30NOV6000CE", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000005::::470005", "expiry": "18-NOV-30", "strike": 6000.0, "lotsize": 100, "instrumenttype": "CE", "tick_size": 0.1},
    {"symbol": "CRUDEOIL18NOV306100CE", "brsymbol": "CRUDEOIL30NOV6100CE", "name": "CRUDEOIL", "exchange": "MCX", "brexchange": "MCX", "token": "70000006::::470006", "expiry": "18-NOV-30", "strike": 6100.0, "lotsize": 100, "instrumenttype": "CE", "tick_size": 0.1},
]

FREEZE = [
    {"exchange": "NFO", "symbol": "NIFTY", "freeze_qty": 1800},
    {"exchange": "NFO", "symbol": "NIFTYNXT50", "freeze_qty": 600},
    {"exchange": "MCX", "symbol": "CRUDEOIL", "freeze_qty": 10000},
]

ORDER_OK = {"body": {"status": "success", "data": {"order_id": "151220000000000"}}}
ORDER_REFUSED = {"status": 400, "body": {"status": "error", "message": "Insufficient funds. Required margin is 8054.50 but available margin is 100.00.", "error_type": "InputException"}}


def _quote(key, ltp):
    return {"body": {"status": "success", "data": {key: {"instrument_token": 1, "last_price": ltp, "last_quantity": 1, "volume": 10, "oi": 0, "ohlc": {"open": ltp, "high": ltp, "low": ltp, "close": ltp}, "depth": {"buy": [], "sell": []}}}}}


NIFTY_24040 = _quote("NSE:NIFTY 50", 24040.35)
NIFTY_24050 = _quote("NSE:NIFTY 50", 24050.0)
NIFTY_24150 = _quote("NSE:NIFTY 50", 24150.0)
CRUDE_6080 = _quote("MCX:CRUDEOIL30NOVFUT", 6080.0)

def _quotes(prices):
    """One Kite /quote reply for several instruments: {"NFO:NIFTY30DEC24000CE": (ltp, oi), ...}."""
    data = {}
    for key, (ltp, oi) in prices.items():
        data[key] = {"instrument_token": 1, "last_price": ltp, "last_quantity": 1, "volume": 1000, "oi": oi,
                     "ohlc": {"open": ltp + 1, "high": ltp + 5, "low": ltp - 5, "close": ltp - 2},
                     "depth": {"buy": [{"price": ltp - 0.05, "quantity": 75, "orders": 1}], "sell": [{"price": ltp + 0.05, "quantity": 150, "orders": 2}]}}
    return {"body": {"status": "success", "data": data}}


def _k(strike, kind):
    return f"NFO:NIFTY30DEC{strike}{kind}"


CHAIN_QUOTES = _quotes({_k(23900, "CE"): (180.5, 1000), _k(24000, "CE"): (120.25, 2000), _k(24100, "CE"): (75.0, 1500),
                        _k(23900, "PE"): (40.0, 900), _k(24000, "PE"): (80.15, 2500), _k(24100, "PE"): (135.0, 700)})
REFUSED_403 = {"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}

OPT = {"strategy": "t", "underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30"}

SCENARIOS = [
    # Symbol master.
    {"name": "symbol_option", "path": "/api/v1/symbol", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO"}},
    {"name": "symbol_equity_no_freeze", "path": "/api/v1/symbol", "request": {"symbol": "SBIN", "exchange": "NSE"}},
    {"name": "symbol_not_found", "path": "/api/v1/symbol", "request": {"symbol": "NOPE", "exchange": "NSE"}},
    {"name": "symbol_missing_exchange", "path": "/api/v1/symbol", "request": {"symbol": "SBIN"}},
    {"name": "symbol_invalid_key", "path": "/api/v1/symbol", "request": {"apikey": "wrong-key", "symbol": "SBIN", "exchange": "NSE"}},
    {"name": "search_ranked", "path": "/api/v1/search", "request": {"query": "nifty"}},
    {"name": "search_terms_and_strike", "path": "/api/v1/search", "request": {"query": "NIFTY 24000", "exchange": "NFO"}},
    {"name": "search_exchange_filter", "path": "/api/v1/search", "request": {"query": "NIFTY", "exchange": "NSE_INDEX"}},
    {"name": "search_by_name", "path": "/api/v1/search", "request": {"query": "state bank"}},
    {"name": "search_no_match", "path": "/api/v1/search", "request": {"query": "ZZZZ"}},
    {"name": "search_blank_query", "path": "/api/v1/search", "request": {"query": "   "}},
    {"name": "search_bad_exchange", "path": "/api/v1/search", "request": {"query": "SBIN", "exchange": "LSE"}},
    {"name": "expiry_options", "path": "/api/v1/expiry", "request": {"symbol": "NIFTY", "exchange": "NFO", "instrumenttype": "options"}},
    {"name": "expiry_futures", "path": "/api/v1/expiry", "request": {"symbol": "nifty", "exchange": "NFO", "instrumenttype": "futures"}},
    {"name": "expiry_mcx_futures_exact_base", "path": "/api/v1/expiry", "request": {"symbol": "CRUDEOIL", "exchange": "MCX", "instrumenttype": "futures"}},
    {"name": "expiry_none", "path": "/api/v1/expiry", "request": {"symbol": "SBIN", "exchange": "NFO", "instrumenttype": "futures"}},
    {"name": "expiry_bad_type", "path": "/api/v1/expiry", "request": {"symbol": "NIFTY", "exchange": "NFO", "instrumenttype": "swaps"}},
    {"name": "expiry_equity_exchange", "path": "/api/v1/expiry", "request": {"symbol": "SBIN", "exchange": "NSE", "instrumenttype": "futures"}},
    {"name": "instruments_mcx_json", "method": "GET", "path": "/api/v1/instruments", "request": {"exchange": "MCX"}},
    {"name": "instruments_none", "method": "GET", "path": "/api/v1/instruments", "request": {"exchange": "CDS"}},
    {"name": "instruments_no_key", "method": "GET", "path": "/api/v1/instruments", "request": {"exchange": "MCX"}, "auth": "none"},
    {"name": "instruments_bad_exchange", "method": "GET", "path": "/api/v1/instruments", "request": {"exchange": "LSE"}},
    # Option symbol resolution.
    {"name": "optionsymbol_atm_ce", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_itm1_ce", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "ITM1", "option_type": "ce"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_otm2_pe", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "OTM2", "option_type": "PE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_out_of_range", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "OTM3", "option_type": "PE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_strike_int_half_even", "path": "/api/v1/optionsymbol", "request": {**OPT, "strike_int": 100, "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": NIFTY_24050}},
    {"name": "optionsymbol_strike_int_itm2_pe", "path": "/api/v1/optionsymbol", "request": {**OPT, "strike_int": 100, "offset": "ITM2", "option_type": "PE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_strike_int_not_listed", "path": "/api/v1/optionsymbol", "request": {**OPT, "strike_int": 50, "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": NIFTY_24050}},
    {"name": "optionsymbol_embedded_expiry", "path": "/api/v1/optionsymbol", "request": {"underlying": "NIFTY30DEC30FUT", "exchange": "NFO", "offset": "OTM1", "option_type": "CE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_no_expiry", "path": "/api/v1/optionsymbol", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "offset": "ATM", "option_type": "CE"}},
    {"name": "optionsymbol_no_strikes", "path": "/api/v1/optionsymbol", "request": {**OPT, "expiry_date": "29NOV30", "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsymbol_mcx_near_future", "path": "/api/v1/optionsymbol", "request": {"underlying": "CRUDEOIL", "exchange": "MCX", "expiry_date": "18NOV30", "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": CRUDE_6080}},
    {"name": "optionsymbol_mcx_no_future", "path": "/api/v1/optionsymbol", "request": {"underlying": "GOLD", "exchange": "MCX", "expiry_date": "18NOV30", "offset": "ATM", "option_type": "CE"}},
    {"name": "optionsymbol_quote_refused", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "ATM", "option_type": "CE"}, "broker": {"GET /quote": {"status": 403, "body": {"status": "error", "message": "Incorrect `api_key` or `access_token`.", "error_type": "TokenException"}}}},
    {"name": "optionsymbol_unknown_underlying", "path": "/api/v1/optionsymbol", "request": {"underlying": "FOO", "exchange": "NSE", "expiry_date": "30DEC30", "offset": "ATM", "option_type": "CE"}},
    {"name": "quotes_unknown_symbol", "path": "/api/v1/quotes", "request": {"symbol": "FOO", "exchange": "NSE"}},
    {"name": "depth_unknown_symbol", "path": "/api/v1/depth", "request": {"symbol": "FOO", "exchange": "NFO"}},
    {"name": "optionsymbol_bad_offset", "path": "/api/v1/optionsymbol", "request": {**OPT, "offset": "ATM5", "option_type": "CE"}},
    # Several quotes at once, and what is built on them.
    {"name": "multiquotes_with_unknown", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}, {"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "NIFTY", "exchange": "NSE_INDEX"}, {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO"}]},
     "broker": {"GET /quote": _quotes({"NSE:SBIN": (805.45, 0), "NSE:NIFTY 50": (24040.35, 0)})}},
    {"name": "multiquotes_all_unknown", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "NOPE", "exchange": "NSE"}, {"symbol": "ZZZ", "exchange": "BSE"}]}},
    {"name": "multiquotes_refused", "path": "/api/v1/multiquotes", "request": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}]}, "broker": {"GET /quote": REFUSED_403}},
    {"name": "multiquotes_empty", "path": "/api/v1/multiquotes", "request": {"symbols": []}},
    {"name": "syntheticfuture", "path": "/api/v1/syntheticfuture", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30"},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "syntheticfuture_no_strikes", "path": "/api/v1/syntheticfuture", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "29NOV30"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "syntheticfuture_option_unpriced", "path": "/api/v1/syntheticfuture", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30"},
     "broker": {"GET /quote": [NIFTY_24040, _quotes({_k(24000, "CE"): (120.25, 0)})]}},
    {"name": "optionchain_three_strikes", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30", "strike_count": 1},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "optionchain_whole", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30"},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "optionchain_embedded_expiry", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY30DEC30FUT", "exchange": "NFO", "expiry_date": "26DEC24", "strike_count": 1},
     "broker": {"GET /quote": [NIFTY_24150, CHAIN_QUOTES]}},
    {"name": "optionchain_mcx", "path": "/api/v1/optionchain", "request": {"underlying": "CRUDEOIL", "exchange": "MCX", "expiry_date": "18NOV30", "strike_count": 5},
     "broker": {"GET /quote": [CRUDE_6080, _quotes({"MCX:CRUDEOIL30NOV6000CE": (110.0, 5000)})]}},
    {"name": "optionchain_mcx_no_future", "path": "/api/v1/optionchain", "request": {"underlying": "GOLD", "exchange": "MCX", "expiry_date": "18NOV30"}},
    {"name": "optionchain_no_strikes", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "29NOV30"}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionchain_quote_refused", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30", "strike_count": 1}, "broker": {"GET /quote": REFUSED_403}},
    {"name": "optionchain_option_quotes_refused", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30", "strike_count": 1},
     "broker": {"GET /quote": [NIFTY_24040, REFUSED_403]}},
    {"name": "optionchain_with_greeks", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30", "strike_count": 1, "with_greeks": True},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "optionchain_bad_count", "path": "/api/v1/optionchain", "request": {"underlying": "NIFTY", "exchange": "NSE_INDEX", "expiry_date": "30DEC30", "strike_count": 0}},
    # Greeks of one option and of several: the forward is the synthetic future unless one is given, the spot otherwise.
    {"name": "optiongreeks_given_forward", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "forward_price": 24100.5},
     "broker": {"GET /quote": _quotes({_k(24000, "CE"): (120.25, 2000)})}},
    {"name": "optiongreeks_synthetic_forward", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000PE", "exchange": "NFO"},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES, _quotes({_k(24000, "PE"): (80.15, 2500)})]}},
    {"name": "optiongreeks_rate", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "forward_price": 24100.5, "interest_rate": 6.5},
     "broker": {"GET /quote": _quotes({_k(24000, "CE"): (120.25, 2000)})}},
    {"name": "optiongreeks_spot_when_no_synthetic", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024100CE", "exchange": "NFO"},
     "broker": {"GET /quote": [NIFTY_24040, REFUSED_403, NIFTY_24040, _quotes({_k(24100, "CE"): (75.0, 1500)})]}},
    {"name": "optiongreeks_explicit_underlying", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "underlying_symbol": "NIFTY30DEC30FUT", "underlying_exchange": "NFO"},
     "broker": {"GET /quote": [_quote("NFO:NIFTY30DECFUT", 24120.0), _quotes({_k(24000, "CE"): (120.25, 2000)})]}},
    {"name": "optiongreeks_deep_in_the_money", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3023800CE", "exchange": "NFO", "forward_price": 24100.5},
     "broker": {"GET /quote": _quotes({_k(23800, "CE"): (300.5, 100)})}},
    {"name": "optiongreeks_expired", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY26DEC2424000CE", "exchange": "NFO", "forward_price": 24100.5},
     "broker": {"GET /quote": _quotes({"NFO:NIFTY24DEC24000CE": (120.25, 2000)})}},
    {"name": "optiongreeks_bad_symbol", "path": "/api/v1/optiongreeks", "request": {"symbol": "NOTANOPTION", "exchange": "NFO", "forward_price": 24100.5}},
    {"name": "optiongreeks_bad_expiry_time", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "forward_price": 24100.5, "expiry_time": "25:99"},
     "broker": {"GET /quote": _quotes({_k(24000, "CE"): (120.25, 2000)})}},
    {"name": "optiongreeks_option_quote_refused", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "forward_price": 24100.5}, "broker": {"GET /quote": REFUSED_403}},
    {"name": "optiongreeks_no_option_price", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "forward_price": 24100.5}, "broker": {"GET /quote": _quotes({_k(24000, "CE"): (0, 0)})}},
    {"name": "optiongreeks_underlying_quote_refused", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "underlying_symbol": "FOO", "underlying_exchange": "NSE"}},
    {"name": "optiongreeks_bad_exchange", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NSE"}},
    {"name": "optiongreeks_bad_rate", "path": "/api/v1/optiongreeks", "request": {"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO", "interest_rate": 150}},
    {"name": "optiongreeks_missing_symbol", "path": "/api/v1/optiongreeks", "request": {"exchange": "NFO"}},
    {"name": "multioptiongreeks", "path": "/api/v1/multioptiongreeks",
     "request": {"symbols": [{"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO"}, {"symbol": "NIFTY30DEC3024100PE", "exchange": "NFO"}, {"symbol": "NIFTY30DEC3023900CE", "exchange": "NFO"}]},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES, NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "multioptiongreeks_partial", "path": "/api/v1/multioptiongreeks",
     "request": {"symbols": [{"symbol": "NIFTY30DEC3024000CE", "exchange": "NFO"}, {"symbol": "BROKEN", "exchange": "NFO"}, {"symbol": "NIFTY30DEC3024200CE", "exchange": "NFO"}], "interest_rate": 6.5},
     "broker": {"GET /quote": [NIFTY_24040, CHAIN_QUOTES, NIFTY_24040, CHAIN_QUOTES]}},
    {"name": "multioptiongreeks_all_fail", "path": "/api/v1/multioptiongreeks", "request": {"symbols": [{"symbol": "BROKEN", "exchange": "NFO"}, {"symbol": "ALSOBROKEN", "exchange": "NFO"}]}},
    {"name": "multioptiongreeks_expired_leg", "path": "/api/v1/multioptiongreeks",
     "request": {"symbols": [{"symbol": "NIFTY26DEC2424000CE", "exchange": "NFO"}]},
     "broker": {"GET /quote": [NIFTY_24040, NIFTY_24040, _quotes({"NFO:NIFTY24DEC24000CE": (120.25, 2000)})]}},
    {"name": "multioptiongreeks_empty", "path": "/api/v1/multioptiongreeks", "request": {"symbols": []}},
    {"name": "multioptiongreeks_bad_leg", "path": "/api/v1/multioptiongreeks", "request": {"symbols": [{"symbol": "NIFTY30DEC3024000CE", "exchange": "NSE"}]}},
    # Orders made of several orders.
    {"name": "basketorder_two", "path": "/api/v1/basketorder", "request": {"strategy": "t", "orders": [{"symbol": "INFY", "exchange": "NSE", "action": "sell", "quantity": 5, "pricetype": "MARKET", "product": "CNC"}, {"symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "pricetype": "LIMIT", "product": "MIS", "price": 800}]}, "broker": {"POST /orders/regular": ORDER_OK}},
    {"name": "basketorder_refused", "path": "/api/v1/basketorder", "request": {"strategy": "t", "orders": [{"symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": 1}]}, "broker": {"POST /orders/regular": ORDER_REFUSED}},
    {"name": "basketorder_empty", "path": "/api/v1/basketorder", "request": {"strategy": "t", "orders": []}},
    {"name": "basketorder_bad_item", "path": "/api/v1/basketorder", "request": {"strategy": "t", "orders": [{"symbol": "SBIN", "exchange": "NSE", "action": "HOLD", "quantity": 1}]}},
    {"name": "splitorder_remainder", "path": "/api/v1/splitorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": 25, "splitsize": 10, "pricetype": "MARKET", "product": "MIS"}, "broker": {"POST /orders/regular": [ORDER_OK, ORDER_REFUSED, ORDER_OK]}},
    {"name": "splitorder_exact", "path": "/api/v1/splitorder", "request": {"strategy": "t", "symbol": "INFY", "exchange": "NSE", "action": "SELL", "quantity": "20", "splitsize": "10", "product": "CNC"}, "broker": {"POST /orders/regular": ORDER_OK}},
    {"name": "splitorder_too_many", "path": "/api/v1/splitorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": 1001, "splitsize": 10}},
    {"name": "splitorder_zero_size", "path": "/api/v1/splitorder", "request": {"strategy": "t", "symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": 10, "splitsize": 0}},
    {"name": "optionsorder_atm", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "ATM", "option_type": "CE", "action": "BUY", "quantity": 75, "pricetype": "MARKET", "product": "NRML"}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": ORDER_OK}},
    {"name": "optionsorder_split", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "OTM1", "option_type": "PE", "action": "SELL", "quantity": 160, "splitsize": 75}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": [ORDER_OK, ORDER_OK, ORDER_REFUSED]}},
    {"name": "optionsorder_refused", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "ATM", "option_type": "PE", "action": "BUY", "quantity": 75}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": ORDER_REFUSED}},
    {"name": "optionsorder_unresolved", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "ITM4", "option_type": "CE", "action": "BUY", "quantity": 75}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsorder_bad_offset", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "MIDDLE", "option_type": "CE", "action": "BUY", "quantity": 75}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsorder_cnc_refused", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "ATM", "option_type": "CE", "action": "BUY", "quantity": 75, "product": "CNC"}},
    {"name": "optionsorder_split_too_many", "path": "/api/v1/optionsorder", "request": {**OPT, "offset": "ATM", "option_type": "CE", "action": "BUY", "quantity": 7600, "splitsize": 75}, "broker": {"GET /quote": NIFTY_24040}},
    {"name": "optionsmultiorder_iron_condor", "path": "/api/v1/optionsmultiorder", "request": {**OPT, "legs": [
        {"offset": "OTM1", "option_type": "CE", "action": "SELL", "quantity": 75},
        {"offset": "OTM2", "option_type": "CE", "action": "BUY", "quantity": 75},
        {"offset": "OTM1", "option_type": "PE", "action": "SELL", "quantity": 75, "product": "NRML"},
        {"offset": "OTM2", "option_type": "PE", "action": "BUY", "quantity": 75},
    ]}, "broker": {"GET /quote": NIFTY_24150, "POST /orders/regular": ORDER_OK}},
    {"name": "optionsmultiorder_leg_fails", "path": "/api/v1/optionsmultiorder", "request": {**OPT, "legs": [
        {"offset": "ATM", "option_type": "CE", "action": "SELL", "quantity": 75},
        {"offset": "OTM5", "option_type": "CE", "action": "BUY", "quantity": 75},
    ]}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": ORDER_REFUSED}},
    {"name": "optionsmultiorder_split_leg", "path": "/api/v1/optionsmultiorder", "request": {**OPT, "legs": [
        {"offset": "ATM", "option_type": "CE", "action": "BUY", "quantity": 150, "splitsize": 75},
    ]}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": ORDER_OK}},
    {"name": "optionsmultiorder_leg_expiry", "path": "/api/v1/optionsmultiorder", "request": {**OPT, "legs": [
        {"offset": "ATM", "option_type": "CE", "action": "BUY", "quantity": 75, "expiry_date": "29NOV30"},
        {"offset": "ATM", "option_type": "PE", "action": "SELL", "quantity": 75},
    ]}, "broker": {"GET /quote": NIFTY_24040, "POST /orders/regular": ORDER_OK}},
    {"name": "optionsmultiorder_no_legs", "path": "/api/v1/optionsmultiorder", "request": {**OPT, "legs": []}},
]
