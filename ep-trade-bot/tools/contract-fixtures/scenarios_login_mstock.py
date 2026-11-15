"""mStock sign-in: the password and the one-time code from the authenticator go to /connect/login with the client code; the refresh token it answers is verified at /session/verifytotp with the API key and the same code, and the answer carries the jwtToken and the feedToken."""

BROKER = "mstock"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "ms-client-code", "BROKER_API_SECRET": "ms-private-key"}
LOGIN = "POST /openapi/typeb/connect/login"
VERIFY = "POST /openapi/typeb/session/verifytotp"


def sign_in_steps(password, totp):
    from broker.mstock.api.auth_api import authenticate_with_totp

    token, feed, error = authenticate_with_totp(password, totp)
    return token, feed, error


def step(name, login, verify=None, password="pw-1", totp="123456"):
    broker = {LOGIN: login}
    if verify is not None:
        broker[VERIFY] = verify
    return {"name": name, "login": [password, totp], "request": {"password": password, "totp": totp}, "broker": broker}


def logged_in(**extra):
    d = {"refreshToken": "refresh-1"}
    d.update(extra)
    return {"body": {"status": True, "message": "Login ok", "data": d}}


def verified(token="ms-jwt-777", feed="ms-feed-777", **extra):
    d = {"jwtToken": token, "feedToken": feed}
    d.update(extra)
    return {"body": {"status": "true", "message": "SUCCESS", "data": d}}


SCENARIOS = [
    step("login_exchanges_the_code_for_tokens", logged_in(), verified()),
    step("login_the_status_as_text_true", {"body": {"status": "true", "data": {"refreshToken": "r"}}}, verified()),
    step("login_with_a_jwt_token_in_place_of_a_refresh_token", {"body": {"status": True, "data": {"jwtToken": "j-1"}}}, verified()),
    step("login_without_a_feed_token", logged_in(), {"body": {"status": True, "data": {"jwtToken": "ms-jwt-888"}}}),
    step("login_login_refused_with_a_message", {"body": {"status": False, "message": "Invalid password"}}),
    step("login_login_refused_without_a_message", {"body": {"status": False}}),
    step("login_login_status_true_without_data", {"body": {"status": True}}),
    step("login_login_with_no_token_in_it", {"body": {"status": True, "data": {"x": 1}}}),
    step("login_login_http_error_with_a_message", {"status": 401, "body": {"message": "Bad credentials"}}),
    step("login_login_http_error_without_json", {"status": 502, "body": "Bad gateway"}),
    step("login_verify_refused_with_a_message", logged_in(), {"body": {"status": False, "message": "Invalid TOTP"}}),
    step("login_verify_refused_without_a_message", logged_in(), {"body": {"status": False}}),
    step("login_verify_without_a_token", logged_in(), {"body": {"status": True, "data": {"feedToken": "f"}}}),
    step("login_verify_status_true_without_data", logged_in(), {"body": {"status": True}}),
    step("login_verify_http_error_with_a_message", logged_in(), {"status": 400, "body": {"message": "Expired"}}),
    step("login_verify_http_error_without_json", logged_in(), {"status": 500, "body": "oops"}),
    step("login_answer_that_is_not_json", {"status": 200, "body": "<html>"}),
    step("login_answer_that_is_a_list", {"body": [1]}),
]
