"""iifl (XTS) sign-in: the Wisdom Capital cases, run against this broker's own OpenAlgo plugin."""

import importlib as _importlib

from scenarios_login_wisdom import *  # noqa: F403
from scenarios_login_wisdom import ENV as _ENV

BROKER = "iifl"
ENV = dict(_ENV)

_baseurl = _importlib.import_module("broker.iifl.baseurl")
_base = "https://ttblaze.iifl.com"
_baseurl.BASE_URL = _base
_baseurl.MARKET_DATA_URL = _base + "/apimarketdata"
_baseurl.INTERACTIVE_URL = _base + "/interactive"


def sign_in_steps(_unused):
    authenticate_broker = _importlib.import_module(f"broker.{BROKER}.api.auth_api").authenticate_broker
    token, feed, _user, error = authenticate_broker(_unused)
    return token, feed, error, 400
