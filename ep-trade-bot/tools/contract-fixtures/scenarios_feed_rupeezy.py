"""Rupeezy feed: what the Vortex socket is sent to subscribe and what the adapter publishes for the binary frames it gets back."""

import json
import struct

BROKER = "rupeezy"
NOW_MS = 1_793_700_000_123
AUTH_TOKEN = "rupeezy-access-token"
ENV = {"BROKER_API_KEY": "rupeezy-app-id", "BROKER_API_SECRET": "rupeezy-api-key"}


def row(symbol, brsymbol, exchange, brexchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token),
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN", "NSE", "NSE_EQ", 3045),
    row("INFY", "INFY", "NSE", "NSE_EQ", 1594),
    row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", "NSE_FO", 35001, "FUT"),
    row("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", "MCX_FO", 80001, "FUT"),
    row("NIFTY", "NIFTY", "NSE_INDEX", "NSE_EQ", 26000, "INDEX"),
    row("BADTOKEN", "BADTOKEN", "NSE", "NSE_EQ", "abc"),
    row("SBIN", "SBIN", "BSE", "BSE_EQ", 3045),
]
SYMBOLS += [row(f"TKN{i}", f"TKN{i}", "NSE", "NSE_EQ", 900000 + i) for i in range(1002)]
T = {"SBIN": 3045, "INFY": 1594, "FUT": 35001, "GOLD": 80001, "NIFTY": 26000}


def ex(name):
    return name.encode().ljust(10, b"\x00")


def ltp(exchange, token, price):
    return struct.pack("<10sid", ex(exchange), token, price)


def ohlcv(exchange, token, price, ltt=1793699999, o=800.0, h=810.0, lo=795.5, c=799.95, volume=1234567):
    return struct.pack("<10sididdddi", ex(exchange), token, price, ltt, o, h, lo, c, volume)


def full(exchange, token, price, ltt=1793699999, lut=1793699998, ltq=5, avg=800.5, tbq=170, tsq=97, oi=12000, c=799.95):
    head = struct.pack("<10sididdddiiidqqi", ex(exchange), token, price, ltt, 800.0, 810.0, 795.5, c, 1234567, lut, ltq, avg, tbq, tsq, oi)
    buys = b"".join(struct.pack("<dii", round(price - 0.05 * (i + 1), 2), 100 * (i + 1), i + 1) for i in range(5))
    sells = b"".join(struct.pack("<dii", round(price + 0.05 * (i + 1), 2), 90 * (i + 1), i + 1) for i in range(5))
    return head + buys + sells + struct.pack("<ii", round(price * 1.1 * 100), round(price * 0.9 * 100))


def message(*packets):
    return struct.pack("<H", len(packets)) + b"".join(struct.pack("<H", len(p)) + p for p in packets)


