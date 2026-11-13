"""Dhan feed (the 5-level connection): what the socket is sent to subscribe (JSON requests) and what the adapter publishes for the binary packets it gets back."""

import json
import struct

BROKER = "dhan"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "1100000001:::dhan-app-key", "BROKER_API_SECRET": "dhan-app-secret"}


def row(symbol, brexchange, token, exchange="NSE", instrument="EQUITY"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", "3045"),
    row("INFY", "NSE", "1594"),
    row("INFY", "BSE", "500209", "BSE"),
    row("NIFTY25NOV26FUT", "NSE_FNO", "35001", "NFO", "FUTIDX"),
    row("NIFTY", "NSE", "13", "NSE_INDEX", "INDEX"),
    row("SENSEX", "BSE", "51", "BSE_INDEX", "INDEX"),
    row("CRUDEOILM", "MCX", "487465", "MCX", "FUTCOM"),
    row("USDINR", "NSE_CURRENCY", "1002", "CDS", "FUTCUR"),
    row("SAMEID", "NSE", "7777"),
    row("SAMEID", "BSE", "7777", "BSE"),
    row("BSEIDX", "BSE", "88", "BSE_INDEX", "INDEX"),
    row("NSEIDX", "NSE", "88", "NSE_INDEX", "INDEX"),
    row("COIN", "CRYPTO", "27", "CRYPTO", "FUTURES"),
]
MANY = [row(f"SYM{i:04d}", "NSE", str(10000 + i)) for i in range(130)]
SYMBOLS += MANY

SEGMENTS = {"IDX": 0, "NSE": 1, "NFO": 2, "CDS": 3, "BSE": 4, "MCX": 5, "NCO": 6, "BCD": 7, "BFO": 8}


def packet(code, segment, security_id, payload):
    return struct.pack("<BHBI", code, 8 + len(payload), segment, security_id) + payload


def ticker(segment, security_id, ltp=805.45, ltt=1793699999):
    return packet(2, segment, security_id, struct.pack("<fI", ltp, ltt))


def quote(segment, security_id, ltp=805.45):
    return packet(4, segment, security_id, struct.pack("<fHIfIIIffff", ltp, 5, 1793699999, 803.1, 1234567, 97, 170, 800.0, 799.95, 810.0, 795.5))


def oi(segment, security_id, value=12000):
    return packet(5, segment, security_id, struct.pack("<I", value))


def prev_close(segment, security_id):
    return packet(6, segment, security_id, struct.pack("<fI", 799.95, 11000))


def full(segment, security_id, ltp=805.45, levels=5):
    body = struct.pack("<fHIfIIIIIIffff", ltp, 5, 1793699999, 803.1, 1234567, 97, 170, 12000, 12500, 11500, 800.0, 799.95, 810.0, 795.5)
    depth = b"".join(struct.pack("<IIHHff", 100 * (i + 1), 90 * (i + 1), i + 1, i + 2, ltp - 0.05 * (i + 1), ltp + 0.05 * (i + 1)) for i in range(levels))
    return packet(8, segment, security_id, body + depth)


def disconnect(code=805):
    return packet(50, 0, 0, struct.pack("<H", code))


