"""AliceBlue sign-in: the trader signs in on AliceBlue's page, which sends back an auth code and the user id. The code is exchanged at the vendor's getUserDetails with a SHA-256 checksum of the user id, the code and the API secret; the answer carries the user session."""

BROKER = "aliceblue"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "ab-app-code", "BROKER_API_SECRET": "ab-secret"}
CLIENT_ID = "AB1234"

URL = "POST /open-api/od/v1/vendor/getUserDetails"


def sign_in_steps(user_id, auth_code):
    """What the callback does: the answer's session is the token; AliceBlue has no separate feed token."""
    from broker.aliceblue.api.auth_api import authenticate_broker

    token, _client, error = authenticate_broker(user_id, auth_code)
    return token, None, error


def sign_in(name, reply, code="auth-code-abc"):
    return {"name": name, "login": [CLIENT_ID, code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_auth_code", {"body": {"stat": "Ok", "userSession": "ab-session-777", "clientId": "AB1234"}}),
    sign_in("login_without_a_client_id_in_the_answer", {"body": {"stat": "Ok", "userSession": "ab-session-778"}}),
    sign_in("login_refused_not_ok", {"body": {"stat": "Not_ok", "emsg": "Invalid auth code"}}),
    sign_in("login_refused_not_ok_lowercase_without_a_message", {"body": {"stat": "Not_ok"}}),
    sign_in("login_refused_not_ok_exact_spelling", {"body": {"stat": "Not_Ok", "emsg": "Auth code expired"}}),
    sign_in("login_refused_not_ok_without_a_message", {"body": {"stat": "Not_Ok"}}),
    sign_in("login_refused_with_a_message_only", {"body": {"emsg": "Checksum mismatch"}}),
    sign_in("login_ok_without_a_session", {"body": {"stat": "Ok", "clientId": "AB1234"}}),
    sign_in("login_ok_with_an_empty_session", {"body": {"stat": "Ok", "userSession": ""}}),
    sign_in("login_refused_with_a_status", {"status": 401, "body": {"stat": "Not_Ok", "emsg": "Unauthorised"}}),
    sign_in("login_answer_that_is_not_json", {"status": 502, "body": "Bad gateway"}),
    sign_in("login_answer_that_is_not_an_object", {"body": ["x"]}),
]
