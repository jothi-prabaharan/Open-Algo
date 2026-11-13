"""iBulls (XTS) history: the Wisdom Capital cases with the service under /apibinarymarketdata."""

import scenarios_history_wisdom as _h
from _rekey import rekey
from scenarios_history_wisdom import AUTH_TOKEN, ENV, SYMBOLS, SEED_API_KEY_ROW  # noqa: F401

BROKER = "ibulls"
SCENARIOS = rekey(_h.SCENARIOS)
