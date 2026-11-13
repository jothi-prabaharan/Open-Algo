"""CompositEdge (XTS) sign-in: an access token from CompositEdge's page is exchanged for the interactive session; the market-data session must name the same account."""

import importlib as _importlib

BROKER = "compositedge"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "ce-app-key", "BROKER_API_SECRET": "ce-app-secret", "BROKER_API_KEY_MARKET": "ce-market-key", "BROKER_API_SECRET_MARKET": "ce-market-secret"}

SESSION = "POST /interactive/user/session"
MARKET = "POST /apimarketdata/auth/login"


def session_ok(user="AB123"):
    return {"body": {"type": "success", "result": {"token": "ce-session-token", "userID": user}}}


def market_ok(user="AB123"):
    return {"body": {"type": "success", "result": {"token": "ce-feed-token", "userID": user}}}


def sign_in_steps(access_token):
    authenticate_broker = _importlib.import_module(f"broker.{BROKER}.api.auth_api").authenticate_broker
    token, feed, _user, error = authenticate_broker(access_token)
    return token, feed, error, 400


def sign_in(name, session, market=None, token="ce-access-token"):
    broker = {SESSION: session}
    if market is not None:
        broker[MARKET] = market
    return {"name": name, "login": [token], "request": {"request_token": token, "state": "<issued>"}, "broker": broker}


SCENARIOS = [
    sign_in("login_same_account", session_ok(), market_ok()),
    sign_in("login_same_account_other_letter_case", session_ok("ab123"), market_ok("AB123")),
    sign_in("login_account_with_spaces", session_ok(" AB123 "), market_ok("AB123")),
    sign_in("login_different_accounts", session_ok("AB123"), market_ok("XY999")),
    sign_in("login_session_names_no_account", session_ok(""), market_ok("XY999")),
    sign_in("login_market_data_names_no_account", session_ok("AB123"), market_ok("")),
    sign_in("login_session_refused", {"status": 401, "body": {"type": "error", "description": "Invalid access token", "message": "Invalid access token"}}),
    sign_in("login_session_error_type_with_ok_status", {"body": {"type": "error", "description": "Expired"}}),
    sign_in("login_market_data_refused", session_ok(), {"status": 401, "body": {"type": "error", "description": "Bad market key"}}),
    sign_in("login_market_data_error_type_with_ok_status", session_ok(), {"body": {"type": "error"}}),
]
