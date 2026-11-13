"""/margin on Wisdom Capital, which has no margin calculator (and neither have iBulls, Jainam, 5paisa XTS, IIFL or CompositEdge)."""

import scenarios_wisdom as w

BROKER = "wisdom"
AUTH_TOKEN, ENV, SYMBOLS = w.AUTH_TOKEN, w.ENV, w.SYMBOLS
SEED_API_KEY_ROW = True
SCENARIOS = [
    {"name": "margin_has_no_calculator", "path": "/api/v1/margin", "request": {"positions": [{"symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "product": "MIS", "pricetype": "MARKET"}]}, "broker": {}},
]
