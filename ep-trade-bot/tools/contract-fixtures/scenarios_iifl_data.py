"""iifl (XTS) market data: the Wisdom Capital cases, run against this broker's own OpenAlgo plugin."""

import scenarios_iifl  # noqa: F401  (points the broker's addresses at the form EP Trade bot sends)
from scenarios_wisdom_data import *  # noqa: F403
from scenarios_wisdom_data import ENV as _ENV

BROKER = "iifl"
ENV = dict(_ENV)
