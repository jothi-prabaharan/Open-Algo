"""Dhan feed, the 20-level connection (wss://depth-api-feed.dhan.co/twentydepth): a subscriber asks for it by adding :20 to the symbol (SBIN:20), only on NSE and NFO and only in depth mode. The socket is sent RequestCode 23 with the instruments in batches of
fifty; every message is led by a 12-byte header (length, response code, exchange segment, security id) and the bid side (code 41) and the ask side (code 51) each carry twenty 16-byte levels (price as a double, quantity, orders). A tick is published, to the topic of the
symbol with its :20, when both sides of a security have arrived. Run on the real adapter and its real 20-level client with a fake socket. (OpenAlgo cannot unsubscribe such a subscription through the adapter, which asks for the token of SBIN:20; that is not recorded.)"""

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from scenarios_feed_dhan import ENV, NOW_MS, row  # noqa: E402,F401

BROKER = "dhan"
SYMBOLS = [
    row("SBIN", "NSE", "3045"),
    row("INFY", "NSE", "1594"),
    row("NIFTY25NOV26FUT", "NSE_FNO", "35001", "NFO", "FUTIDX"),
    row("TATAMOTORS", "BSE", "500570", "BSE"),
    row("SAMEID", "NSE", "7777"),
    row("SAMEID", "NFO", "7777", "NFO", "FUTIDX"),
] + [row(f"SYM{i:04d}", "NSE", str(10000 + i)) for i in range(60)]
# The feed host finds the instrument by the symbol without its :20; the recording keeps a row for the symbol as it is asked for, which the runner looks up by.
SYMBOLS += [dict(r, symbol=r["symbol"] + ":20") for r in list(SYMBOLS)]


def packet(code, segment, security_id, payload):
    return struct.pack("<HBBII", 12 + len(payload), code, segment, security_id, 0) + payload


def levels(base, step=0.05, quantity=100, orders=3, count=20):
    return b"".join(struct.pack("<dII", round(base + step * i, 2), quantity * (i + 1), orders + i) for i in range(count))


def bid(segment, security_id, base=805.4, count=20):
    return packet(41, segment, security_id, levels(base, -0.05, 100, 3, count))


def ask(segment, security_id, base=805.5, count=20):
    return packet(51, segment, security_id, levels(base, 0.05, 110, 2, count))


