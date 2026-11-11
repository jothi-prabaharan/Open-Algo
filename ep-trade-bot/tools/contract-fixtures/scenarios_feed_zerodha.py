"""Zerodha feed: what the Kite ticker is sent to subscribe and what the adapter publishes for the frames it gets back."""

import json
import struct

BROKER = "zerodha"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "kite-api-key", "BROKER_API_SECRET": "kite-api-secret"}


def row(symbol, brsymbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": exchange if "INDEX" not in exchange else "NSE", "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN", "NSE", "779521::::3045"),
    row("INFY", "INFY", "NSE", "408065::::1594"),
    row("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", "13238786::::51714", "FUT"),
    row("NIFTY", "NIFTY 50", "NSE_INDEX", "256265::::1001", "INDEX"),
    row("SENSEX", "SENSEX", "BSE_INDEX", "265::::1", "INDEX"),
    row("CRUDEOIL18DEC30FUT", "CRUDEOIL30DECFUT", "MCX", "70000001::::470001", "FUT"),
]
SYMBOLS += [row(f"TKN{i}", f"TKN{i}", "NSE", f"{900000 + i}::::{i}") for i in range(250)]
T = {"SBIN": 779521, "INFY": 408065, "FUT": 13238786, "NIFTY": 256265, "SENSEX": 265, "CRUDE": 70000001}


def ltp(token, price):
    return struct.pack(">Ii", token, round(price * 100))


def quote(token, price, last_qty=5, avg=800.5, volume=1234567, buy=170, sell=97, o=800.0, h=810.0, l=795.5, c=799.95):
    return struct.pack(">11i", token, round(price * 100), last_qty, round(avg * 100), volume, buy, sell, round(o * 100), round(h * 100), round(l * 100), round(c * 100))


def index_packet(token, price, size=28):
    """An index quote: LTP, high, low, open, close, change; 28 bytes (32 with the exchange time)."""
    body = struct.pack(">Ii5i", token, round(price * 100), round(24100 * 100), round(23900 * 100), round(24000 * 100), round(23950 * 100), round(10 * 100))
    return body + (struct.pack(">I", 1793699999) if size == 32 else b"")


def full(token, price, oi=12000):
    base = quote(token, price)  # 44 bytes
    change = struct.pack(">i", 535)
    last_trade_time = struct.pack(">I", 1793699900)  # 48..52
    oi_fields = struct.pack(">III", oi, oi + 100, oi - 100)  # 52..64 -> OI, day high, day low (exchange time overlays 60..64)
    ts = struct.pack(">I", 1793699999)
    buys = b"".join(struct.pack(">IIHH", 100 * (i + 1), round((price - 0.05 * (i + 1)) * 100), i + 1, 0) for i in range(5))
    sells = b"".join(struct.pack(">IIHH", 90 * (i + 1), round((price + 0.05 * (i + 1)) * 100), i + 1, 0) for i in range(5))
    packet = base + change + last_trade_time + oi_fields[:8] + ts + buys + sells
    return packet.ljust(184, b"\x00")[:180] + struct.pack(">I", oi)


def message(*packets):
    return struct.pack(">H", len(packets)) + b"".join(struct.pack(">H", len(p)) + p for p in packets)


