"""Shoonya sign-in: the code Shoonya's page returns is exchanged at GenAcsTok with a SHA-256 checksum of the client id, the secret and the code."""

BROKER = "shoonya"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "FA12345:::shoonya-client-id", "BROKER_API_SECRET": "shoonya-secret"}

URL = "POST /NorenWClientAPI/GenAcsTok"
GOOD = {"body": {"stat": "Ok", "access_token": "shoonya-access-777", "request_time": "10:20:30 03-11-2026"}}


def sign_in(name, reply, code="auth-code-abc"):
    return {"name": name, "login": [code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_code", GOOD),
    sign_in("login_code_with_odd_characters", GOOD, code="a/b+c=d&e"),
    sign_in("login_refused_with_a_message", {"body": {"stat": "Not_Ok", "emsg": "Invalid code"}}),
    sign_in("login_refused_without_a_message", {"body": {"stat": "Not_Ok"}}),
    sign_in("login_ok_without_a_token", {"body": {"stat": "Ok"}}),
    sign_in("login_token_without_ok_status", {"body": {"access_token": "x"}}),
    sign_in("login_http_error", {"status": 500, "body": {"error": "down"}}),
]
