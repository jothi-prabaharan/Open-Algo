"""/ticker/EXCHANGE:SYMBOL on Zerodha: candles as JSON or as plain text lines (format=txt)."""

import scenarios_history as h

BROKER = "zerodha"
SYMBOLS = h.SYMBOLS
SBIN = h.SBIN
M5, DAILY, EMPTY, REFUSED = h.M5, h.DAILY, h.EMPTY, h.REFUSED

T = "/api/v1/ticker/NSE:SBIN"


def tick(name, request, broker, path=T, drop=(), **extra):
    base = {"interval": "5m", "from": "2026-11-02", "to": "2026-11-02"}
    return {"name": name, "method": "GET", "path": path, "request": {k: v for k, v in {**base, **request}.items() if k not in drop}, "broker": broker, **extra}


SCENARIOS = [
    tick("ticker_json_5m", {}, {SBIN + "5minute": M5}),
    tick("ticker_text_intraday", {"format": "txt"}, {SBIN + "5minute": M5}),
    tick("ticker_text_daily", {"interval": "D", "from": "2026-10-29", "to": "2026-10-30", "format": "txt"}, {SBIN + "day": DAILY}),
    tick("ticker_json_daily", {"interval": "D", "from": "2026-10-29", "to": "2026-10-30"}, {SBIN + "day": DAILY}),
    tick("ticker_text_nothing_traded", {"format": "txt"}, {SBIN + "5minute": EMPTY}),
    tick("ticker_text_upper_case_format", {"format": "TXT"}, {SBIN + "5minute": M5}),
    tick("ticker_interval_defaults_to_daily", {"from": "2026-10-29", "to": "2026-10-30"}, {SBIN + "day": DAILY}, drop=("interval",)),
    tick("ticker_text_futures_volume_and_open_interest", {"interval": "1m", "format": "txt"}, {"GET /instruments/historical/13238786/minute": h.FUT}, path="/api/v1/ticker/NFO:NIFTY25NOV26FUT"),
    tick("ticker_futures", {"interval": "1m"}, {"GET /instruments/historical/13238786/minute": h.FUT}, path="/api/v1/ticker/NFO:NIFTY25NOV26FUT"),
    # 60 days of 5 minute candles are asked for; only the last 30 are served.
    tick("ticker_intraday_window_held_to_30_days", {"from": "2026-09-03", "to": "2026-11-02"}, {SBIN + "5minute": M5}),
    # Eleven years of daily candles are asked for; ten are served (Kite takes 2000 days a request).
    tick("ticker_daily_window_held_to_10_years", {"interval": "D", "from": "2015-11-02", "to": "2026-11-02"}, {SBIN + "day": DAILY}),
    tick("ticker_missing_from", {"from": None}, {}),
    tick("ticker_bad_exchange", {}, {}, path="/api/v1/ticker/XYZ:SBIN"),
    tick("ticker_bad_interval", {"interval": "7m"}, {}),
    tick("ticker_symbol_without_exchange", {}, {SBIN + "5minute": M5}, path="/api/v1/ticker/SBIN"),
    tick("ticker_unknown_symbol", {}, {}, path="/api/v1/ticker/NSE:NOSUCH"),
    tick("ticker_interval_zerodha_lacks", {"interval": "W"}, {}),
    tick("ticker_broker_refused", {}, {SBIN + "5minute": REFUSED}),
    {"name": "ticker_invalid_apikey", "method": "GET", "path": T, "auth": "none", "request": {"apikey": "not-a-key", "interval": "5m", "from": "2026-11-02", "to": "2026-11-02"}, "broker": {}},
    {"name": "ticker_missing_apikey", "method": "GET", "path": T, "auth": "none", "request": {"interval": "5m", "from": "2026-11-02", "to": "2026-11-02"}, "broker": {}},
]
