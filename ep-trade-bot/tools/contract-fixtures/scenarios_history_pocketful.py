"""Pocketful has no history: OpenAlgo's call answers a frame with one message row, which the platform reads as no candles."""

import scenarios_pocketful as s
from scenarios_pocketful import AUTH_TOKEN, CLIENT_ID, ENV, READ_POSTS, SEED_API_KEY_ROW, SYMBOLS  # noqa: F401

BROKER = "pocketful"


def hist(name, request):
    base = {"symbol": "SBIN", "exchange": "NSE", "interval": "1m", "start_date": "2026-11-02", "end_date": "2026-11-03"}
    return {"name": name, "path": "/api/v1/history", "request": {**base, **request}, "broker": {}, "ignore": []}


SCENARIOS = [
    hist("history_one_minute", {}),
    hist("history_daily", {"interval": "D"}),
    hist("history_unknown_symbol", {"symbol": "NOPE"}),
]
