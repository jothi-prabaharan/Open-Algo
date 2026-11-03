"""Dhan Sandbox scenarios. Same requests as Dhan; the sandbox's own replies where they differ.

Dhan Sandbox reports updateTime in UTC, which the plugin converts to IST, and has
working states (TRANSIT, PART_TRADED) and EXPIRED that the live Dhan plugin does not map.
"""

import copy

import scenarios_dhan as dhan

CLIENT_ID = dhan.CLIENT_ID
AUTH_TOKEN = "dhan-sandbox-access-token"
ENV = {"BROKER_API_KEY": f"{CLIENT_ID}:::dhan-sandbox-app-key", "BROKER_API_SECRET": "dhan-sandbox-secret"}
READ_POSTS = dhan.READ_POSTS
SEED_API_KEY_ROW = True
SYMBOLS = dhan.SYMBOLS

ORDERS = copy.deepcopy(dhan.ORDERS)
ORDERS["body"].append(dhan._order("1009", "NSE_EQ", "3045", "SBIN", "BUY", "LIMIT", "INTRADAY", 1, 650, 0, "EXPIRED", "2026-11-03 03:50:00"))
POSITIONS = copy.deepcopy(dhan.POSITIONS)
POSITIONS["body"][0]["ltp"] = 805.456
POSITIONS["body"][1]["lastTradedPrice"] = 0
POSITIONS["body"][1]["closePrice"] = 23990.0

# holdings_none: the sandbox plugin has no no-holdings branch. cancelallorder: replaced below,
# because the sandbox also cancels TRANSIT orders and needs a reply for them.
SKIP = {"holdings_none", "cancelallorder"}
CHART = {"body": {"open": [805.0, 805.5, 806.0], "high": [806.0, 806.5, 806.2], "low": [804.5, 0, 805.4], "close": [805.5, 806.0, 805.9], "volume": [1200, 800.0, 400], "timestamp": [1793686800, 1793686860, 1793686920]}}
SCENARIOS = []
for sc in copy.deepcopy(dhan.SCENARIOS):
    if sc["name"] in SKIP:
        continue
    broker = sc.get("broker", {})
    if "GET /v2/orders" in broker:
        broker["GET /v2/orders"] = ORDERS
    if "GET /v2/positions" in broker:
        broker["GET /v2/positions"] = POSITIONS
    if "POST /v2/marketfeed/quote" in broker and sc["name"].startswith(("quotes", "depth", "multiquotes")):
        # The sandbox has no market feed; it derives a quote from today's one-minute candles.
        del broker["POST /v2/marketfeed/quote"]
        if sc["name"] != "quotes_not_subscribed":
            broker["POST /v2/charts/intraday"] = CHART
    SCENARIOS.append(sc)

SCENARIOS.append({"name": "quotes_no_candles", "path": "/api/v1/quotes", "request": {"symbol": "SBIN", "exchange": "NSE"}, "broker": {"POST /v2/charts/intraday": {"body": {"open": [], "high": [], "low": [], "close": [], "volume": [], "timestamp": []}}}})
SCENARIOS.append({"name": "cancelallorder_working_states", "path": "/api/v1/cancelallorder", "request": {"strategy": "t"}, "broker": {
    "GET /v2/orders": ORDERS,
    **{f"DELETE /v2/orders/{oid}": {"body": {"orderId": oid, "orderStatus": "CANCELLED"}} for oid in ("1001", "1003", "1006", "1007")},
}})
