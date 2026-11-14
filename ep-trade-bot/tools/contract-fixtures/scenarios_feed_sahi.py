"""Sahi feed: the protobuf frames the stream is sent to subscribe (batched, one mode per instrument) and what the adapter publishes for the frames it gets back."""

NOW_MS = 1_793_700_000_123
AUTH_TOKEN = "sahi-access-token"
BROKER = "sahi"
ENV = {"BROKER_API_KEY": "sahi-api-key", "BROKER_API_SECRET": "sahi-api-secret"}


def row(symbol, exchange, token, instrument="EQ", brexchange=None):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token),
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 1001), row("INFY", "NSE", 1002), row("SBIN", "BSE", 2001),
    row("NIFTY25NOV26FUT", "NFO", 50001, "FUT"), row("SENSEX26NOVFUT", "BFO", 60001, "FUT"),
    row("NIFTY", "NSE_INDEX", 26000, "INDEX", "NSE"), row("SENSEX", "BSE_INDEX", 26001, "INDEX", "BSE"),
    row("GOLDM25NOV26FUT", "MCX", 80001, "FUT"), row("BADTOKEN", "NSE", "abc"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i) for i in range(1002)]
ID = {"SBIN": 1001, "INFY": 1002, "FUT": 50001, "NIFTY": 26000, "BSEFUT": 60001}


def varint(v):
    if v < 0:
        v += 1 << 64
    out = bytearray()
    while v > 0x7F:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    out.append(v)
    return bytes(out)


def vfield(n, v):
    return varint(n << 3) + varint(v)


def lfield(n, payload):
    return varint((n << 3) | 2) + varint(len(payload)) + payload


def level(price, qty, orders):
    return vfield(1, price) + vfield(2, qty) + vfield(3, orders)


def tick(iid, price, ltt=1793699999, volume=1234567, upper=88600, lower=72500, oi=None, buys=(), sells=(), segment=1, kind=1):
    b = vfield(1, iid) + vfield(2, segment) + vfield(3, kind) + vfield(4, price)
    if ltt is not None:
        b += vfield(5, ltt)
    b += vfield(6, volume) + vfield(7, upper) + vfield(8, lower)
    if oi is not None:
        b += vfield(9, oi - 100) + vfield(10, oi)
    for p, q, o in buys:
        b += lfield(11, level(p, q, o))
    for p, q, o in sells:
        b += lfield(12, level(p, q, o))
    return b


def msg(field, body):
    return lfield(field, body).hex()


BUYS = [(80540 - 5 * i, 100 * (i + 1), i + 1) for i in range(5)]
SELLS = [(80550 + 5 * i, 110 * (i + 1), 2 * (i + 1)) for i in range(5)]