class Driver:
    """Runs the real adapter and its real 5-level client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.dhan.streaming.dhan_adapter import DhanWebSocketAdapter
        from broker.dhan.streaming.dhan_websocket import DhanWebSocket

        self.adapter_cls, self.ws_cls = DhanWebSocketAdapter, DhanWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls("1100000001", "dhan-token", False)
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        ws.ws = FakeSocket()
        ws.connected = True
        ws._start_health_check = lambda: None
        ws.on_open = adapter._on_open_5depth
        ws.on_data = adapter._on_data_5depth
        ws.on_error = adapter._on_error_5depth
        ws.on_close = adapter._on_close_5depth
        adapter.ws_client_5depth = ws
        adapter.running, adapter.connected = True, True
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
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"], 5)
        self.adapter._process_batch_subscriptions()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def reconnect(self, _):
        self.ws._on_open(None)
        return self._take()

    def frame(self, hex_text):
        self.ws._on_message(None, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(data):
    return {"frame": data.hex()}


SCENARIOS = [
    {"name": "subscribe_in_each_mode", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("NIFTY25NOV26FUT", 3, "NFO")]}]},
    {"name": "one_request_per_mode", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 1), sub("NIFTY25NOV26FUT", 1, "NFO"), sub("SBIN", 2)]}]},
    {"name": "every_exchange", "steps": [{"subscribe": [
        sub("SBIN", 1), sub("INFY", 1, "BSE"), sub("NIFTY25NOV26FUT", 1, "NFO"), sub("NIFTY", 1, "NSE_INDEX"), sub("SENSEX", 1, "BSE_INDEX"), sub("CRUDEOILM", 1, "MCX"), sub("USDINR", 1, "CDS")]}]},
    {"name": "a_hundred_and_thirty_instruments_go_in_batches_of_a_hundred", "steps": [{"subscribe": [sub(f"SYM{i:04d}", 2) for i in range(130)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("SBIN", 3)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 1)]}]},
    {"name": "a_mode_the_exchange_does_not_have", "steps": [{"subscribe": [sub("SBIN", 4), sub("SBIN", 0)]}]},
    {"name": "unsubscribe_stops_the_mode_it_was_subscribed_in", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("NIFTY25NOV26FUT", 3, "NFO")]}, {"unsubscribe": sub("SBIN", 1)}, {"unsubscribe": sub("INFY", 2)}, {"unsubscribe": sub("NIFTY25NOV26FUT", 3, "NFO")}]},
    {"name": "unsubscribe_twice", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"unsubscribe": sub("SBIN", 1)}, {"unsubscribe": sub("SBIN", 1)}]},
    {"name": "unsubscribe_something_never_subscribed", "steps": [{"unsubscribe": sub("SBIN", 1)}]},
    {"name": "unsubscribing_one_mode_stops_the_instrument_in_the_mode_it_was_last_subscribed", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 3)]}, {"unsubscribe": sub("SBIN", 1)}]},
    {"name": "reconnect_repeats_every_subscription_by_mode", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("NIFTY25NOV26FUT", 3, "NFO"), sub("SENSEX", 1, "BSE_INDEX")]}, {"unsubscribe": sub("INFY", 2)}, {"reconnect": None}]},
    {"name": "reconnect_with_a_hundred_and_thirty_in_one_mode", "steps": [{"subscribe": [sub(f"SYM{i:04d}", 1) for i in range(130)]}, {"reconnect": None}]},
    {"name": "ticker_frame", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(ticker(1, 3045))]},
    {"name": "quote_frame", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(quote(1, 3045))]},
    {"name": "full_frame_with_five_levels", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", 3, "NFO")]}, frame(full(2, 35001, 23980.25))]},
    {"name": "full_frame_too_short_for_depth", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(full(1, 3045, levels=3))]},
    {"name": "index_ticker_frame", "steps": [{"subscribe": [sub("NIFTY", 1, "NSE_INDEX")]}, frame(ticker(0, 13, 24050.5))]},
    {"name": "bse_index_ticker_frame", "steps": [{"subscribe": [sub("SENSEX", 1, "BSE_INDEX")]}, frame(ticker(0, 51, 79000.25))]},
    {"name": "a_ticker_frame_reaches_a_quote_subscriber_as_a_ticker_tick", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(ticker(1, 3045))]},
    {"name": "a_full_frame_reaches_a_ticker_subscriber_as_a_depth_tick", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(full(1, 3045))]},
    {"name": "oi_and_previous_close_frames_publish_nothing", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", 2, "NFO")]}, frame(oi(2, 35001)), frame(prev_close(2, 35001))]},
    {"name": "frame_with_several_packets", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 2)]}, frame(ticker(1, 3045) + quote(1, 1594) + oi(1, 3045) + ticker(1, 3045, 806.0))]},
    {"name": "two_exchanges_share_a_security_id_and_the_segment_decides", "steps": [
        {"subscribe": [sub("SAMEID", 1), sub("SAMEID", 1, "BSE")]}, frame(ticker(4, 7777)), frame(ticker(1, 7777))]},
    {"name": "a_security_id_alone_matches_when_the_segment_does_not", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(ticker(2, 3045))]},
    {"name": "frame_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(ticker(1, 1594))]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"unsubscribe": sub("SBIN", 1)}, frame(ticker(1, 3045))]},
    {"name": "a_packet_cut_short_ends_the_frame", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 1)]}, frame(ticker(1, 3045) + ticker(1, 1594)[:10])]},
    {"name": "payloads_too_short_for_their_kind", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(packet(4, 1, 3045, b"\x00" * 20)), frame(packet(2, 1, 3045, b"\x00" * 4))]},
    {"name": "disconnect_and_unknown_packets_publish_nothing", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(disconnect()), frame(packet(99, 1, 3045, b"\x00" * 8)), frame(packet(0, 1, 3045, b"\x00" * 8))]},
    {"name": "an_exchange_dhan_does_not_have", "steps": [{"subscribe": [sub("COIN", 1, "CRYPTO")]}]},
    {"name": "unsubscribing_a_mode_never_subscribed_stops_nothing", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"unsubscribe": sub("SBIN", 2)}, frame(ticker(1, 3045))]},
    {"name": "unsubscribing_what_the_socket_no_longer_tracks_sends_nothing", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 3)]}, {"unsubscribe": sub("SBIN", 3)}, {"unsubscribe": sub("SBIN", 1)}]},
    {"name": "two_indices_share_an_id_and_the_first_subscribed_wins_the_segment", "steps": [
        {"subscribe": [sub("BSEIDX", 1, "BSE_INDEX"), sub("NSEIDX", 1, "NSE_INDEX")]}, frame(ticker(0, 88))]},
    {"name": "a_quote_payload_between_the_two_sizes", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(packet(4, 1, 3045, b"\x00" * 35))]},
    {"name": "frames_that_carry_no_market_data", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"frame": ""}, {"frame": "02"}, {"frame": "0200000000"}]},
]
