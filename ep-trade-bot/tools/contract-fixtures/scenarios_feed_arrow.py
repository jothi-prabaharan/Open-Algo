"""Arrow feed: the JSON frames the stream is sent to subscribe (the array key equals the mode) and what the adapter publishes for the big-endian binary packets (one per message, told apart by length)."""

import json
import struct

NOW_MS = 1_793_700_000_123
BROKER = "arrow"
AUTH_TOKEN = "arrow-access-token"
ENV = {"BROKER_API_KEY": "arrow-app-id", "BROKER_API_SECRET": "arrow-api-secret"}


def row(symbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0,
            "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 1001), row("INFY", "NSE", 1002), row("NIFTY25NOV26FUT", "NFO", 50001, "FUT"), row("NIFTY", "NSE_INDEX", 26000, "INDEX"),
    row("SENSEX", "BSE_INDEX", 26001, "INDEX"), row("GOLDM25NOV26FUT", "MCX", 80001, "FUT"), row("BADTOKEN", "NSE", "abc"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i) for i in range(1002)]
T = {"SBIN": 1001, "INFY": 1002, "FUT": 50001, "NIFTY": 26000}


def ltp(token, price):
    return struct.pack(">II", token, round(price * 100)) + b"\x00" * 5


def ltpc(token, price, close):
    return struct.pack(">II", token, round(price * 100)) + b"\x00" * 5 + struct.pack(">I", round(close * 100))


def quote(token, price, ltq=25, avg=800.5, tbq=5000, tsq=4000, o=800.0, h=810.0, c=799.95, lo=795.5, volume=1234567, ltt=1793699900, feed=1793699999, oi=12000):
    return struct.pack(">II5xIIQQIIIIQIIQQQ", token, round(price * 100), ltq, round(avg * 100), tbq, tsq, round(o * 100), round(h * 100), round(c * 100), round(lo * 100), volume, ltt, feed, oi, oi + 100, oi - 100)


def full(token, price, legacy=False, **extra):
    base = quote(token, price, **extra)
    limits = struct.pack(">II", round(price * 90), round(price * 110))
    levels = b"".join(struct.pack(">QIH", 100 * (i + 1), round((price - 0.05 * (i + 1)) * 100), i + 1) for i in range(5)) + b"".join(struct.pack(">QIH", 90 * (i + 1), round((price + 0.05 * (i + 1)) * 100), 2 * (i + 1)) for i in range(5))
    return base + limits + (b"" if legacy else b"\x00" * 8) + levels


