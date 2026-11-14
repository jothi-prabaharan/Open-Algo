"""Samco sign-in: there is no redirect and nothing to type. The app's API key and secret from settings are exchanged at POST /session/token for a session token that lasts until 08:00 IST the next day."""

BROKER = "samco"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "samco-api-key", "BROKER_API_SECRET": "samco-api-secret"}

URL = "POST /session/token"
GOOD = {"body": {"status": "Success", "statusMessage": "ok", "sessionToken": "samco-session-777", "accountID": "AB1234", "srcIp": "203.0.113.5", "primaryIp": "203.0.113.5", "secondaryIp": ""}}


def sign_in(name, reply):
    return {"name": name, "login": [], "request": {}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_key_and_secret", GOOD),
    sign_in("login_from_an_address_that_is_not_registered", {"body": {"status": "Success", "sessionToken": "samco-session-778", "srcIp": "198.51.100.9", "primaryIp": "203.0.113.5", "secondaryIp": ""}}),
    sign_in("login_with_no_registered_address", {"body": {"status": "Success", "sessionToken": "samco-session-779", "srcIp": "198.51.100.9", "primaryIp": "", "secondaryIp": ""}}),
    sign_in("login_refused_with_a_message", {"status": 400, "body": {"status": "Failure", "statusMessage": "Invalid credentials"}}),
    sign_in("login_refused_with_an_unknown_key", {"status": 400, "body": {"status": "Failure", "statusMessage": "Invalid API key", "errorCode": "EOAUTH001"}}),
    sign_in("login_refused_with_a_wrong_secret", {"status": 400, "body": {"status": "Failure", "statusMessage": "Invalid API secret", "errorCode": "EOAUTH008"}}),
    sign_in("login_refused_for_an_unregistered_address", {"status": 403, "body": {"status": "Failure", "statusMessage": "IP not whitelisted", "errorCode": "EOAUTH009"}}),
    sign_in("login_refused_with_an_unknown_error_code", {"status": 400, "body": {"status": "Failure", "statusMessage": "Something else", "errorCode": "EOAUTH999"}}),
    sign_in("login_refused_without_a_message", {"status": 400, "body": {"status": "Failure"}}),
    sign_in("login_success_without_a_token", {"body": {"status": "Success", "statusMessage": "ok"}}),
    sign_in("login_success_with_an_empty_token", {"body": {"status": "Success", "sessionToken": ""}}),
    sign_in("login_a_token_without_a_success_status", {"body": {"status": "Failure", "sessionToken": "samco-session-780"}}),
]


def patch():
    pass