class Driver:
    """Runs the real adapter and its real client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.rupeezy.streaming.rupeezy_adapter import RupeezyWebSocketAdapter
        from broker.rupeezy.streaming.rupeezy_websocket import RupeezyWebSocket

        self.adapter_cls, self.ws_cls = RupeezyWebSocketAdapter, RupeezyWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(AUTH_TOKEN, on_ticks=adapter._on_ticks, user_id="u")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        ws.ws, ws.connected, ws.running = FakeSocket(), True, True
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
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
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item.get("mode"))
        return self._take()

    def reconnect(self, _):
        self.ws._resubscribe_all()
        return self._take()

    def frame(self, hex_text):
        self.ws._on_message(self.ws.ws, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(*packets):
    return {"frame": message(*packets).hex()}


SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
    ]},
    {"name": "mcx_and_index_tickers", "steps": [{"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 2), sub("NIFTY", "NSE_INDEX", 1)]}]},
    {"name": "a_higher_mode_raises_the_stream", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [sub("SBIN", "NSE", 1)]},
    ]},
    {"name": "unsubscribe_ends_the_stream_only_when_no_mode_needs_it", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]},
        {"unsubscribe": sub("SBIN", "NSE", 3)},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
        {"unsubscribe": sub("SBIN", "NSE", 1)},
    ]},
    {"name": "unsubscribe_without_a_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]},
        {"unsubscribe": {"symbol": "SBIN", "exchange": "NSE"}},
    ]},
    {"name": "unsubscribe_of_a_mode_never_subscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]},
        {"unsubscribe": sub("SBIN", "NSE", 3)},
    ]},
    {"name": "reconnect_repeats_every_subscription", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]}, {"reconnect": None},
    ]},
    {"name": "a_symbol_without_a_numeric_token_is_refused", "steps": [{"subscribe": [sub("BADTOKEN", "NSE", 1)]}]},
    {"name": "the_thousand_and_first_instrument_is_refused", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1000)]},
        {"subscribe": [sub("TKN1000", "NSE", 1)]},
        {"subscribe": [sub("TKN5", "NSE", 3)]},
        frame(ltp("NSE_EQ", 900000 + 1000, 10.0)),
    ]},
    {"name": "ltp_frame", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltp("NSE_EQ", T["SBIN"], 805.45))]},
    {"name": "ohlcv_frame_for_a_quote_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(ohlcv("NSE_EQ", T["SBIN"], 805.45))]},
    {"name": "ohlcv_frame_also_feeds_the_ltp_subscription", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]}, frame(ohlcv("NSE_EQ", T["SBIN"], 805.45)),
    ]},
    {"name": "full_frame_fans_out_to_depth_quote_and_ltp", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]},
        frame(full("NSE_FO", T["FUT"], 23980.25)),
    ]},
    {"name": "full_frame_for_a_depth_only_subscription", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(full("NSE_FO", T["FUT"], 23980.25))]},
    {"name": "ltp_frame_for_a_depth_subscription_is_published_as_ltp", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(ltp("NSE_EQ", T["SBIN"], 805.45))]},
    {"name": "frame_without_a_close_has_no_change", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(ohlcv("NSE_EQ", T["SBIN"], 805.45, c=0.0))]},
    {"name": "frame_without_a_time_has_no_timestamp", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(ohlcv("NSE_EQ", T["SBIN"], 805.45, ltt=0))]},
    {"name": "full_frame_without_an_update_time_uses_the_trade_time", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(full("NSE_EQ", T["SBIN"], 805.45, lut=0))]},
    {"name": "mcx_ohlcv_frame", "steps": [{"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 2)]}, frame(ohlcv("MCX_FO", T["GOLD"], 75000.0))]},
    {"name": "index_ltp_frame", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]}, frame(ltp("NSE_EQ", T["NIFTY"], 24050.5))]},
    {"name": "one_frame_with_several_packets", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)]}, frame(ltp("NSE_EQ", T["SBIN"], 805.45), ltp("NSE_EQ", T["INFY"], 1500.1)),
    ]},
    {"name": "frame_for_a_token_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltp("NSE_EQ", T["INFY"], 1500.1))]},
    {"name": "frame_for_the_right_token_on_another_exchange", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(ltp("BSE_EQ", T["SBIN"], 805.45))]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, frame(ltp("NSE_EQ", T["SBIN"], 805.45)),
    ]},
    {"name": "heartbeat_and_short_frames_carry_nothing", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": "00"}, {"frame": "0000"}, {"frame": "0100"},
    ]},
    {"name": "frame_cut_off_in_the_middle_of_a_packet", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": message(ltp("NSE_EQ", T["SBIN"], 805.45), ltp("NSE_EQ", T["SBIN"], 805.5)).hex()[:-6]},
    ]},
    {"name": "packet_of_an_unknown_size_is_skipped", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": message(ltp("NSE_EQ", T["SBIN"], 805.45)[:20], ltp("NSE_EQ", T["SBIN"], 805.5)).hex()},
    ]},
    {"name": "a_text_frame_carries_no_ticks", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": b'{"order":"postback"}'.hex()}]},
    {"name": "the_same_token_on_two_exchanges_is_two_instruments", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 1)]},
        {"unsubscribe": sub("SBIN", "BSE", 1)},
        frame(ltp("NSE_EQ", T["SBIN"], 805.45)),
        frame(ltp("BSE_EQ", T["SBIN"], 805.5)),
    ]},
    {"name": "an_ohlcv_frame_for_a_depth_subscription_is_published_as_a_quote", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(ohlcv("NSE_EQ", T["SBIN"], 805.45))]},
    {"name": "change_and_percent_are_rounded_like_python", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 2)]},
        frame(ohlcv("NSE_EQ", T["SBIN"], 103.235, c=100.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 1000.25, c=1000.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 52.945, c=50.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 3.675, c=1.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 800.125, c=800.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 800.375, c=800.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 799.875, c=800.0)),
        frame(ohlcv("NSE_EQ", T["SBIN"], 801.234567, c=800.0)),
    ]},
]
