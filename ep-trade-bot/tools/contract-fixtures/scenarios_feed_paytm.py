"""Paytm Money feed: what the broadcast stream is sent to subscribe and what the adapter publishes for the binary frames it gets back."""

import json
import struct

BROKER = "paytm"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "paytm-api-key", "BROKER_API_SECRET": "paytm-api-secret"}


def row(symbol, brsymbol, exchange, brexchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN", "NSE", "NSE", "3045"),
    row("INFY", "INFY", "NSE", "NSE", "1594"),
    row("NIFTYBEES", "NIFTYBEES", "NSE", "NSE", "10576", "ETF"),
    row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", "NFO", "35001", "FUTIDX"),
    row("NIFTY25NOV2624000CE", "NIFTY25NOV2624000CE", "NFO", "NFO", "35100", "OPTIDX"),
    row("NIFTY", "NIFTY", "NSE_INDEX", "NSE", "13", "INDEX"),
    row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", "51", "INDEX"),
    row("TCS", "TCS", "BSE", "BSE", "532540"),
    row("GOLDETF", "GOLDETF", "NSE", "NSE", "9999", "ETF"),
]


def ltp_packet(code, token, price, time=1793699999, absolute=1.25, percent=0.15):
    return struct.pack("<BfIIBBff", code, price, time, token, 1, 1, absolute, percent)


def quote_packet(token, price):
    return struct.pack("<BfIIBBIfIIIffffffff", 62, price, 1793699999, token, 1, 2, 5, 800.55, 1234567, 170, 97, 800.0, 799.95, 810.0, 795.5, 0.65, 5.2, 900.0, 600.0)


def index_quote_packet(token, price, code=65):
    return struct.pack("<BfIBBffffffff", code, price, token, 1, 2, 24000.0, 23950.0, 24100.0, 23900.0, 100.5, 0.42, 26000.0, 21000.0)


def index_full_packet(token, price):
    return struct.pack("<BfIBBffffffI", 66, price, token, 1, 3, 24000.0, 23950.0, 24100.0, 23900.0, 0.42, 100.5, 1793699999)


def full_packet(token, price, oi=True):
    depth = b"".join(struct.pack("<iihhff", 100 * (i + 1), 90 * (i + 1), i + 1, i + 2, price - 0.05 * (i + 1), price + 0.05 * (i + 1)) for i in range(5))
    body = struct.pack("<fIIBBIfIIIffffffffff", price, 1793699999, token, 1, 3, 5, 800.55, 1234567, 170, 97, 800.0, 799.95, 810.0, 795.5, 5.2, 0.65, 900.0, 600.0, 0, 0)
    body = body[:-8]
    packet = bytes([63]) + depth + body
    if oi:
        packet += struct.pack("<II", 12000, 150)
    return packet


class Driver:
    """Runs the real adapter and its real client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.paytm.streaming.paytm_adapter import PaytmWebSocketAdapter
        from broker.paytm.streaming.paytm_websocket import PaytmWebSocket

        self.adapter_cls, self.ws_cls = PaytmWebSocketAdapter, PaytmWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls("public-access-token")
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
        self.ws._on_message(self.ws.wsapp, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(data):
    return {"frame": data.hex()}


SCENARIOS = [
    {"name": "subscribe_in_each_mode_and_each_kind_of_scrip", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY25NOV2624000CE", "NFO", 2),
                       sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 2), sub("NIFTYBEES", "NSE", 1), sub("TCS", "BSE", 1), sub("GOLDETF", "NSE", 1)]},
    ]},
    {"name": "subscribe_one_at_a_time", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"subscribe": [sub("SBIN", "NSE", 3)]},
    ]},
    {"name": "unsubscribe_sends_a_remove", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
    ]},
    {"name": "reconnect_repeats_every_subscription_in_one_message", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]},
        {"unsubscribe": sub("INFY", "NSE", 2)},
        {"reconnect": None},
    ]},
    {"name": "ltp_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_packet(61, 3045, 805.45)),
    ]},
    {"name": "ltp_frame_rounds_a_float_price", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_packet(61, 3045, 805.455, absolute=1.255, percent=0.1549)),
    ]},
    {"name": "quote_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote_packet(3045, 805.45)),
    ]},
    {"name": "full_frame_with_depth_and_open_interest", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(full_packet(35001, 23980.25)),
    ]},
    {"name": "full_frame_without_open_interest", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        frame(full_packet(3045, 805.45, oi=False)),
    ]},
    {"name": "index_ltp_frame", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]},
        frame(ltp_packet(64, 13, 24050.5)),
    ]},
    {"name": "index_quote_frame", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]},
        frame(index_quote_packet(13, 24050.5)),
    ]},
    {"name": "index_full_frame", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]},
        frame(index_full_packet(13, 24050.5)),
    ]},
    {"name": "bse_index_quote_frame", "steps": [
        {"subscribe": [sub("SENSEX", "BSE_INDEX", 2)]},
        frame(index_quote_packet(51, 79000.25)),
    ]},
    {"name": "frame_for_a_different_mode_than_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(quote_packet(3045, 805.45)),
    ]},
    {"name": "two_modes_of_one_token", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        frame(ltp_packet(61, 3045, 805.45)),
        frame(quote_packet(3045, 805.45)),
    ]},
    {"name": "frame_after_one_of_two_modes_is_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        frame(quote_packet(3045, 805.45)),
    ]},
    {"name": "frame_for_a_token_nobody_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp_packet(61, 1594, 1500.1)),
    ]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        frame(ltp_packet(61, 3045, 805.45)),
    ]},
    {"name": "frames_that_carry_no_market_data", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"frame": ""},
        {"frame": "99"},
        {"frame": ltp_packet(61, 3045, 805.45)[:10].hex()},
    ]},
    {"name": "quote_frame_cut_short", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote_packet(3045, 805.45)[:40]),
    ]},
    {"name": "full_frame_cut_short", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        frame(full_packet(3045, 805.45)[:120]),
    ]},
]
