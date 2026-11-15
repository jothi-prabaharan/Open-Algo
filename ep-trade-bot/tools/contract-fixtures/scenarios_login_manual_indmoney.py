"""IndMoney sign-in with a token pasted into the API secret: /user/profile must accept it; a rejection is refused and an answer that says nothing about the token lets it through."""

from scenarios_login_indmoney import *  # noqa: F401,F403
from scenarios_login_indmoney import manual

ENV = {"BROKER_API_KEY": "ind-client-id", "BROKER_API_SECRET": ""}

SCENARIOS = [
    manual("login_manual_token_accepted", {"body": {"status": "success", "data": {}}}),
    manual("login_manual_token_accepted_with_201", {"status": 201, "body": {}}),
    manual("login_manual_token_rejected_with_401", {"status": 401, "body": {"message": "Token expired"}}),
    manual("login_manual_token_rejected_with_403_and_an_error_type", {"status": 403, "body": {"error_type": "TokenException"}}),
    manual("login_manual_token_rejected_without_a_message", {"status": 401, "body": {}}),
    manual("login_manual_token_rejected_with_a_body_that_is_a_list", {"status": 401, "body": ["x"]}),
    manual("login_manual_token_rejected_with_a_body_that_is_not_json", {"status": 403, "body": "no"}),
    manual("login_manual_token_unverified_after_a_server_error", {"status": 500, "body": {"message": "down"}}),
    manual("login_manual_token_unverified_after_a_throttle", {"status": 429, "body": {}}),
    manual("login_manual_token_unverified_after_a_not_found", {"status": 404, "body": {}}),
]
