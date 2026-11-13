"""fivepaisaxts (XTS) scenarios: the Wisdom Capital set, run against this broker's own OpenAlgo plugin."""

from scenarios_wisdom import *  # noqa: F403
from scenarios_wisdom import ENV as _ENV

ENV = dict(_ENV)

# OpenAlgo's base address for this broker may end in a slash, which makes every path start with //interactive.
# The recording uses the address without it, the form EP Trade bot sends.
import importlib as _importlib

_baseurl = _importlib.import_module("broker.fivepaisaxts.baseurl")
_base = "https://xtsmum.5paisa.com"
_baseurl.BASE_URL = _base
_baseurl.MARKET_DATA_URL = _base + "/apimarketdata"
_baseurl.INTERACTIVE_URL = _base + "/interactive"
