"""TradeSmart sign-in: the code its page returns is exchanged at GenAcsTok with a SHA-256 checksum of the client id, the secret and the code; the answer may carry the user id."""

BROKER = "tradesmart"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "FA12345:::tradesmart-client-id", "BROKER_API_SECRET": "tradesmart-secret"}

URL = "POST /NorenWClientAPIv2/GenAcsTok"
GOOD = {"body": {"stat": "Ok", "access_token": "tradesmart-access-777", "actid": "TS0001", "request_time": "10:20:30 03-11-2026"}}


def sign_in(name, reply, code="auth-code-abc"):
    return {"name": name, "login": [code], "request": {"code": code, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_code_and_keeps_the_user_id", GOOD),
    sign_in("login_code_with_odd_characters", GOOD, code="a/b+c=d&e"),
    sign_in("login_token_without_a_user_id", {"body": {"stat": "Ok", "access_token": "tradesmart-access-777"}}),
    sign_in("login_token_under_another_name", {"body": {"stat": "Ok", "accesstoken": "tok-2", "uid": "U9"}}),
    sign_in("login_token_under_a_third_name", {"body": {"stat": "Ok", "token": "tok-3", "accountId": "A7"}}),
    sign_in("login_token_under_a_fourth_name", {"body": {"stat": "Ok", "susertoken": "tok-4", "uname": "N5"}}),
    sign_in("login_user_id_as_client_id", {"body": {"stat": "Ok", "access_token": "tok-5", "client_id": "C3"}}),
    sign_in("login_both_user_ids_prefers_the_account_id", {"body": {"stat": "Ok", "access_token": "tok-6", "actid": "ACT1", "uid": "UID2"}}),
    sign_in("login_refused_with_a_message", {"body": {"stat": "Not_Ok", "emsg": "Invalid code"}}),
    sign_in("login_refused_without_a_message", {"body": {"stat": "Not_Ok"}}),
    sign_in("login_ok_without_a_token", {"body": {"stat": "Ok"}}),
    sign_in("login_token_without_ok_status", {"body": {"access_token": "x"}}),
    sign_in("login_http_error", {"status": 500, "body": {"error": "down"}}),
]
