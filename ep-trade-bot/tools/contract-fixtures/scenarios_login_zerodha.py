"""Zerodha sign-in: the request token Kite returns is exchanged at /session/token."""

BROKER = "zerodha"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "kite-api-key", "BROKER_API_SECRET": "kite-api-secret"}

def stored(token):
    """What OpenAlgo keeps: Kite's Authorization header takes api_key:access_token (blueprints/brlogin.py)."""
    return "kite-api-key:" + token


URL = "POST /session/token"
GOOD = {"body": {"status": "success", "data": {"user_id": "AB1234", "access_token": "kite-access-token-777", "public_token": "pub"}}}


def sign_in(name, request_token, reply, state="<issued>"):
    return {"name": name, "login": [request_token], "request": {"request_token": request_token, "state": state}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_request_token", "req-token-abc", GOOD),
    sign_in("login_token_with_special_characters", "req+token/with=chars&more", GOOD),
    sign_in("login_kite_refuses_the_token", "stale-token", {"status": 403, "body": {"status": "error", "message": "Token is invalid or has expired.", "error_type": "TokenException"}}),
    sign_in("login_kite_refuses_without_a_message", "stale-token", {"status": 500, "body": {"status": "error"}}),
    sign_in("login_no_access_token_in_the_answer", "req-token-abc", {"body": {"status": "success", "data": {"user_id": "AB1234"}}}),
]
