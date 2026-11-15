"""Nubra sign-in by a code sent to the phone, in two steps. The first asks Nubra to send the code (/sendphoneotp, with no device id; when it answers VERIFY_TOTP a second call with that token and skip_totp set sends the SMS) and answers a temp token. The second sends
the code (/verifyphoneotp, with the temp token and the device id) for an auth token, and the MPIN (/verifypin) for the session token."""

BROKER = "nubra"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "9876543210", "BROKER_API_SECRET": "1234"}
SEND = "POST /sendphoneotp"
VERIFY = "POST /verifyphoneotp"
PIN = "POST /verifypin"


def sign_in_steps(kind, otp=None, temp=None):
    import importlib

    auth = importlib.import_module("broker.nubra.api.auth_api")
    if kind == "send":
        token, masked, error = auth.request_login_otp()
        if error:
            return None, None, error, 400
        return None, None, f"A one-time code was sent to {masked}", 400
    token, feed, error = auth.authenticate_broker(otp, temp)
    return token, feed, error, 400


def send(name, replies):
    return {"name": name, "login": ["send"], "request": {"otp": ""}, "broker": {SEND: replies}}


def verify(name, broker, otp="123456", temp="temp-2"):
    return {"name": name, "login": ["verify", otp, temp], "request": {"otp": otp, "otp_token": temp} if temp else {"otp": otp}, "broker": broker}


def started(next_step="VERIFY_MOBILE", token="temp-1"):
    return {"body": {"temp_token": token, "next": next_step, "message": "OTP sent", "expiry": 30, "attempts_left": 3}}


GOOD_VERIFY = {"body": {"auth_token": "auth-1"}}
GOOD_PIN = {"body": {"session_token": "nubra-session-777"}}

SCENARIOS = [
    send("login_sends_the_code", started()),
    send("login_sends_the_code_in_two_calls_when_totp_is_asked", [started("VERIFY_TOTP", "temp-1"), started("VERIFY_MOBILE", "temp-2")]),
    send("login_the_first_call_is_refused_with_an_error", {"status": 400, "body": {"error": "Phone not registered"}}),
    send("login_the_first_call_is_refused_with_a_message", {"status": 400, "body": {"message": "Too many attempts"}}),
    send("login_the_first_call_is_refused_with_no_reason", {"status": 500, "body": {}}),
    send("login_the_first_call_answers_no_token", {"body": {"next": "VERIFY_MOBILE"}}),
    send("login_the_first_call_is_not_json", {"status": 200, "body": "<html>"}),
    send("login_an_unexpected_next_step", started("SOMETHING")),
    send("login_no_next_step", {"body": {"temp_token": "temp-1"}}),
    send("login_the_second_call_is_refused", [started("VERIFY_TOTP"), {"status": 400, "body": {"error": "Cannot send"}}]),
    send("login_the_next_step_in_lower_case", started("verify_mobile")),
    verify("login_returns_the_session", {VERIFY: GOOD_VERIFY, PIN: GOOD_PIN}),
    verify("login_with_no_temp_token", {VERIFY: GOOD_VERIFY, PIN: GOOD_PIN}, temp=None),
    verify("login_with_an_otp_that_is_not_a_number", {VERIFY: GOOD_VERIFY, PIN: GOOD_PIN}, otp="12a456"),
    verify("login_with_an_otp_with_spaces", {VERIFY: GOOD_VERIFY, PIN: GOOD_PIN}, otp=" 123456 "),
    verify("login_the_code_is_refused_with_an_error", {VERIFY: {"status": 400, "body": {"error": "Invalid OTP"}}}),
    verify("login_the_code_is_refused_with_a_message", {VERIFY: {"status": 400, "body": {"message": "Expired"}}}),
    verify("login_the_code_is_refused_with_no_reason", {VERIFY: {"status": 400, "body": {}}}),
    verify("login_the_code_answer_is_not_json", {VERIFY: {"status": 200, "body": "<html>"}}),
    verify("login_the_code_answer_has_no_token", {VERIFY: {"body": {}}}),
    verify("login_the_pin_is_refused_with_an_error", {VERIFY: GOOD_VERIFY, PIN: {"status": 400, "body": {"error": "Wrong PIN"}}}),
    verify("login_the_pin_is_refused_with_no_reason", {VERIFY: GOOD_VERIFY, PIN: {"status": 401, "body": {}}}),
    verify("login_the_pin_answer_has_no_session_token", {VERIFY: GOOD_VERIFY, PIN: {"body": {}}}),
    verify("login_the_pin_answer_is_not_json", {VERIFY: GOOD_VERIFY, PIN: {"status": 200, "body": "<html>"}}),
]
