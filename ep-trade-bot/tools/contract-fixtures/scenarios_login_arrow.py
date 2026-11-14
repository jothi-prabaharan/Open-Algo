"""Arrow sign-in: the request token its page returns is exchanged at /auth/app/authenticate-token with a checksum of the app id, the secret and the token, colon separated."""

BROKER = "arrow"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "arrow-app-id", "BROKER_API_SECRET": "arrow-app-secret"}

URL = "POST /auth/app/authenticate-token"
GOOD = {"body": {"status": "success", "data": {"name": "T", "token": "arrow-jwt-777", "userID": "U1"}}}


def sign_in(name, reply, token="req-token-abc"):
    return {"name": name, "login": [token], "request": {"request_token": token, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_request_token", GOOD),
    sign_in("login_refused_with_a_message", {"status": 401, "body": {"status": "error", "message": "Invalid checksum"}}),
    sign_in("login_refused_without_a_message", {"status": 500, "body": {"x": 1}}),
    sign_in("login_failure_envelope_with_a_message", {"body": {"status": "failure", "message": "Token expired"}}),
    sign_in("login_failure_envelope_without_a_message", {"body": {"status": "failure"}}),
    sign_in("login_success_without_a_token", {"body": {"status": "success", "data": {"name": "T"}}}),
    sign_in("login_success_without_data", {"body": {"status": "success"}}),
]
