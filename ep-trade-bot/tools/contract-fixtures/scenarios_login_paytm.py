"""Paytm Money sign-in: the request token its page returns is exchanged at /accounts/v2/gettoken."""

BROKER = "paytm"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "paytm-api-key", "BROKER_API_SECRET": "paytm-api-secret"}

URL = "POST /accounts/v2/gettoken"
GOOD = {"body": {"access_token": "paytm-access-777", "public_access_token": "paytm-public-888", "read_access_token": "paytm-read-999"}}


def sign_in(name, reply, request_token="req-token-abc"):
    return {"name": name, "login": [request_token], "request": {"request_token": request_token, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_request_token", GOOD),
    sign_in("login_without_a_public_token_uses_the_access_token", {"body": {"access_token": "paytm-access-777"}}),
    sign_in("login_refused_with_reasons", {"status": 400, "body": {"errors": [{"message": "Request token is invalid."}, {"message": "Request token has expired."}]}}),
    sign_in("login_refused_without_reasons", {"status": 500, "body": {"error": "x"}}),
    sign_in("login_no_access_token_in_the_answer", {"body": {"read_access_token": "paytm-read-999"}}),
]
