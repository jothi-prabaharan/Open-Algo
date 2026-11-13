"""RMoney (XTS) sign-in: the session RMoney's page posts back holds the interactive token; RMoney is asked who it belongs to, and the market-data keys must be for the same account."""

import importlib as _importlib
import json

BROKER = "rmoney"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "rm-app-key", "BROKER_API_SECRET": "rm-app-secret", "BROKER_API_KEY_MARKET": "rm-market-key", "BROKER_API_SECRET_MARKET": "rm-market-secret"}

PROFILE = "GET /interactive/user/profile"
MARKET = "POST /apibinarymarketdata/auth/login"


def profile(**fields):
    return {"body": {"type": "success", "result": fields}}


def market_ok(user="AB123"):
    return {"body": {"type": "success", "result": {"token": "rm-feed-token", "userID": user}}}


def sign_in_steps(session_text):
    """The steps of the sign-in handler in OpenAlgo's login blueprint, with its own functions."""
    mod = _importlib.import_module("broker.rmoney.api.auth_api")
    data = json.loads(session_text)
    if isinstance(data, str):
        data = json.loads(data)
    token = data.get("token")
    user = mod.session_account(token)
    if not user:
        return None, None, "account_unverified", 409
    feed, feed_user, feed_error = mod.get_feed_token()
    if feed_error:
        feed = None
    a, b = str(user or "").strip().upper(), str(feed_user or "").strip().upper()
    if a and b and a != b:
        return None, None, "account_mismatch", 409
    return token, feed, None, 400


def sign_in(name, profile_reply, market=None, session=None):
    session = session or json.dumps({"token": "rm-session-token", "userID": "label-only"})
    broker = {PROFILE: profile_reply}
    if market is not None:
        broker[MARKET] = market
    return {"name": name, "login": [session], "request": {"request_token": session, "state": "<issued>"}, "broker": broker}


SCENARIOS = [
    sign_in("login_same_account", profile(ClientId="AB123"), market_ok()),
    sign_in("login_account_letter_case_and_spaces", profile(ClientId=" ab123 "), market_ok("AB123")),
    sign_in("login_account_under_another_key_spelling", profile(clientID="AB123"), market_ok()),
    sign_in("login_market_data_account_with_spaces", profile(ClientId="AB123"), market_ok(" ab123 ")),
    sign_in("login_profile_error_status_with_a_success_body", {"status": 500, "body": {"type": "success", "result": {"ClientId": "AB123"}}}, market_ok()),
    sign_in("login_different_accounts", profile(ClientId="AB123"), market_ok("XY999")),
    sign_in("login_market_data_names_no_account", profile(ClientId="AB123"), market_ok("")),
    sign_in("login_market_data_refused_leaves_no_feed_token", profile(ClientId="AB123"), {"status": 401, "body": {"type": "error", "description": "Bad market key"}}),
    sign_in("login_market_data_error_type_leaves_no_feed_token", profile(ClientId="AB123"), {"body": {"type": "error"}}),
    sign_in("login_profile_names_no_account", profile(Other="x"), market_ok()),
    sign_in("login_profile_refused", {"status": 401, "body": {"type": "error", "description": "Invalid token"}}, market_ok()),
    sign_in("login_profile_result_not_an_object", {"body": {"type": "success", "result": "x"}}, market_ok()),
    sign_in("login_session_posted_as_a_json_string", profile(ClientId="AB123"), market_ok(), session=json.dumps(json.dumps({"token": "rm-session-token", "userID": "x"}))),
]
