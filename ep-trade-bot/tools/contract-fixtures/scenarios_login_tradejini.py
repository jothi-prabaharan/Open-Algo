"""Tradejini sign-in: the password (the CubePlus PIN), the one-time code and its kind go to the individual-token service with the app API key as the bearer token; the access token it answers is the session, and every later call sends it as <api key>:<access token>."""

BROKER = "tradejini"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "tj-app-api-key"}
EP = "POST /v2/api-gw/oauth/individual-token-v2"


def step(name, reply, password="1234", twofa="123456", kind="totp", request=None):
    return {"name": name, "login": [password, twofa, kind], "request": request or {"password": password, "totp": twofa, "twofatype": kind}, "broker": {EP: reply}}


def issued(**extra):
    d = {"scope": "general", "access_token": "tj-access-777", "token_type": "bearer", "expires_in": 86400}
    d.update(extra)
    return {"body": d}


SCENARIOS = [
    step("login_with_a_totp", issued()),
    step("login_with_an_otp", issued(), kind="otp"),
    step("login_with_the_kind_in_capitals", issued(), kind="TOTP"),
    step("login_with_a_kind_the_service_does_not_know_is_a_totp", issued(), kind="sms"),
    step("login_with_no_kind_is_a_totp", issued(), kind=None, request={"password": "1234", "totp": "123456"}),
    step("login_with_a_token_type_in_capitals", issued(token_type="Bearer")),
    step("login_with_no_token_type", {"body": {"access_token": "tj-access-888"}}),
    step("login_with_a_token_type_that_is_not_bearer", issued(token_type="mac")),
    step("login_with_no_access_token", {"body": {"token_type": "bearer"}}),
    step("login_refused_with_a_message", {"status": 400, "body": {"s": "error", "msg": "Invalid two factor code"}}),
    step("login_refused_with_a_message_field", {"status": 403, "body": {"message": "Account locked"}}),
    step("login_refused_without_a_message", {"status": 500, "body": {"s": "error"}}),
    step("login_refused_as_unauthorized", {"status": 401, "body": {"s": "error", "msg": "Unauthorized"}}),
    step("login_refused_as_unauthorized_without_a_message", {"status": 401, "body": {}}),
    step("login_with_an_answer_that_is_not_json", {"status": 200, "body": "<html>"}),
    step("login_with_an_error_that_is_not_json", {"status": 502, "body": "Bad gateway"}),
    step("login_with_no_password", issued(), password=None, request={"totp": "123456", "twofatype": "totp"}),
    step("login_with_no_code", issued(), twofa=None, request={"password": "1234", "twofatype": "totp"}),
    step("login_with_an_empty_password", issued(), password="", request={"password": "", "totp": "123456", "twofatype": "totp"}),
]
