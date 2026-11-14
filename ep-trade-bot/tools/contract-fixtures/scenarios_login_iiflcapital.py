"""IIFL Capital sign-in: the authCode and clientId its page returns are exchanged at /getusersession with a checksum of the client id, the code and the app secret."""

BROKER = "iiflcapital"
SYMBOLS = []
CLIENT_ID = "CL1234"
ENV = {"BROKER_API_KEY": "iifl-app-key", "BROKER_API_SECRET": "iifl-app-secret"}

URL = "POST /v1/getusersession"
GOOD = {"body": {"status": "Ok", "userSession": "iifl-session-777", "message": "ok"}}


def sign_in(name, reply, code="auth-code-abc", client="CL1234"):
    return {"name": name, "login": [code, client], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_code", GOOD),
    sign_in("login_status_in_another_case", {"body": {"status": "OK", "userSession": "iifl-session-778"}}),
    sign_in("login_refused_with_a_message", {"status": 401, "body": {"status": "error", "message": "Invalid checksum"}}),
    sign_in("login_refused_with_an_error_text", {"status": 400, "body": {"error": "authCode expired"}}),
    sign_in("login_refused_with_nothing_useful", {"status": 500, "body": {"x": 1}}),
    sign_in("login_ok_status_without_a_session", {"body": {"status": "Ok", "message": "no session"}}),
    sign_in("login_not_ok_with_a_message", {"body": {"status": "Failed", "message": "Account locked"}}),
    sign_in("login_not_ok_without_a_message", {"body": {"status": "Failed"}}),
]
