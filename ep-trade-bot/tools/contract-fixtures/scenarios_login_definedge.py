"""Definedge sign-in: two steps. A request to GET signin/.../login/{api token} with the API secret sends a one-time code and answers an otp_token; the code and a hash of the token, the code and the secret go to POST .../token, which answers the session key and user token. The session is the session key, the user token and the API token joined by three colons."""

BROKER = "definedge"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "dg-api-token", "BROKER_API_SECRET": "dg-secret"}
SEND = "GET /auth/realms/debroking/dsbpkc/login/dg-api-token"
TOKEN = "POST /auth/realms/debroking/dsbpkc/token"


def sign_in_steps(kind, otp_token, otp, secret="dg-secret"):
    import importlib

    auth = importlib.import_module("broker.definedge.api.auth_api")
    if kind == "send":
        answer = auth.login_step1("dg-api-token", secret)
        if answer is None:
            return None, None, "Failed to send the one-time code", 400
        return None, None, answer.get("message"), 400
    token, feed, _uid, error = auth.authenticate_broker(otp_token, otp, secret)
    return token, feed, error


def step(name, reply, kind="verify", otp_token="otp-token-1", otp="123456"):
    broker = {SEND: reply} if kind == "send" else {TOKEN: reply}
    return {"name": name, "login": [kind, otp_token, otp], "request": {"otp_token": otp_token, "otp": otp if kind == "verify" else ""}, "broker": broker}


def good(**extra):
    d = {"stat": "Ok", "api_session_key": "dg-session-777", "susertoken": "dg-user-token", "uid": "U1"}
    d.update(extra)
    return {"body": d}


SCENARIOS = [
    step("login_sends_the_code", {"body": {"otp_token": "otp-token-1", "message": "OTP sent to your registered mobile"}}, kind="send"),
    step("login_sends_the_code_with_no_message", {"body": {"otp_token": "otp-token-1"}}, kind="send"),
    step("login_cannot_send_the_code", {"status": 500, "body": {"message": "down"}}, kind="send"),
    step("login_returns_the_session", good()),
    step("login_without_a_user_token", good(susertoken=None)),
    step("login_without_a_session_key", {"body": {"stat": "Ok", "susertoken": "x"}}),
    step("login_refused_with_a_message", {"body": {"stat": "Not_Ok", "emsg": "Invalid OTP"}}),
    step("login_refused_without_a_message", {"body": {"stat": "Not_Ok"}}),
    step("login_refused_with_a_status", {"status": 401, "body": {"emsg": "Bad"}}),
    step("login_answer_that_is_not_json", {"status": 200, "body": "oops"}),
]


def patch():
    pass
