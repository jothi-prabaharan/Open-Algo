"""Angel One sign-in: client code, PIN and a one-time code at loginByPassword."""

BROKER = "angel"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "angel-private-key", "BROKER_API_SECRET": "angel-secret"}

URL = "POST /rest/auth/angelbroking/user/v1/loginByPassword"
GOOD = {"body": {"status": True, "message": "SUCCESS", "errorcode": "", "data": {"jwtToken": "angel-jwt-777", "refreshToken": "r", "feedToken": "angel-feed-888"}}}
NO_FEED = {"body": {"status": True, "message": "SUCCESS", "errorcode": "", "data": {"jwtToken": "angel-jwt-777"}}}


def sign_in(name, reply, code="A12345", pin="1234", totp="123456"):
    return {"name": name, "login": [code, pin, totp], "request": {"clientcode": code, "password": pin, "totp": totp}, "broker": {URL: reply}}


SCENARIOS = [
    sign_in("login_returns_token_and_feed_token", GOOD),
    sign_in("login_without_a_feed_token", NO_FEED),
    sign_in("login_wrong_one_time_code", {"body": {"status": False, "message": "Invalid totp", "errorcode": "AB1050", "data": None}}),
    sign_in("login_wrong_pin", {"body": {"status": False, "message": "Invalid Password", "errorcode": "AB1007", "data": None}}),
    sign_in("login_refused_without_a_message", {"body": {"status": False}}),
]
