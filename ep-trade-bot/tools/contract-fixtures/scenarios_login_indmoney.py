"""IndMoney sign-in. Two ways, as OpenAlgo has them: the Client ID, the MPIN and a live one-time code go to /generate/token and the token it answers is the session; or, with no MPIN and code, a token pasted into the API secret is used after /user/profile accepts it (a rejection is refused, an answer that says nothing about the token lets it through)."""

import os

BROKER = "indmoney"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "ind-client-id", "BROKER_API_SECRET": ""}
TOKEN_EP = "POST /generate/token"
PROFILE_EP = "GET /user/profile"


def sign_in_steps(mode, mpin=None, totp=None, secret=None):
    import importlib

    auth = importlib.import_module("broker.indmoney.api.auth_api")
    importlib.import_module("broker.indmoney.api.rate_limiter").time.sleep = lambda *a, **k: None
    os.environ["BROKER_API_SECRET"] = secret or ""
    if mode == "manual":
        token, error = auth.authenticate_broker(None)
        return token, None, error, 400
    if not mpin or not totp:
        return None, None, "Please provide both MPIN and TOTP code", 400
    token, error = auth.authenticate_broker_totp(mpin, totp)
    return token, None, error, 400


def totp(name, reply, mpin="123456", code="654321", request=None):
    return {"name": name, "login": ["totp", mpin, code], "request": request or {"mpin": mpin, "totp": code}, "broker": {TOKEN_EP: reply}}


def manual(name, reply=None, secret="pasted-token-1"):
    return {"name": name, "login": ["manual", None, None, secret], "request": {}, "broker": {} if reply is None else {PROFILE_EP: reply}}


def minted(**extra):
    return {"body": {"status": "success", "data": {"token": "ind-token-777", **extra}}}


SCENARIOS = [
    totp("login_totp", minted()),
    totp("login_totp_token_at_the_top", {"body": {"token": "top-token"}}),
    totp("login_totp_access_token_in_data", {"body": {"data": {"access_token": "data-access"}}}),
    totp("login_totp_access_token_at_the_top", {"body": {"access_token": "top-access"}}),
    totp("login_totp_status_201", {"status": 201, "body": {"data": {"token": "created-token"}}}),
    totp("login_totp_with_a_code_that_has_leading_zeros", minted(), code="004321"),
    totp("login_totp_with_spaces_around_the_mpin_and_code", minted(), mpin=" 123456 ", code=" 654321 "),
    totp("login_totp_no_token_in_the_answer", {"body": {"status": "success", "data": {}}}),
    totp("login_totp_an_empty_answer", {"body": {}}),
    totp("login_totp_wrong_code", {"status": 401, "body": {"message": "Invalid TOTP"}}),
    totp("login_totp_forbidden_with_an_error_field", {"status": 403, "body": {"error": "Account blocked"}}),
    totp("login_totp_unauthorized_without_a_message", {"status": 401, "body": {}}),
    totp("login_totp_throttled_with_a_message", {"status": 429, "body": {"message": "Too many requests"}}),
    totp("login_totp_throttled_without_a_message", {"status": 429, "body": {}}),
    totp("login_totp_server_error_with_a_message", {"status": 500, "body": {"message": "Internal error"}}),
    totp("login_totp_server_error_without_a_message", {"status": 503, "body": {}}),
    totp("login_totp_bad_request_without_a_message", {"status": 400, "body": {}}),
    totp("login_totp_bad_request_with_a_message", {"status": 400, "body": {"message": "Invalid MPIN"}}),
    totp("login_totp_answer_is_not_json_with_status_200", {"status": 200, "body": "<html>"}),
    totp("login_totp_answer_is_not_json_with_status_401", {"status": 401, "body": "denied"}),
    totp("login_totp_answer_is_not_json_with_status_429", {"status": 429, "body": "slow down"}),
    totp("login_totp_answer_is_not_json_with_status_502", {"status": 502, "body": "Bad gateway"}),
    totp("login_totp_answer_is_not_json_with_status_400", {"status": 400, "body": "bad"}),
    totp("login_totp_no_mpin", minted(), mpin=None, request={"totp": "654321"}),
    totp("login_totp_no_code", minted(), code=None, request={"mpin": "123456"}),
    totp("login_totp_empty_mpin", minted(), mpin="", request={"mpin": "", "totp": "654321"}),
    manual("login_manual_no_token", None, secret=""),
]
