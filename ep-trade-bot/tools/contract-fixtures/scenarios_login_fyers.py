"""Fyers sign-in: the auth code its page returns is exchanged at /api/v3/validate-authcode with a hash of app id and secret."""

BROKER = "fyers"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "FYAPP-100", "BROKER_API_SECRET": "fyers-secret", "REDIRECT_URL": "http://127.0.0.1:5000/fyers/callback"}

URL = "POST /api/v3/validate-authcode"


def sign_in(name, reply, code="fy-auth-code"):
    return {"name": name, "login": [code], "request": {"request_token": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_auth_code", {"body": {"s": "ok", "code": 200, "message": "", "access_token": "fyers-access-777", "refresh_token": "r"}}),
    sign_in("login_refused_by_status", {"body": {"s": "error", "code": -413, "message": "Invalid auth code"}}),
    sign_in("login_refused_by_http_error", {"status": 401, "body": {"s": "error", "code": -413, "message": "Invalid auth code"}}),
    sign_in("login_no_access_token_in_the_answer", {"body": {"s": "ok", "code": 200}}),
    sign_in("login_http_error_with_an_ok_looking_body", {"status": 500, "body": {"s": "ok", "access_token": "fyers-access-777"}}),
    sign_in("login_empty_access_token", {"body": {"s": "ok", "access_token": ""}}),
]
