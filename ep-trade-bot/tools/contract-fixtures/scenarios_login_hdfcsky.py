"""HDFC Sky sign-in: the request token its page returns is exchanged at /oapi/v1/access-token, the api_key and token in the query and the secret in the body."""

BROKER = "hdfcsky"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "sky-api-key", "BROKER_API_SECRET": "sky-api-secret"}

URL = "POST /oapi/v1/access-token"


def sign_in(name, reply, token="req-token-abc"):
    return {"name": name, "login": [token], "request": {"request_token": token, "state": "<issued>"}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_exchanges_the_request_token", {"body": {"accessToken": "hdfc-jwt-777"}}),
    sign_in("login_snake_case_token", {"body": {"access_token": "hdfc-jwt-778"}}),
    sign_in("login_token_in_a_data_envelope", {"body": {"status": "success", "data": {"accessToken": "hdfc-jwt-779"}}}),
    sign_in("login_snake_case_token_in_a_data_envelope", {"body": {"status": "success", "data": {"access_token": "hdfc-jwt-780"}}}),
    sign_in("login_refused_with_a_message", {"status": 401, "body": {"message": "Invalid request token"}}),
    sign_in("login_refused_with_an_error_text", {"status": 400, "body": {"error": "request_token expired"}}),
    sign_in("login_refused_with_nothing_useful", {"status": 500, "body": {"x": 1}}),
    sign_in("login_ok_without_a_token", {"body": {"status": "success", "data": {}}}),
]
