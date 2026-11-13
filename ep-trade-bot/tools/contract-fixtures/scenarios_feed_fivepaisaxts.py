"""fivepaisaxts (XTS) feed: the Wisdom scenarios, recorded from this broker's own adapter and client."""

import scenarios_feed_wisdom as w
from scenarios_feed_wisdom import NOW_MS, SYMBOLS, SCENARIOS as _BASE, ENV  # noqa: F401

BROKER = "fivepaisaxts"


def _sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


# More than fifty instruments of one mode: the first fifty go in one request, the rest in the next.
SYMBOLS = SYMBOLS + [dict(w.row(f"S{i:03d}", str(5000 + i))) for i in range(60)]
SCENARIOS = _BASE + [
    {"name": "sixty_instruments_go_in_two_requests", "steps": [{"subscribe": [_sub(f"S{i:03d}", 2) for i in range(60)]}]},
    {"name": "modes_are_grouped_into_requests", "steps": [{"subscribe": [_sub("RELIANCE", 1), _sub("SBIN", 2), _sub("INFY", 1), _sub("NIFTY25NOV26FUT", 2, "NFO"), _sub("RELIANCE", 3)]}]},
    {"name": "reconnect_groups_by_mode", "steps": [{"subscribe": [_sub("RELIANCE", 1), _sub("SBIN", 2), _sub("INFY", 1)]}, {"reconnect": None}]},
]


class Driver(w.Driver):
    def __init__(self):
        super().__init__("fivepaisaxts", "FivepaisaXTSWebSocketAdapter", "FivepaisaXTSWebSocketClient")

    def reset(self):
        super().reset()
        adapter = self.adapter

        def queue_only(items):
            adapter.pending_subscriptions.extend(items)

        adapter._enqueue_subscriptions = queue_only
        adapter.SUBSCRIPTION_DELAY = 0

    def _take(self):
        # The batch processor runs here, once the calls of the step have all been queued.
        if self.adapter.pending_subscriptions:
            self.adapter._process_pending_subscriptions()
        return super()._take()