class Driver:
    """Runs the real adapter and its real client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.zerodha.streaming.zerodha_adapter import ZerodhaWebSocketAdapter
        from broker.zerodha.streaming.zerodha_websocket import ZerodhaWebSocket

        self.adapter_cls, self.ws_cls = ZerodhaWebSocketAdapter, ZerodhaWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(api_key="kite-api-key", access_token="kite-access-token", on_ticks=adapter._handle_ticks, user_id="u")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        class Alive:
            def is_alive(self):
                return True

        ws.ws, ws.connected, ws.running = FakeSocket(), True, True
        ws._subscription_thread = Alive()  # the pending queue is drained by hand below
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
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


def frame(*packets):
    return {"frame": message(*packets).hex()}


SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"subscribe": [sub("INFY", "NSE", 2)]},
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
    ]},
    {"name": "several_symbols_are_grouped_by_the_mode_asked_of_kite", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY", "NSE_INDEX", 2)]},
    ]},
    {"name": "a_higher_mode_raises_the_stream_and_a_lower_one_does_not_lower_it", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"subscribe": [sub("SBIN", "NSE", 3)]},
        {"subscribe": [sub("SBIN", "NSE", 1)]},
    ]},
    {"name": "more_than_two_hundred_tokens_go_in_batches", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(250)]},
        {"subscribe": [sub(f"TKN{i}", "NSE", 3) for i in range(5)]},
    ]},
    {"name": "unsubscribe_drops_the_stream_only_when_no_mode_needs_it", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        {"unsubscribe": sub("SBIN", "NSE", 2)},
        {"unsubscribe": sub("SBIN", "NSE", 2)},
    ]},
    {"name": "unsubscribe_without_a_mode_drops_every_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]},
        {"unsubscribe": {"symbol": "SBIN", "exchange": "NSE"}},
    ]},
    {"name": "reconnect_repeats_every_subscription", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]},
        {"reconnect": None},
    ]},
    {"name": "ltp_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp(T["SBIN"], 805.45)),
    ]},
    {"name": "quote_frame_for_a_quote_subscription", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote(T["SBIN"], 805.45)),
    ]},
    {"name": "quote_frame_also_feeds_the_ltp_subscription", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]},
        frame(quote(T["SBIN"], 805.45)),
    ]},
    {"name": "full_frame_fans_out_to_depth_quote_and_ltp", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(full(T["FUT"], 23980.25)),
    ]},
    {"name": "full_frame_for_a_depth_only_subscription", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(full(T["FUT"], 23980.25)),
    ]},
    {"name": "full_frame_with_quote_and_depth_subscribed", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(full(T["FUT"], 23980.25)),
    ]},
    {"name": "index_ltp_frame", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]},
        frame(ltp(T["NIFTY"], 24050.5)),
    ]},
    {"name": "index_quote_frame_of_28_bytes", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]},
        frame(index_packet(T["NIFTY"], 24050.5)),
    ]},
    {"name": "index_quote_frame_of_32_bytes_for_a_depth_subscription", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]},
        frame(index_packet(T["NIFTY"], 24050.5, 32)),
    ]},
    {"name": "bse_index_quote_frame", "steps": [
        {"subscribe": [sub("SENSEX", "BSE_INDEX", 2)]},
        frame(index_packet(T["SENSEX"], 79000.25)),
    ]},
    {"name": "one_frame_with_several_packets", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)]},
        frame(ltp(T["SBIN"], 805.45), ltp(T["INFY"], 1500.1)),
    ]},
    {"name": "mcx_quote_frame", "steps": [
        {"subscribe": [sub("CRUDEOIL18DEC30FUT", "MCX", 2)]},
        frame(quote(T["CRUDE"], 6500.0)),
    ]},
    {"name": "frame_for_a_token_nobody_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(ltp(T["INFY"], 1500.1)),
    ]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        frame(ltp(T["SBIN"], 805.45)),
    ]},
    {"name": "heartbeat_and_short_frames_carry_nothing", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"frame": "00"},
        {"frame": "0001"},
        {"frame": "000100"},
    ]},
    {"name": "frame_cut_off_in_the_middle_of_a_packet", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"frame": message(ltp(T["SBIN"], 805.45), ltp(T["SBIN"], 805.5)).hex()[:-6]},
    ]},
    {"name": "a_quote_sized_packet_is_a_quote_whatever_was_asked", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        frame(quote(T["SBIN"], 805.45)),
    ]},
    {"name": "packet_of_an_odd_length_takes_the_mode_asked_of_kite", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(quote(T["SBIN"], 805.45)[:20]),
    ]},
]
