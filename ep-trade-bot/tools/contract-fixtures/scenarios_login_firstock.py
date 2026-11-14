"""Firstock sign-in: user id, password (sent as its SHA-256) and a one-time code at POST /V1/login, with the vendor code (the broker key) and the API key (the secret)."""

BROKER = "firstock"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "AB1234_API", "BROKER_API_SECRET": "firstock-api-key"}
URL = "POST /V1/login"


def sign_in(name, reply, user="AB1234", password="Secret#1", totp="123456"):
    return {"name": name, "login": [user, password, totp], "request": {"clientcode": user, "password": password, "totp": totp}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_returns_the_session_key", {"body": {"status": "success", "data": {"susertoken": "firstock-session-777", "userId": "AB1234"}}}),
    sign_in("login_session_key_under_its_old_name", {"body": {"status": "success", "data": {"jKey": "firstock-session-778"}}}),
    sign_in("login_success_without_a_session_key", {"body": {"status": "success", "data": {}}}),
    sign_in("login_refused_with_a_message", {"body": {"status": "failed", "message": "Invalid TOTP"}}),
    sign_in("login_refused_naming_a_field", {"body": {"status": "failed", "message": "Bad", "error": {"field": "TOTP", "message": "TOTP is invalid"}}}),
    sign_in("login_refused_with_an_error_object_and_no_field", {"body": {"status": "failed", "message": "Bad", "error": {"message": "Only a message"}}}),
    sign_in("login_refused_without_a_message", {"body": {"status": "failed"}}),
    sign_in("login_bad_request_with_a_message", {"status": 400, "body": {"status": "failed", "error": {"message": "userId is required"}}}),
    sign_in("login_bad_request_without_a_message", {"status": 400, "body": {"status": "failed"}}),
    sign_in("login_unauthorized", {"status": 401, "body": {"status": "failed"}}),
    sign_in("login_other_http_error", {"status": 503, "body": "Service unavailable"}),
]


def patch():
    pass
