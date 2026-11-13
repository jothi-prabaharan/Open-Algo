"""Wisdom Capital (XTS) sign-in: no redirect and nothing typed. The interactive key signs in at /interactive/user/session and the market-data key at /apimarketdata/auth/login."""

BROKER = "wisdom"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "wisdom-app-key", "BROKER_API_SECRET": "wisdom-app-secret",
       "BROKER_API_KEY_MARKET": "wisdom-market-key", "BROKER_API_SECRET_MARKET": "wisdom-market-secret"}

SESSION = "POST /interactive/user/session"
MARKET = "POST /apimarketdata/auth/login"
SESSION_OK = {"body": {"type": "success", "code": "s-user-0001", "description": "Login Successful", "result": {"token": "wisdom-session-token", "userID": "WC001", "isInvestorClient": False}}}
MARKET_OK = {"body": {"type": "success", "code": "s-user-0001", "description": "Login Successful", "result": {"token": "wisdom-feed-token", "userID": "WC001_MD"}}}


def sign_in_steps(_unused):
    import importlib

    authenticate_broker = importlib.import_module(f"broker.{BROKER}.api.auth_api").authenticate_broker

    token, feed, _user, error = authenticate_broker(_unused)
    return token, feed, error, 400


def sign_in(name, session, market=None):
    broker = {SESSION: session}
    if market is not None:
        broker[MARKET] = market
    return {"name": name, "login": ["wisdom"], "request": {}, "broker": broker}


SCENARIOS = [
    sign_in("login_returns_session_and_feed_tokens", SESSION_OK, MARKET_OK),
    sign_in("login_refused_with_a_message", {"status": 401, "body": {"type": "error", "code": "e-apikey-0002", "description": "Invalid appKey", "message": "Invalid App Key"}}),
    sign_in("login_refused_with_an_ok_status_and_an_error_type", {"body": {"type": "error", "code": "e-user-0001", "description": "Invalid user"}}),
    sign_in("login_ok_body_with_an_error_status", {"status": 500, "body": {"type": "success", "message": "Gateway trouble", "result": {"token": "should-not-be-used"}}}),
    sign_in("login_session_without_a_token_field", {"body": {"type": "success", "result": {}}}),
    sign_in("login_market_data_refused", SESSION_OK, {"status": 401, "body": {"type": "error", "code": "e-apikey-0002", "description": "Invalid market data key"}}),
    sign_in("login_market_data_ok_status_error_type", SESSION_OK, {"body": {"type": "error", "description": "Market data login not enabled"}}),
    sign_in("login_market_data_without_a_token", SESSION_OK, {"body": {"type": "success", "result": {"userID": "X"}}}),
]
