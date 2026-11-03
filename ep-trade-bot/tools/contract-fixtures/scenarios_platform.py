"""Platform endpoints that need no broker call, recorded on Zerodha: the market
calendar (holidays and timings), chart preferences, and the sandbox day P&L by
symbol.

Recorded with the server in India's time zone, as OpenAlgo's deployments run:
market/timings builds midnight from the server's local clock.
"""

BROKER = "zerodha"
ENV = {"TZ": "Asia/Kolkata"}
INIT = [
    "database.market_calendar_db:ensure_market_calendar_tables_exists",
    "database.chart_prefs_db",
    "database.sandbox_db",
]

SYMBOLS = [
    {"symbol": "SBIN", "brsymbol": "SBIN", "name": "STATE BANK OF INDIA", "exchange": "NSE", "brexchange": "NSE", "token": "779521::::3045", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "INFY", "brsymbol": "INFY", "name": "INFOSYS", "exchange": "NSE", "brexchange": "NSE", "token": "408065::::1594", "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": "EQ", "tick_size": 0.05},
    {"symbol": "NIFTY30DEC30FUT", "brsymbol": "NIFTY30DECFUT", "name": "NIFTY", "exchange": "NFO", "brexchange": "NFO", "token": "13300001::::51801", "expiry": "30-DEC-30", "strike": -1.0, "lotsize": 75, "instrumenttype": "FUT", "tick_size": 0.1},
]


def patch():
    """The recording has no live prices: positions keep the prices stored with them."""
    import sandbox.position_manager as pm

    pm.PositionManager._update_positions_mtm = lambda self, positions: None
    pm.PositionManager._check_and_close_expired_positions = lambda self, positions: positions


def _pos(symbol, exchange, product, quantity, average, ltp, today_realized=0.0):
    return {
        "symbol": symbol, "exchange": exchange, "product": product, "quantity": quantity, "average_price": average, "ltp": ltp,
        "pnl": round((ltp - average) * quantity, 2) if quantity else 0.0, "today_realized_pnl": today_realized, "accumulated_realized_pnl": today_realized,
    }


H = "/api/v1/market/holidays"
T = "/api/v1/market/timings"
C = "/api/v1/chart"
P = "/api/v1/pnl/symbols"

