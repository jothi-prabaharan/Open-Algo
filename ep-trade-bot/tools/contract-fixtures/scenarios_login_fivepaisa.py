"""5paisa sign-in: a one-time code login (client code, PIN and a one-time code at TOTPLogin) gives a request token, which GetAccessToken exchanges, with the app secret and the user id, for the access token."""

BROKER = "fivepaisa"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "fp-app-key:::fp-user:::4001234", "BROKER_API_SECRET": "fp-secret"}

TOTP = "POST /VendorsAPI/Service1.svc/TOTPLogin"
TOKEN = "POST /VendorsAPI/Service1.svc/GetAccessToken"
REQUEST_OK = {"body": {"head": {"Status": "0"}, "body": {"RequestToken": "req-token-1", "Message": "Success"}}}
TOKEN_OK = {"body": {"head": {"Status": "0"}, "body": {"AccessToken": "fp-access-777", "Message": "Success"}}}


def sign_in(name, first, second=None, code="user@example.com", pin="123456", totp="654321"):
    broker = {TOTP: first}
    if second is not None:
        broker[TOKEN] = second
    return {"name": name, "login": [code, pin, totp], "request": {"clientcode": code, "password": pin, "totp": totp}, "broker": broker}


SCENARIOS = [
    sign_in("login_exchanges_the_request_token", REQUEST_OK, TOKEN_OK),
    sign_in("login_one_time_code_refused", {"body": {"head": {}, "body": {"Message": "Invalid TOTP"}}}),
    sign_in("login_one_time_code_refused_without_a_message", {"body": {"head": {}, "body": {}}}),
    sign_in("login_one_time_code_answer_has_no_body", {"body": {"head": {}}}),
    sign_in("login_one_time_call_fails", {"status": 500, "body": {"x": 1}}),
    sign_in("login_access_token_refused", REQUEST_OK, {"body": {"head": {}, "body": {"Message": "Invalid encrypted key"}}}),
    sign_in("login_access_token_refused_without_a_message", REQUEST_OK, {"body": {"head": {}, "body": {}}}),
    sign_in("login_access_token_answer_has_no_body", REQUEST_OK, {"body": {"head": {}}}),
    sign_in("login_access_token_call_is_forbidden", REQUEST_OK, {"status": 403, "body": {"x": 1}}),
    sign_in("login_access_token_call_fails", REQUEST_OK, {"status": 401, "body": {"x": 1}}),
]


def patch():
    pass
