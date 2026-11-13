"""ibulls (XTS) feed: the Wisdom scenarios, recorded from this broker's own adapter and client."""

import scenarios_feed_wisdom as w
from scenarios_feed_wisdom import NOW_MS, SYMBOLS, SCENARIOS, ENV  # noqa: F401

BROKER = "ibulls"


class Driver(w.Driver):
    def __init__(self):
        super().__init__("ibulls", "IbullsWebSocketAdapter", "IbullsWebSocketClient")
        # OpenAlgo's iBulls client reads a BASE_URL it never defines, so it cannot even be built; the recording gives it the address it was meant to use.
        self.client_cls.BASE_URL = "https://xts.ibullssecurities.com"