class Driver:
    """Runs the real adapter and its real client with a fake socket, the subscription thread replaced by explicit calls."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.arrow.streaming.arrow_adapter import ArrowWebSocketAdapter
        from broker.arrow.streaming.arrow_websocket import ArrowWebSocket

        self.adapter_cls, self.ws_cls = ArrowWebSocketAdapter, ArrowWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls("arrow-app-id", AUTH_TOKEN, on_ticks=adapter._on_ticks, user_id="u")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(text)

        class Alive:
            def is_alive(self):
                return True

        ws.ws, ws.connected, ws.running = FakeSocket(), True, True
        ws._subscription_thread = Alive()
        ws.SUBSCRIPTION_DELAY = 0
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": [json.loads(x) if x.startswith("{") else x for x in self.sent], "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.ws._process_pending_subscriptions()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item.get("mode"))
        return self._take()

    def reconnect(self, _):
        self.ws._resubscribe_all()
        return self._take()

    def frame(self, hex_text):
        self.ws._on_ws_message(self.ws.ws, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(packet):
    return {"frame": packet.hex()}


SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
    ]},
    {"name": "consecutive_symbols_of_one_mode_share_a_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1)]},
    ]},
    {"name": "indices_and_mcx_subscribe_by_token", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 2), sub("GOLDM25NOV26FUT", "MCX", 2)]}]},
    {"name": "a_second_mode_for_a_symbol_sends_the_new_mode", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}]},
    {"name": "a_hundred_and_one_tokens_go_in_two_frames", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(101)]}]},
    {"name": "the_same_symbol_twice_in_one_call_is_sent_twice", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 1)]}]},
    {"name": "unsubscribe_ends_the_token_in_the_mode_it_was_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"unsubscribe": sub("INFY", "NSE", 3)},
    ]},
    {"name": "unsubscribe_of_one_mode_ends_every_mode", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}]},
    {"name": "unsubscribe_of_a_symbol_never_subscribed_is_sent_as_quote", "steps": [{"unsubscribe": sub("SBIN", "NSE", 1)}]},
    {"name": "unsubscribe_twice", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"unsubscribe": sub("SBIN", "NSE", 1)}]},
    {"name": "reconnect_repeats_every_subscription_grouped_by_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": None},
    ]},
    {"name": "reconnect_after_an_unsubscribe_repeats_the_rest", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"reconnect": None},
    ]},
    {"name": "a_hundred_and_one_tokens_are_repeated_in_two_frames", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(101)]}, {"reconnect": None}]},
    {"name": "the_thousand_and_first_instrument_is_refused", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1000)]}, {"subscribe": [sub("TKN1000", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 1)]},
    ]},
    {"name": "a_symbol_without_a_numeric_token_is_refused", "steps": [{"subscribe": [sub("BADTOKEN", "NSE", 1)]}]},
    {"name": "an_unknown_mode_is_asked_as_quote", "steps": [{"subscribe": [sub("SBIN", "NSE", 9)]}]},
    {"name": "ltp_packet", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltp(T["SBIN"], 805.45))]},
    {"name": "ltpc_packet_carries_the_previous_close", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltpc(T["SBIN"], 805.45, 799.95))]},
    {"name": "ltpc_packet_with_a_zero_close_has_no_change", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltpc(T["SBIN"], 805.45, 0))]},
    {"name": "quote_packet", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(quote(T["SBIN"], 805.45))]},
    {"name": "quote_packet_without_a_feed_time_uses_now", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(quote(T["SBIN"], 805.45, feed=0))]},
    {"name": "quote_packet_without_a_trade_time", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(quote(T["SBIN"], 805.45, ltt=0))]},
    {"name": "quote_packet_for_a_ltp_subscription_is_published_as_a_quote_topic", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(quote(T["SBIN"], 805.45))]},
    {"name": "full_packet", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(full(T["FUT"], 24005.5))]},
    {"name": "legacy_full_packet_of_241_bytes", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(full(T["FUT"], 24005.5, legacy=True))]},
    {"name": "full_packet_is_published_to_the_depth_topic_only", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY25NOV26FUT", "NFO", 2)]}, frame(full(T["FUT"], 24005.5)),
    ]},
    {"name": "a_packet_of_an_odd_size_above_the_quote_size_is_a_quote", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(quote(T["SBIN"], 805.45) + b"\x00" * 10)]},
    {"name": "a_packet_of_an_odd_size_above_the_legacy_full_size_is_full", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(full(T["SBIN"], 805.45, legacy=True) + b"\x00" * 3)]},
    {"name": "a_packet_between_the_ltpc_and_quote_sizes_is_a_ltp", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltpc(T["SBIN"], 805.45, 799.95) + b"\x00" * 20)]},
    {"name": "a_packet_shorter_than_thirteen_bytes_is_dropped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": "000003e9000000"}]},
    {"name": "index_ltp_packet", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]}, frame(ltpc(T["NIFTY"], 24050.5, 24000.0))]},
    {"name": "packet_for_a_token_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltp(T["INFY"], 1500.1))]},
    {"name": "packet_after_unsubscribing", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, frame(ltp(T["SBIN"], 805.45))]},
    {"name": "a_text_frame_carries_no_ticks", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": b'{"status":"success"}'.hex()}]},
]