SCENARIOS = [
    # ---- market holidays ----
    {"name": "holidays_2026", "path": H, "request": {"year": 2026}},
    {"name": "holidays_2025", "path": H, "request": {"year": 2025}},
    {"name": "holidays_year_without_calendar", "path": H, "request": {"year": 2031}},
    {"name": "holidays_year_as_string", "path": H, "request": {"year": "2026"}},
    {"name": "holidays_year_below_range", "path": H, "request": {"year": 2019}},
    {"name": "holidays_year_above_range", "path": H, "request": {"year": 2051}},
    {"name": "holidays_year_not_a_number", "path": H, "request": {"year": "next"}},
    {"name": "holidays_missing_apikey", "path": H, "auth": "none", "request": {"year": 2026}},
    {"name": "holidays_invalid_apikey", "path": H, "auth": "none", "request": {"apikey": "not-a-key", "year": 2026}},
    # ---- market timings ----
    {"name": "timings_trading_day", "path": T, "request": {"date": "2026-11-04"}},
    {"name": "timings_saturday", "path": T, "request": {"date": "2026-11-07"}},
    {"name": "timings_muhurat_sunday", "path": T, "request": {"date": "2026-11-08"}},
    {"name": "timings_holiday_mcx_evening", "path": T, "request": {"date": "2026-11-24"}},
    {"name": "timings_holiday_all_closed", "path": T, "request": {"date": "2026-12-25"}},
    {"name": "timings_2025_muhurat", "path": T, "request": {"date": "2025-10-21"}},
    {"name": "timings_far_future_day", "path": T, "request": {"date": "2049-06-15"}},
    {"name": "timings_day_first_format", "path": T, "request": {"date": "04-11-2026"}},
    {"name": "timings_impossible_date", "path": T, "request": {"date": "2026-02-30"}},
    {"name": "timings_before_range", "path": T, "request": {"date": "2019-12-31"}},
    {"name": "timings_after_range", "path": T, "request": {"date": "2051-01-01"}},
    {"name": "timings_missing_date", "path": T, "request": {}},
    {"name": "timings_invalid_apikey", "path": T, "auth": "none", "request": {"apikey": "not-a-key", "date": "2026-11-04"}},
    # ---- chart preferences ----
    {"name": "chart_get_none_saved", "method": "GET", "path": C, "request": {}, "chart_prefs": {}},
    {"name": "chart_get_saved", "method": "GET", "path": C, "request": {}, "chart_prefs": {"tv_theme": "dark", "tv_chart_layout": "{\"panes\":2}"}},
    {"name": "chart_get_missing_apikey", "method": "GET", "path": C, "auth": "none", "request": {}},
    {"name": "chart_get_invalid_apikey", "method": "GET", "path": C, "auth": "none", "request": {"apikey": "not-a-key"}},
    {"name": "chart_get_header_key", "method": "GET", "path": C, "auth": "header", "request": {}, "chart_prefs": {"tv_theme": "light"}},
    {"name": "chart_post_save", "path": C, "request": {"tv_theme": "dark", "tv_chart_layout": "{\"panes\":1}"}, "chart_prefs": {}},
    {"name": "chart_post_overwrite", "path": C, "request": {"tv_theme": "light"}, "chart_prefs": {"tv_theme": "dark"}},
    {"name": "chart_post_no_preferences", "path": C, "request": {}, "chart_prefs": {}},
    {"name": "chart_post_empty_body", "path": C, "auth": "none", "request": {}},
    {"name": "chart_post_missing_apikey", "path": C, "auth": "none", "request": {"tv_theme": "dark"}},
    {"name": "chart_post_invalid_apikey", "path": C, "auth": "none", "request": {"apikey": "not-a-key", "tv_theme": "dark"}, "chart_prefs": {}},
    {"name": "chart_post_too_many_keys", "path": C, "request": {f"k{i}": "v" for i in range(51)}, "chart_prefs": {}},
    # OpenAlgo checks the limits before the key.
    {"name": "chart_post_too_many_keys_invalid_apikey", "path": C, "auth": "none", "request": {"apikey": "not-a-key", **{f"k{i}": "v" for i in range(51)}}},
    {"name": "chart_post_fifty_keys", "path": C, "request": {f"k{i}": "v" for i in range(50)}, "chart_prefs": {}},
    {"name": "chart_post_key_too_long", "path": C, "request": {"x" * 51: "v"}, "chart_prefs": {}},
    {"name": "chart_post_value_too_large", "path": C, "request": {"layout": "a" * 1_048_575}, "chart_prefs": {}},
    {"name": "chart_post_number_value", "path": C, "request": {"zoom": 5}, "chart_prefs": {}},
    {"name": "chart_post_object_value", "path": C, "request": {"layout": {"panes": 2}}, "chart_prefs": {}},
    {"name": "chart_post_null_value", "path": C, "request": {"tv_theme": None}, "chart_prefs": {}},
    # ---- sandbox day P&L by symbol ----
    {"name": "pnl_symbols_live_mode", "path": P, "request": {}},
    {"name": "pnl_symbols_none_held", "path": P, "request": {}, "analyze": True, "sandbox_positions": []},
    {
        "name": "pnl_symbols_open_and_closed", "path": P, "request": {}, "analyze": True,
        "sandbox_positions": [
            _pos("SBIN", "NSE", "MIS", 10, 800.0, 812.5),
            _pos("INFY", "NSE", "MIS", 0, 1500.0, 1510.0, today_realized=250.0),
            _pos("NIFTY30DEC30FUT", "NFO", "NRML", -75, 24100.0, 24050.0, today_realized=-120.5),
        ],
    },
    {"name": "pnl_symbols_missing_apikey", "path": P, "auth": "none", "request": {}, "analyze": True},
]
