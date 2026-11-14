"""Sahi sign-in: there is no redirect and no code. The key id and secret from settings are exchanged for the day's token at the auth host."""

BROKER = "sahi"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "pk_test_key", "BROKER_API_SECRET": "sahi_sk_test_secret"}

URL = "POST /api/v1/apikeys/token"
GOOD = {"body": {"access_token": "sahi-day-token", "token_type": "Bearer", "expires_in": 65275, "expires_at": "2026-11-14T06:00:00+05:30", "scope": "orders.write"}}


def sign_in(name, reply):
    return {"name": name, "login": ["sahi"], "request": {}, "broker": {URL: reply}}


def refused(status, code, message="m"):
    return {"status": status, "body": {"code": code, "message": message}}


SCENARIOS = [
    sign_in("login_mints_the_days_token", GOOD),
    sign_in("login_key_not_approved_today", refused(403, "Forbidden")),
    sign_in("login_account_locked", refused(401, "Locked")),
    sign_in("login_key_refused", refused(401, "Unauthorized")),
    sign_in("login_too_many_attempts", refused(429, "TooManyRequests")),
    sign_in("login_service_down", {"status": 503, "body": {"x": 1}}),
    sign_in("login_refused_with_a_reason", refused(400, "InvalidRequest", "key_id is malformed")),
    sign_in("login_refused_without_a_reason", {"status": 400, "body": {"x": 1}}),
    sign_in("login_ok_without_a_token", {"body": {"token_type": "Bearer"}}),
    sign_in("login_locked_on_any_status", refused(400, "Locked")),
]