class Driver:
    """Runs the real adapter with a fake socket; the 0.5 s batch timer is replaced by an explicit flush after each step."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.sahi.streaming.sahi_adapter import SahiWebSocketAdapter

        self.adapter_cls = SahiWebSocketAdapter

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        adapter.initialize("sahi", "u", {"auth_token": AUTH_TOKEN})
        sent = self.sent

        class FakeSocket:
            def send_binary(self, data):
                sent.append(bytes(data).hex())

        client = adapter.ws_client
        client._ws, client.connected, client.running = FakeSocket(), True, True
        adapter.running = adapter.connected = True
        adapter._mark_dirty = lambda iid: adapter._dirty.add(iid)
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.client = adapter, client

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.adapter._flush_pending()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item.get("mode"))
        self.adapter._flush_pending()
        return self._take()

    def reconnect(self, _):
        self.adapter._on_close(1006, "lost")
        self.adapter._on_open(self.client)
        return self._take()

    def frame(self, hex_text):
        self.adapter._on_message(bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(field, body):
    return {"frame": msg(field, body)}


LTP, QUOTE, DEPTH = 2, 3, 4

SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]},
    ]},
    {"name": "several_symbols_are_grouped_by_mode_in_one_flush", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX26NOVFUT", "BFO", 3)]},
    ]},
    {"name": "every_streamed_exchange", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1)]}]},
    {"name": "a_higher_mode_changes_the_stream_and_a_lower_one_does_not_change_it_back", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [sub("SBIN", "NSE", 1)]},
    ]},
    {"name": "a_subscribe_and_an_unsubscribe_in_one_window_send_nothing", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 1)]}, {"unsubscribe": sub("INFY", "NSE", 1)},
    ]},
    {"name": "dropping_the_highest_mode_downgrades_the_stream", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"unsubscribe": sub("SBIN", "NSE", 1)},
    ]},
    {"name": "dropping_a_lower_mode_changes_nothing", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 1)},
    ]},
    {"name": "unsubscribe_without_a_mode_drops_every_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]}, {"unsubscribe": {"symbol": "SBIN", "exchange": "NSE"}},
    ]},
    {"name": "unsubscribe_of_a_mode_never_subscribed_sends_nothing", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"unsubscribe": sub("INFY", "NSE", 1)}]},
    {"name": "reconnect_repeats_every_subscription_grouped_by_mode", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": None},
    ]},
    {"name": "six_hundred_ids_go_in_frames_of_five_hundred", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(600)]}, {"reconnect": None}]},
    {"name": "the_thousand_and_first_instrument_is_refused", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1000)]}, {"subscribe": [sub("TKN1000", "NSE", 1)]}, {"subscribe": [sub("TKN5", "NSE", 3)]}, frame(LTP, tick(900000 + 1000, 1000)),
    ]},
    {"name": "an_exchange_sahi_does_not_stream_is_refused", "steps": [{"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 1)]}]},
    {"name": "a_symbol_without_a_numeric_token_is_refused", "steps": [{"subscribe": [sub("BADTOKEN", "NSE", 1)]}]},
    {"name": "an_invalid_mode_is_refused", "steps": [{"subscribe": [sub("SBIN", "NSE", 9)]}]},
    {"name": "ltp_frame", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(LTP, tick(ID["SBIN"], 80545))]},
    {"name": "ltp_frame_without_a_trade_time", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(LTP, tick(ID["SBIN"], 80545, ltt=None))]},
    {"name": "quote_frame_for_a_quote_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(QUOTE, tick(ID["SBIN"], 80545, oi=1500))]},
    {"name": "quote_frame_without_open_interest", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(QUOTE, tick(ID["SBIN"], 80545))]},
    {"name": "quote_frame_also_feeds_the_ltp_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]}, frame(QUOTE, tick(ID["SBIN"], 80545, oi=1500))]},
    {"name": "depth_frame_fans_out_to_depth_quote_and_ltp", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(DEPTH, tick(ID["FUT"], 2400550, oi=12000, buys=BUYS, sells=SELLS, segment=3)),
    ]},
    {"name": "depth_frame_for_a_depth_only_subscription", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(DEPTH, tick(ID["FUT"], 2400550, buys=BUYS, sells=SELLS, segment=3))]},
    {"name": "depth_frame_with_fewer_levels_is_padded", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(DEPTH, tick(ID["SBIN"], 80545, buys=BUYS[:2], sells=[]))]},
    {"name": "depth_frame_with_twenty_levels_is_cut_to_five", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(DEPTH, tick(ID["SBIN"], 80545, buys=[(80540 - i, 10, 1) for i in range(20)], sells=[(80550 + i, 10, 1) for i in range(20)])),
    ]},
    {"name": "ltp_frame_for_a_depth_subscription_publishes_nothing", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(LTP, tick(ID["SBIN"], 80545))]},
    {"name": "quote_frame_for_a_depth_subscription_publishes_nothing", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(QUOTE, tick(ID["SBIN"], 80545))]},
    {"name": "index_ltp_frame", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]}, frame(LTP, tick(ID["NIFTY"], 2405050))]},
    {"name": "a_negative_price_is_read_as_signed", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(LTP, tick(ID["SBIN"], -5))]},
    {"name": "frame_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(LTP, tick(ID["INFY"], 150000))]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, frame(LTP, tick(ID["SBIN"], 80545))]},
    {"name": "an_ack_carries_no_ticks", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(1, vfield(1, 1) + lfield(2, varint(1001)))]},
    {"name": "an_error_message_carries_no_ticks", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(5, lfield(1, b"RATE") + lfield(2, b"slow down"))]},
    {"name": "an_empty_message_carries_nothing", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": ""}]},
    {"name": "an_unknown_field_is_skipped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": (lfield(9, b"zz") + lfield(LTP, tick(ID["SBIN"], 80545))).hex()}]},
    {"name": "a_tick_with_an_unknown_field_inside_is_still_read", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(LTP, tick(ID["SBIN"], 80545) + vfield(20, 5) + lfield(21, b"zz"))]},
    {"name": "two_ticks_in_one_frame_the_last_one_wins", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": (lfield(LTP, tick(ID["SBIN"], 80545)) + lfield(LTP, tick(ID["SBIN"], 80600))).hex()}]},
    {"name": "a_frame_cut_off_in_the_middle_is_dropped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": msg(LTP, tick(ID["SBIN"], 80545))[:-6]}]},
    {"name": "garbage_is_dropped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": "ffffffffffffffffffffffff"}]},
    {"name": "an_unsupported_wire_type_is_dropped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": "0b"}]},
    {"name": "subscribe_again_after_unsubscribing_sends_the_subscribe_again", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"subscribe": [sub("SBIN", "NSE", 1)]},
    ]},
    {"name": "a_mode_change_and_a_new_subscribe_in_one_flush", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3), sub("INFY", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 2)]},
    ]},
    {"name": "a_freed_place_under_the_thousand_limit_can_be_taken", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1000)]}, {"unsubscribe": sub("TKN7", "NSE", 1)}, {"subscribe": [sub("TKN1000", "NSE", 1)]}, frame(LTP, tick(900000 + 1000, 1000)),
    ]},
    {"name": "unsubscribe_after_a_reconnect_reaches_the_server", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"reconnect": None}, {"unsubscribe": sub("SBIN", "NSE", 1)},
    ]},
    {"name": "a_varint_of_eleven_bytes_drops_the_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": ("80" * 10 + "0100" + msg(LTP, tick(ID["SBIN"], 80545)))},
    ]},
    {"name": "a_fixed64_field_is_skipped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": ("49" + "01" * 8 + msg(LTP, tick(ID["SBIN"], 80545)))}]},
    {"name": "a_fixed32_field_is_skipped", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": ("4d" + "01" * 4 + msg(LTP, tick(ID["SBIN"], 80545)))}]},
    {"name": "a_unknown_field_running_past_the_end_drops_the_frame", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": (msg(LTP, tick(ID["SBIN"], 80545)) + "4a32aabb")}]},
    {"name": "a_malformed_ack_before_a_tick_drops_the_frame", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": (msg(1, b"\x12\x05\x01") + msg(LTP, tick(ID["SBIN"], 80545)))}]},
    {"name": "a_malformed_error_before_a_tick_drops_the_frame", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"frame": (msg(5, b"\x0a\x09ab") + msg(LTP, tick(ID["SBIN"], 80545)))}]},
]
