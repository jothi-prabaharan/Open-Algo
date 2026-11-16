"""Motilal sign-in: the client code, the password hashed as SHA-256(password + app key), the date of birth (2FA) and an authenticator code go to POST /rest/login/v7/authdirectapi with no Authorization header. The AuthToken it answers is
joined to the access token that POST /rest/login/v1/getaccesstoken gives (with an API secret set; a failure there never fails the sign-in) as "AuthToken:::accesstoken". A token that is still waiting for its code is refused."""

BROKER = "motilal"
SYMBOLS = []
ENV = {"BROKER_API_KEY": "motilal-api-key", "BROKER_API_SECRET": "motilal-api-secret", "BROKER_VENDOR_CODE": ""}
CLIENT_ID = "AB1234"
LOGIN, ACCESS = "POST /rest/login/v7/authdirectapi", "POST /rest/login/v1/getaccesstoken"


def sign_in(name, reply, access=None, user="AB1234", password="Secret#1", totp="123456", dob="18/10/1988", env=None):
    broker = {LOGIN: reply}
    if access is not None:
        broker[ACCESS] = access
    return {"name": name, "login": [user, password, totp, dob], "request": {"clientcode": user, "password": password, "totp": totp, "dob": dob}, "broker": broker, **({"env": env} if env else {})}


def issued(**extra):
    body = {"status": "SUCCESS", "message": "Success", "errorcode": "", "AuthToken": "mo-auth-token-777", "isAuthTokenVerified": "TRUE"}
    body.update(extra)
    return {"body": body}


ACCESS_OK = {"body": {"status": "SUCCESS", "message": "Success", "errorcode": "", "accesstoken": "mo-access-token-888"}}

SCENARIOS = [
    sign_in("login_with_the_access_token", issued(), ACCESS_OK),
    sign_in("login_access_token_refused_keeps_the_auth_token", issued(), {"body": {"status": "FAILURE", "message": "Bad secret", "errorcode": "MO2005"}}),
    sign_in("login_access_token_http_error_keeps_the_auth_token", issued(), {"status": 500, "body": {}}),
    sign_in("login_access_token_with_no_token_keeps_the_auth_token", issued(), {"body": {"status": "SUCCESS", "accesstoken": ""}}),
    sign_in("login_access_token_answer_not_json_keeps_the_auth_token", issued(), {"status": 200, "body": "<html>"}),
    sign_in("login_without_a_verified_flag_is_accepted", issued(isAuthTokenVerified=None), ACCESS_OK),
    sign_in("login_with_an_unrecognised_verified_flag_is_accepted", issued(isAuthTokenVerified="MAYBE"), ACCESS_OK),
    sign_in("login_verified_flag_in_lower_case", issued(isAuthTokenVerified="true"), ACCESS_OK),
    sign_in("login_token_waiting_for_its_code_is_refused", issued(isAuthTokenVerified="FALSE"), ACCESS_OK),
    sign_in("login_flag_no_is_refused", issued(isAuthTokenVerified="no"), ACCESS_OK),
    sign_in("login_flag_zero_is_refused", issued(isAuthTokenVerified=0), ACCESS_OK),
    sign_in("login_flag_n_is_refused", issued(isAuthTokenVerified="N"), ACCESS_OK),
    sign_in("login_refused_with_a_message_and_code", {"body": {"status": "FAILURE", "message": "Invalid TOTP", "errorcode": "MO1093"}}),
    sign_in("login_refused_with_a_message_only", {"body": {"status": "FAILURE", "message": "Bad"}}),
    sign_in("login_refused_with_no_message", {"body": {"status": "FAILURE"}}),
    sign_in("login_success_with_no_auth_token", {"body": {"status": "SUCCESS", "message": "Success"}}),
    sign_in("login_http_error", {"status": 403, "body": {"status": "FAILURE", "message": "Forbidden"}}),
    sign_in("login_server_error", {"status": 503, "body": ""}),
    sign_in("login_answer_that_is_not_json", {"status": 200, "body": "<html>"}),
    sign_in("login_without_the_authenticator_code", issued(), ACCESS_OK, totp=""),
    sign_in("login_password_with_symbols", issued(), ACCESS_OK, password="p@ss w0rd/é"),
]


def before_each(sc):
    import broker.motilal.api.baseurl as baseurl

    baseurl.set_client_code(None)
