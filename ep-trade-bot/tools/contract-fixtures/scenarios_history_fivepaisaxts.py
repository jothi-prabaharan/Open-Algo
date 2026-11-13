"""fivepaisaxts (XTS) history: the Wisdom Capital cases, run against this broker's own OpenAlgo plugin."""

import scenarios_fivepaisaxts  # noqa: F401  (points the broker's addresses at the form EP Trade bot sends)
from scenarios_history_wisdom import *  # noqa: F403
from scenarios_history_wisdom import ENV as _ENV

BROKER = "fivepaisaxts"
ENV = dict(_ENV)
