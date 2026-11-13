"""/margin on Zebu, which has no position-specific margin calculator."""

import scenarios_zebu as z

BROKER = "zebu"
AUTH_TOKEN, ENV, SYMBOLS = z.AUTH_TOKEN, z.ENV, z.SYMBOLS
SEED_API_KEY_ROW = True
SCENARIOS = [
    {"name": "margin_has_no_calculator", "path": "/api/v1/margin", "request": {"positions": [{"symbol": "SBIN", "exchange": "NSE", "action": "BUY", "quantity": "10", "product": "MIS", "pricetype": "MARKET"}]}, "broker": {}},
]
