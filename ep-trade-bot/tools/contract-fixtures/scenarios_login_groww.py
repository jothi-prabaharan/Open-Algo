"""Groww sign-in: no page and nothing typed in. The API key is a bearer token and the secret is hashed with the time: POST /v1/token/api/access with {key_type: approval, checksum: SHA-256(secret + timestamp), timestamp}. The answer carries token, tokenRefId, sessionName,
expiry and isActive; a token Groww marks inactive, or one already past its expiry, is refused here rather than failing on the first order. The clock is pinned so the checksum is the same every time."""

import time

BROKER = "groww"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "groww-api-key", "BROKER_API_SECRET": "groww-api-secret"}
RECORDED_AT = "2026-11-11T10:00:00+05:30"
URL = "POST /v1/token/api/access"


def sign_in_steps():
    from datetime import datetime

    from broker.groww.api.auth_api import authenticate_broker

    time.time = lambda: datetime.fromisoformat(RECORDED_AT).timestamp()
    token, error = authenticate_broker(None)
    return token, None, error, 400


def step(name, reply):
    return {"name": name, "login": [], "request": {}, "broker": {URL: reply}}


def issued(**extra):
    body = {"token": "groww-token-777", "tokenRefId": "ref-1", "sessionName": "openalgo", "expiry": "2099-11-12T06:00:00", "isActive": True}
    body.update(extra)
    return {"body": body}


SCENARIOS = [
    step("login_issues_a_token", issued()),
    step("login_with_an_expiry_that_carries_a_zone", issued(expiry="2099-11-12T06:00:00+05:30")),
    step("login_with_no_expiry", issued(expiry=None)),
    step("login_with_an_expiry_that_is_not_a_date", issued(expiry="tomorrow")),
    step("login_with_an_expiry_already_past", issued(expiry="2020-01-01T06:00:00")),
    step("login_with_a_token_that_is_not_active", issued(isActive=False)),
    step("login_with_no_isactive_field", {"body": {"token": "groww-token-778", "expiry": "2099-11-12T06:00:00"}}),
    step("login_with_no_token", {"body": {"tokenRefId": "ref-1", "isActive": True}}),
    step("login_with_an_empty_token", issued(token="")),
    step("login_refused_with_a_reason", {"status": 401, "body": {"status": "FAILURE", "error": {"code": "GA002", "message": "Invalid API key"}}}),
    step("login_refused_with_a_message_only", {"status": 403, "body": {"message": "Key not approved for today"}}),
    step("login_refused_with_no_reason", {"status": 400, "body": {}}),
    step("login_throttled", {"status": 429, "body": {"error": {"message": "Too many requests"}}}),
    step("login_server_error", {"status": 503, "body": {"message": "Service unavailable"}}),
    step("login_answer_that_is_not_json", {"status": 502, "body": "Bad gateway"}),
    step("login_answer_that_is_not_an_object", {"body": ["x"]}),
    step("login_a_200_answer_with_a_reason_and_no_token", {"body": {"error": {"message": "Key expired"}}}),
]


def before_each(sc):
    import importlib

    importlib.import_module("broker.groww.api.rate_limiter").time.sleep = lambda *a, **k: None
