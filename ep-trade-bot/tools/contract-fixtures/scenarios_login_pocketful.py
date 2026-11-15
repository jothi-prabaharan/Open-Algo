"""Pocketful sign-in: the code its page returns is exchanged at /oauth2/token (form body, Basic key:secret, with the redirect address), and the client id is read from /api/v1/user/trading_info with the token. A token with no client id is not a sign-in."""

BROKER = "pocketful"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "pf-client-key", "BROKER_API_SECRET": "pf-client-secret", "REDIRECT_URL": "http://127.0.0.1:5000/pocketful/callback"}

TOKEN = "POST /oauth2/token"
INFO = "GET /api/v1/user/trading_info"
GOOD = {"body": {"access_token": "pf-access-777", "token_type": "bearer", "expires_in": 86400}}
CLIENT = {"body": {"status": "success", "data": {"client_id": "PF100"}}}


def sign_in_steps(code, state=None):
    import importlib

    auth = importlib.import_module("broker.pocketful.api.auth_api")
    token, feed, client, error = auth.authenticate_broker(code, state)
    return token, feed, error, 400


def sign_in(name, token_reply, info_reply=None, code="auth-code-abc"):
    replies = {TOKEN: token_reply}
    if info_reply is not None:
        replies[INFO] = info_reply
    return {"name": name, "login": [code, "<issued>"], "request": {"code": code, "state": "<issued>"}, "broker": replies}


SCENARIOS = [
    sign_in("login_exchanges_the_code", GOOD, CLIENT),
    sign_in("login_refused_with_a_message", {"status": 400, "body": {"message": "Invalid authorization code"}}),
    sign_in("login_refused_without_a_message", {"status": 400, "body": {"error": "invalid_grant"}}),
    sign_in("login_refused_with_an_answer_that_is_not_json", {"status": 502, "body": "Bad gateway"}),
    sign_in("login_refused_with_a_server_error", {"status": 500, "body": {"message": "boom"}}),
    sign_in("login_no_access_token_in_the_answer", {"body": {"token_type": "bearer"}}),
    sign_in("login_empty_access_token", {"body": {"access_token": ""}}),
    sign_in("login_the_client_id_is_refused", GOOD, {"body": {"status": "error", "message": "Session expired"}}),
    sign_in("login_the_client_id_is_refused_without_a_message", GOOD, {"body": {"status": "error"}}),
    sign_in("login_the_client_id_is_missing", GOOD, {"body": {"status": "success", "data": {}}}),
    sign_in("login_the_client_id_answer_has_no_data", GOOD, {"body": {"status": "success"}}),
    sign_in("login_the_client_id_read_fails_with_http_401", GOOD, {"status": 401, "body": {"message": "Unauthorized"}}),
    sign_in("login_the_client_id_read_fails_with_http_500", GOOD, {"status": 500, "body": {"x": 1}}),
]
