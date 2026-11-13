"""Rupeezy sign-in: the auth code its page returns is exchanged at /user/session with a checksum of application id, code and secret."""

BROKER = "rupeezy"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "rupeezy-app-id", "BROKER_API_SECRET": "rupeezy-secret"}

URL = "POST /v2/user/session"


def sign_in(name, reply, code="auth-code-abc"):
    return {"name": name, "login": [code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_auth_code", {"body": {"status": "success", "data": {"access_token": "rupeezy-access-777"}}}),
    sign_in("login_refused_with_a_message", {"status": 401, "body": {"status": "error", "message": "Invalid checksum"}}),
    sign_in("login_refused_without_a_message", {"status": 500, "body": {"x": 1}}),
    sign_in("login_ok_without_a_token_with_message", {"body": {"status": "error", "message": "Auth code expired"}}),
    sign_in("login_ok_without_a_token", {"body": {"status": "success", "data": {}}}),
]
