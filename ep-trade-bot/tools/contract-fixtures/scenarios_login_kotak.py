"""Kotak Neo sign-in: a one-time code at POST /login/1.0/tradeApiLogin gives a view token, and the MPIN at POST /login/1.0/tradeApiValidate gives the trading token, the session id and the account's base address. The session is those, the access token and the data centre joined by three colons."""

BROKER = "kotak"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "UCC123", "BROKER_API_SECRET": "access-token"}
ONE, TWO = "POST /login/1.0/tradeApiLogin", "POST /login/1.0/tradeApiValidate"


def good_one(token="view-token", sid="view-sid"):
    return {"body": {"data": {"status": "success", "token": token, "sid": sid}}}


def good_two(**data):
    d = {"status": "success", "token": "trade-token", "sid": "trade-sid", "baseUrl": "https://cis.kotaksecurities.com", "dataCenter": "E43"}
    d.update(data)
    return {"body": {"data": d}}


def sign_in(name, one, two=None, mobile="9876543210", totp="123456", mpin="654321"):
    broker = {ONE: one}
    if two is not None:
        broker[TWO] = two
    return {"name": name, "login": [mobile, totp, mpin], "request": {"mobilenumber": mobile, "totp": totp, "mpin": mpin}, "broker": broker}


SCENARIOS = [
    sign_in("login_returns_the_session", good_one(), good_two()),
    sign_in("login_mobile_with_the_country_code", good_one(), good_two(), mobile="+919876543210"),
    sign_in("login_mobile_with_the_country_digits", good_one(), good_two(), mobile="919876543210"),
    sign_in("login_mobile_with_spaces", good_one(), good_two(), mobile=" +91 98765 43210 "),
    sign_in("login_without_a_data_centre", good_one(), good_two(dataCenter=None)),
    sign_in("login_data_centre_missing", good_one(), {"body": {"data": {"status": "success", "token": "trade-token", "sid": "trade-sid", "baseUrl": "https://cis.kotaksecurities.com"}}}),
    sign_in("login_without_a_base_address", good_one(), good_two(baseUrl="")),
    sign_in("login_base_address_missing", good_one(), {"body": {"data": {"status": "success", "token": "trade-token", "sid": "trade-sid"}}}),
    sign_in("login_totp_refused_with_a_message", {"body": {"errMsg": "Invalid TOTP"}}),
    sign_in("login_totp_refused_with_the_short_message", {"body": {"message": "Bad request"}}),
    sign_in("login_totp_refused_without_a_message", {"body": {}}),
    sign_in("login_totp_status_not_success", {"body": {"data": {"status": "failure", "errMsg": "Locked"}}}),
    sign_in("login_totp_answer_without_a_token", {"body": {"data": {"status": "success"}}}),
    sign_in("login_totp_answer_that_is_not_json", {"status": 502, "body": "Bad gateway"}),
    sign_in("login_totp_answer_data_is_text", {"body": {"data": "oops"}}),
    sign_in("login_mpin_refused_with_a_message", good_one(), {"body": {"errMsg": "Invalid MPIN"}}),
    sign_in("login_mpin_refused_with_the_short_message", good_one(), {"body": {"message": "Bad pin"}}),
    sign_in("login_mpin_refused_without_a_message", good_one(), {"body": {}}),
    sign_in("login_mpin_status_not_success", good_one(), {"body": {"data": {"status": "failure"}}}),
    sign_in("login_mpin_answer_without_a_token", good_one(), {"body": {"data": {"status": "success", "sid": "x"}}}),
    sign_in("login_mpin_answer_that_is_not_json", good_one(), {"status": 500, "body": "down"}),
]


def patch():
    pass
