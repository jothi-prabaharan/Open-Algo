"""Dhan sign-in: a consent token id is exchanged at /app/consumeApp-consent (or a pasted access token is taken), proven with the fund limits, and
refused when it is for another account than the client id saved with the key. The steps live in blueprints/brlogin.py, so sign_in_steps repeats
them in order over the real Dhan functions."""

BROKER = "dhan"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "DH1000:::dhan-app-key", "BROKER_API_SECRET": "dhan-app-secret"}

GENERATE = "POST /app/generate-consent"
CONSUME = "POST /app/consumeApp-consent"
FUNDS = "GET /v2/fundlimit"
LONG_TOKEN = "J" * 140
CONSENT_OK = {"body": {"accessToken": "dhan-access-777", "dhanClientId": "DH1000", "dhanClientName": "T", "givenPowerOfAttorney": False}}
FUNDS_OK = {"body": {"dhanClientId": "DH1000", "availabelBalance": 100.0}}


def _mismatch(reported):
    configured = "DH1000"
    if not reported:
        return (None, None, "Dhan did not confirm which account this login is for, so it was not stored. Log in again.", 409)
    if str(reported).strip() == configured:
        return None
    return (None, None, "This Dhan login is for a different account than the client ID saved for Dhan. Log in with that account, or change the client ID under Broker Configuration.", 409)


def sign_in_steps(code):
    from broker.dhan.api.auth_api import authenticate_broker, consume_consent, generate_consent
    from broker.dhan.api.funds import get_client_id, test_auth_token

    # Starting a sign-in comes first, as /dhan/initiate-oauth does it: the consent is asked for under the configured client id.
    generate_consent("DH1000")

    if len(code) > 100:
        token, error = authenticate_broker(code)
        if not token:
            return (None, None, error or "Invalid access token", 401)
        valid, why = test_auth_token(token)
        if not valid:
            return (None, None, f"Token validation failed: {why}", 401)
        refused = _mismatch(get_client_id(token))
        return refused or (token, None, None)

    token, details = consume_consent(code)
    if not token or not isinstance(details, dict):
        return (None, None, details if isinstance(details, str) else "Authentication failed", 400)
    refused = _mismatch(details.get("dhan_client_id"))
    if refused:
        return refused
    valid, why = test_auth_token(token)
    if not valid:
        return (None, None, f"Authentication validation failed: {why}", 400)
    return (token, None, None)


def sign_in(name, consume=None, funds=None, code="tok-abc"):
    broker = {GENERATE: {"body": {"status": "success", "consentAppId": "consent-app-1"}}}
    if consume is not None:
        broker[CONSUME] = consume
    if funds is not None:
        broker[FUNDS] = funds
    return {"name": name, "login": [code], "request": {"request_token": code, "state": "<issued>"}, "broker": broker}


SCENARIOS = [
    sign_in("login_exchanges_the_consent_token", CONSENT_OK, FUNDS_OK),
    sign_in("login_consent_refused", {"status": 500, "body": {"error": "x"}}),
    sign_in("login_consent_without_an_access_token", {"body": {"dhanClientId": "DH1000"}}),
    sign_in("login_consent_for_another_account", {"body": {"accessToken": "dhan-access-777", "dhanClientId": "DH2000"}}, FUNDS_OK),
    sign_in("login_consent_names_no_account", {"body": {"accessToken": "dhan-access-777"}}, FUNDS_OK),
    sign_in("login_token_fails_the_fund_limits_check", CONSENT_OK, {"body": {"errorType": "Invalid_Authentication", "errorMessage": "Client ID or user generated consent is invalid"}}),
    sign_in("login_pasted_access_token", None, FUNDS_OK, code=LONG_TOKEN),
    sign_in("login_pasted_access_token_for_another_account", None, {"body": {"dhanClientId": "DH2000"}}, code=LONG_TOKEN),
    sign_in("login_pasted_access_token_refused_by_dhan", None, {"body": {"errorType": "Invalid_Authentication", "errorMessage": "Token expired"}}, code=LONG_TOKEN),
    sign_in("login_token_fails_the_fund_limits_check_by_status", CONSENT_OK, {"body": {"status": "error", "errors": "Account blocked"}}),
]
