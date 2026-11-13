"""iBulls (XTS) market data: the Wisdom Capital cases with the service under /apibinarymarketdata."""

import scenarios_wisdom_data as _d
from _rekey import rekey
from scenarios_wisdom_data import AUTH_TOKEN, ENV, SYMBOLS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "ibulls"
SCENARIOS = rekey(_d.SCENARIOS)
