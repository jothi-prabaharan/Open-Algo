"""History on Dhan Sandbox. Same endpoints as Dhan; its intraday candles are passed through a mock-realism step in OpenAlgo
that invents prices, which EP Trade bot does not do (a recorded deviation)."""

import scenarios_history_dhan as live
from scenarios_history_dhan import (  # noqa: F401
    D1, DAILY, EMPTY, FUT, HISTORICAL, INTRADAY, LONG, M5, NOT_SUBSCRIBED, RECORDED_AT, SATURDAY, SUNDAY, SYMBOLS, WEEK, hist, iso, wd,
)

BROKER = "dhan_sandbox"
CLIENT_ID = live.CLIENT_ID
AUTH_TOKEN = "dhan-sandbox-access-token"
ENV = {"BROKER_API_KEY": f"{CLIENT_ID}:::dhan-sandbox-app-key", "BROKER_API_SECRET": "dhan-sandbox-secret", "TZ": "Asia/Kolkata"}
READ_POSTS = live.READ_POSTS
INIT = live.INIT


def keep(names):
    return [s for s in live.SCENARIOS if s["name"] in names]


SCENARIOS = keep({
    "history_daily", "history_weekend_only_intraday", "history_weekend_only_daily", "history_unsupported_interval",
    "history_unknown_symbol", "history_bad_interval", "history_invalid_apikey", "intervals", "intervals_invalid_apikey",
}) + [
    hist("history_daily_futures_sends_no_expiry_code", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "D", "start_date": iso(wd(5)), "end_date": iso(wd(4))}, {HISTORICAL: DAILY}),
    hist("history_5m_one_day", {}, {INTRADAY: M5}),
    hist("history_5m_futures_with_open_interest", {"symbol": "NIFTY25NOV26FUT", "exchange": "NFO", "interval": "1m"}, {INTRADAY: FUT}),
    hist("history_week_one_request", {**WEEK}, {INTRADAY: M5}),
    # 200 days of 5 minute candles: 90 days a request, three requests, none tried again.
    hist("history_chunked_90_days", {**LONG}, {INTRADAY: [M5, M5, M5]}),
    hist("history_nothing_traded", {}, {INTRADAY: EMPTY}),
    hist("history_one_day_refused", {}, {INTRADAY: NOT_SUBSCRIBED}),
    hist("history_week_refused", {**WEEK}, {INTRADAY: NOT_SUBSCRIBED}),
]
