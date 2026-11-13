"""Upstox sign-in: the code its page returns is exchanged at /v2/login/authorization/token (form body, with the redirect address)."""

BROKER = "upstox"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "upstox-api-key", "BROKER_API_SECRET": "upstox-api-secret", "REDIRECT_URL": "http://127.0.0.1:5000/upstox/callback"}

URL = "POST /v2/login/authorization/token"
GOOD = {"body": {"access_token": "upstox-access-777", "email": "t@example.com", "user_id": "UP1234"}}


def sign_in(name, reply, code="auth-code-abc"):
    return {"name": name, "login": [code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_code", GOOD),
    sign_in("login_refused_with_reasons", {"status": 401, "body": {"status": "error", "errors": [{"message": "Invalid Auth code"}, {"message": "Code expired"}]}}),
    sign_in("login_refused_without_reasons", {"status": 500, "body": {"error": "x"}}),
    sign_in("login_no_access_token_in_the_answer", {"body": {"email": "t@example.com"}}),
    sign_in("login_empty_access_token", {"body": {"access_token": ""}}),
]