class Driver:
    """Runs the real adapter and its real 20-level client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.dhan.streaming.dhan_adapter import DhanWebSocketAdapter
        from broker.dhan.streaming.dhan_websocket import DhanWebSocket

        self.adapter_cls, self.ws_cls = DhanWebSocketAdapter, DhanWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        five = self.ws_cls("1100000001", "dhan-token", False)
        five.ws = FakeSocket()
        five.connected = True
        five._start_health_check = lambda: None
        twenty = self.ws_cls("1100000001", "dhan-token", True)
        twenty.ws = FakeSocket()
        twenty.connected = True
        twenty._start_health_check = lambda: None
        twenty.on_open = adapter._on_open_20depth
        twenty.on_data = adapter._on_data_20depth
        twenty.on_error = adapter._on_error_20depth
        twenty.on_close = adapter._on_close_20depth
        five.on_open, five.on_data, five.on_error, five.on_close = adapter._on_open_5depth, adapter._on_data_5depth, adapter._on_error_5depth, adapter._on_close_5depth
        adapter.ws_client_5depth, adapter.ws_client_20depth = five, twenty
        adapter.running, adapter.connected = True, True
        adapter._start_batch_timer = lambda: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, twenty

    def _take(self, extra=None):
        out = {"sent": list(self.sent), "published": list(self.published)}
        if extra:
            out.update(extra)
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        errors = []
        for item in items:
            result = self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"], item.get("depth_level", 20))
            if result.get("status") != "success":
                errors.append(result.get("error_code"))
        self.adapter._process_batch_subscriptions()
        return self._take({"errors": errors} if errors else None)

    def reconnect(self, _):
        self.ws._on_open(None)
        return self._take()

    def frame(self, hex_text):
        self.ws._on_message(None, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, exchange="NSE", mode=3, depth=20):
    return {"symbol": symbol, "exchange": exchange, "mode": mode, "depth_level": depth}


def frame(data):
    return {"frame": data.hex()}


SCENARIOS = [
    {"name": "subscribe_one", "steps": [{"subscribe": [sub("SBIN:20")]}]},
    {"name": "subscribe_a_future", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT:20", "NFO")]}]},
    {"name": "subscribe_several_at_once", "steps": [{"subscribe": [sub("SBIN:20"), sub("INFY:20"), sub("NIFTY25NOV26FUT:20", "NFO")]}]},
    {"name": "fifty_go_in_one_request", "steps": [{"subscribe": [sub(f"SYM{i:04d}:20") for i in range(50)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("SBIN:20")]}, {"subscribe": [sub("INFY:20")]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("SBIN:20")]}, {"subscribe": [sub("SBIN:20")]}]},
    {"name": "reconnect_repeats_every_subscription", "steps": [{"subscribe": [sub("SBIN:20"), sub("INFY:20")]}, {"reconnect": None}]},
    {"name": "reconnect_with_nothing_subscribed", "steps": [{"reconnect": None}]},
    {"name": "a_tick_when_both_sides_have_arrived", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045)), frame(ask(1, 3045))]},
    {"name": "a_tick_when_both_sides_come_in_one_frame", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045) + ask(1, 3045))]},
    {"name": "the_ask_side_first", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(ask(1, 3045)), frame(bid(1, 3045))]},
    {"name": "one_side_alone_publishes_nothing", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045))]},
    {"name": "a_side_that_comes_twice_keeps_the_last", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045, 800.0)), frame(bid(1, 3045, 805.4)), frame(ask(1, 3045))]},
    {"name": "two_ticks_in_a_row", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045) + ask(1, 3045)), frame(bid(1, 3045, 804.0) + ask(1, 3045, 804.1))]},
    {"name": "a_future_tick", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT:20", "NFO")]}, frame(bid(2, 35001, 24000.0) + ask(2, 35001, 24000.5))]},
    {"name": "two_securities_in_one_frame", "steps": [{"subscribe": [sub("SBIN:20"), sub("INFY:20")]}, frame(bid(1, 3045) + bid(1, 1594, 1500.0) + ask(1, 1594, 1500.5) + ask(1, 3045))]},
    {"name": "a_security_nobody_asked_for_publishes_nothing", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 1594) + ask(1, 1594))]},
    {"name": "the_segment_must_match", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(2, 3045) + ask(2, 3045))]},
    {"name": "two_exchanges_share_a_security_id_and_the_segment_decides", "steps": [{"subscribe": [sub("SAMEID:20"), sub("SAMEID:20", "NFO")]}, frame(bid(2, 7777) + ask(2, 7777))]},
    {"name": "a_payload_shorter_than_twenty_levels_is_dropped", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(bid(1, 3045, count=19) + ask(1, 3045))]},
    {"name": "a_message_cut_short_ends_the_frame", "steps": [{"subscribe": [sub("SBIN:20")]}, frame((bid(1, 3045) + ask(1, 3045))[:-10])]},
    {"name": "an_unknown_response_code_publishes_nothing", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(packet(7, 1, 3045, b"\x00" * 320)), frame(packet(0, 1, 3045, b"\x00" * 4))]},
    {"name": "a_frame_with_less_than_a_header", "steps": [{"subscribe": [sub("SBIN:20")]}, {"frame": "00" * 11}]},
    {"name": "levels_with_zero_values", "steps": [{"subscribe": [sub("SBIN:20")]}, frame(packet(41, 1, 3045, b"\x00" * 320) + packet(51, 1, 3045, b"\x00" * 320))]},
]
