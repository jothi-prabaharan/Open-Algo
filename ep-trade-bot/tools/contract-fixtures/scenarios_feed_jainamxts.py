"""jainamxts (XTS) feed: the Wisdom scenarios, recorded from this broker's own adapter and client."""

import scenarios_feed_wisdom as w
from scenarios_feed_wisdom import NOW_MS, SYMBOLS, SCENARIOS, ENV  # noqa: F401

BROKER = "jainamxts"


class Driver(w.Driver):
    def __init__(self):
        super().__init__("jainamxts", "JainamXTSWebSocketAdapter", "JainamXTSWebSocketClient")
