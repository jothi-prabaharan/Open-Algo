"""Angel One feed: what the SmartAPI stream is sent to subscribe and what the adapter publishes for the binary frames it gets back."""

import json
import struct

BROKER = "angel"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "angel-private-key", "BROKER_API_SECRET": "angel-secret"}


def row(symbol, brsymbol, exchange, brexchange, token, instrument=""):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN-EQ", "NSE", "NSE", "3045"),
    row("INFY", "INFY-EQ", "NSE", "NSE", "1594"),
    row("RELIANCE", "RELIANCE-EQ", "NSE", "NSE", "2885"),
    row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", "NFO", "35001", "FUTIDX"),
    row("NIFTY", "Nifty 50", "NSE_INDEX", "NSE", "99926000", "AMXIDX"),
    row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", "99919000", "AMXIDX"),
    row("CRUDEOIL18DEC30FUT", "CRUDEOIL18DEC30FUT", "MCX", "MCX", "234230", "FUTCOM"),
    row("TCS", "TCS-EQ", "BSE", "BSE", "532540"),
]


def head(mode, exchange_type, token, ltp, seq=10100759, exch_ts=1793699999000):
    return struct.pack("<BB25sqqq", mode, exchange_type, token.encode(), seq, exch_ts, round(ltp * 100))


def ltp_frame(exchange_type, token, price):
    return head(1, exchange_type, token, price)


def quote_frame(exchange_type, token, price, o=800.0, h=810.0, l=795.5, c=799.95):
    body = struct.pack("<qqqddqqqq", 5, round(800.5 * 100), 1234567, 170.0, 97.0, round(o * 100), round(h * 100), round(l * 100), round(c * 100))
    return head(2, exchange_type, token, price) + body


def snap_frame(exchange_type, token, price, flags=(0, 0, 0, 0, 0, 1, 1, 1, 1, 1)):
    base = quote_frame(exchange_type, token, price)
    base = bytes([3]) + base[1:]
    extra = struct.pack("<qqq", 1793699900000, 12000, 25)
    levels = b"".join(struct.pack("<HqqH", flag, 100 * (i + 1), round((price + (0.05 * (i + 1) if flag else -0.05 * (i + 1))) * 100), i + 1) for i, flag in enumerate(flags))
    limits = struct.pack("<qqqq", round(price * 1.1 * 100), round(price * 0.9 * 100), round(price * 1.5 * 100), round(price * 0.5 * 100))
    return base + extra + levels + limits


class Driver:
    """Runs the real adapter and its real client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.angel.streaming.angel_adapter import AngelWebSocketAdapter
        from broker.angel.streaming.smartWebSocketV2 import SmartWebSocketV2

        SmartWebSocketV2._logging_initialized = True  # the client would otherwise open a log file in the working directory
        self.adapter_cls, self.ws_cls = AngelWebSocketAdapter, SmartWebSocketV2

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(auth_token="jwt", api_key="angel-private-key", client_code="C123", feed_token="feed")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        ws.wsapp = FakeSocket()
        ws.on_data = adapter._on_data
        adapter.ws_client, adapter.connected, adapter.running = ws, True, True
        adapter._start_batch_timer = lambda: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.adapter._process_batch_subscriptions()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def reconnect(self, _):
        self.adapter._on_open(None)
        return self._take()

    def frame(self, hex_text):
        try:
            self.ws._on_data(self.ws.wsapp, bytes.fromhex(hex_text), 2, True)
        except Exception:
            pass  # a frame the client cannot read is dropped, as the websocket library does
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(data):
    return {"frame": data.hex()}


SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"subscribe": [sub("INFY", "NSE", 2)]},
        {"subscribe": [sub("RELIANCE", "NSE", 3)]},
    ]},
    {"name": "several_symbols_are_grouped_by_mode_then_exchange_type", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("INFY", "NSE", 2), sub("CRUDEOIL18DEC30FUT", "MCX", 1),
                       sub("RELIANCE", "NSE", 1), sub("NIFTY", "NSE_INDEX", 2), sub("TCS", "BSE", 2), sub("SENSEX", "BSE_INDEX", 1)]},
    ]},
    {"name": "the_same_token_in_two_modes", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
    ]},
    {"name": "unsubscribe_one_mode_of_two", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        {"unsubscribe": sub("SBIN", "NSE", 2)},
    ]},
    {"name": "reconnect_repeats_every_subscription_in_groups", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 2), sub("RELIANCE", "NSE", 1), sub("SBIN", "NSE", 3)]},
        {"unsubscribe": sub("INFY", "NSE", 2)},
        {"reconnect": None},
    ]},
    {"name": "ltp_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_frame(1, "3045", 805.45)),
    ]},
    {"name": "quote_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote_frame(1, "3045", 805.45)),
    ]},
    {"name": "snap_quote_frame_with_depth", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        frame(snap_frame(1, "3045", 805.45)),
    ]},
    {"name": "snap_quote_frame_with_one_sided_depth", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        frame(snap_frame(1, "3045", 805.45, flags=(0,) * 10)),
    ]},
    {"name": "snap_quote_frame_on_a_futures_contract", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(snap_frame(2, "35001", 23980.25)),
    ]},
    {"name": "index_ltp_and_quote_frames", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]},
        frame(ltp_frame(1, "99926000", 24050.5)),
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]},
        frame(quote_frame(1, "99926000", 24050.5)),
    ]},
    {"name": "bse_index_frame", "steps": [
        {"subscribe": [sub("SENSEX", "BSE_INDEX", 1)]},
        frame(ltp_frame(3, "99919000", 79000.25)),
    ]},
    {"name": "mcx_quote_frame", "steps": [
        {"subscribe": [sub("CRUDEOIL18DEC30FUT", "MCX", 2)]},
        frame(quote_frame(5, "234230", 6500.0)),
    ]},
    {"name": "bse_cash_frame", "steps": [
        {"subscribe": [sub("TCS", "BSE", 1)]},
        frame(ltp_frame(3, "532540", 3900.5)),
    ]},
    {"name": "frame_carries_the_subscription_mode_of_the_last_subscribe", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        frame(ltp_frame(1, "3045", 805.45)),
        frame(quote_frame(1, "3045", 805.45)),
    ]},
    {"name": "frame_after_one_of_two_modes_is_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        {"unsubscribe": sub("SBIN", "NSE", 2)},
        frame(ltp_frame(1, "3045", 805.45)),
    ]},
    {"name": "frame_for_a_token_nobody_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_frame(1, "1594", 1500.1)),
    ]},
    {"name": "frame_for_the_right_token_on_the_wrong_exchange", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_frame(3, "3045", 805.45)),
    ]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        frame(ltp_frame(1, "3045", 805.45)),
    ]},
    {"name": "frames_that_carry_no_market_data", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"frame": ""},
        {"frame": "00"},
        {"frame": "0101"},
        {"frame": ltp_frame(1, "3045", 805.45)[:30].hex()},
    ]},
    {"name": "quote_frame_cut_short", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote_frame(1, "3045", 805.45)[:100]),
    ]},
    {"name": "depth_frame_cut_short", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        frame(snap_frame(1, "3045", 805.45)[:300]),
    ]},
    {"name": "frame_with_a_mode_nobody_asked_for", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(bytes([4]) + ltp_frame(1, "3045", 805.45)[1:]),
        frame(bytes([0]) + ltp_frame(1, "3045", 805.45)[1:]),
    ]},
]
