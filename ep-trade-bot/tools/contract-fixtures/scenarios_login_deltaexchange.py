"""Delta Exchange sign-in: there is no redirect. The key and secret from settings are proven by a signed GET /v2/profile."""

BROKER = "deltaexchange"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "delta-api-key", "BROKER_API_SECRET": "delta-api-secret"}

URL = "GET /v2/profile"


def sign_in(name, reply):
    return {"name": name, "login": ["deltaexchange"], "request": {}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_proves_the_key_with_a_signed_profile_call", {"body": {"success": True, "result": {"email": "t@example.com", "id": 1}}}),
    sign_in("login_profile_says_not_successful", {"body": {"success": False, "error": {"code": "bad"}}}),
    sign_in("login_key_refused", {"status": 401, "body": {"success": False, "error": {"code": "invalid_api_key"}}}),
    sign_in("login_blocked_by_ip_whitelist", {"status": 403, "body": {"success": False}}),
    sign_in("login_unexpected_status", {"status": 500, "body": {"error": "x"}}),
]
