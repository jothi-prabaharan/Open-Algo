"""iBulls (XTS) sign-in: the Wisdom Capital cases against iBulls' own plugin, with market data under /apibinarymarketdata."""

import importlib as _importlib

import scenarios_login_wisdom as _w
from _rekey import rekey
from scenarios_login_wisdom import ENV  # noqa: F401

BROKER = "ibulls"
SYMBOLS = []
SCENARIOS = rekey(_w.SCENARIOS)


def sign_in_steps(_unused):
    authenticate_broker = _importlib.import_module(f"broker.{BROKER}.api.auth_api").authenticate_broker
    token, feed, _user, error = authenticate_broker(_unused)
    return token, feed, error, 400
