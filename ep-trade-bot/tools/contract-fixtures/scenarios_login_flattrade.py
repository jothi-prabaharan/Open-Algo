"""Flattrade sign-in: the code its page returns is exchanged at authapi.flattrade.in/trade/apitoken with a SHA-256 of the app key, the code and the secret."""

BROKER = "flattrade"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "FA12345:::flattrade-app-key", "BROKER_API_SECRET": "flattrade-secret"}

URL = "POST /trade/apitoken"
GOOD = {"body": {"stat": "Ok", "token": "flattrade-token-777"}}


def sign_in(name, reply, code="req-code-abc"):
    return {"name": name, "login": [code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_code", GOOD),
    sign_in("login_refused_with_a_message", {"body": {"stat": "Not_Ok", "emsg": "Invalid Input : Wrong code"}}),
    sign_in("login_refused_without_a_message", {"body": {"stat": "Not_Ok"}}),
    sign_in("login_ok_without_a_token", {"body": {"stat": "Ok"}}),
    sign_in("login_token_without_ok_status", {"body": {"token": "x"}}),
    sign_in("login_http_error_with_a_message", {"status": 400, "body": {"stat": "Not_Ok", "emsg": "Bad request"}}),
    sign_in("login_http_error_without_a_message", {"status": 500, "body": {"error": "down"}}),
]
