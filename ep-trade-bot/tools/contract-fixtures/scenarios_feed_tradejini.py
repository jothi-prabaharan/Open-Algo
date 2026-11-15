"""Tradejini feed: one address that carries the token (<api key>:<access token>), no sign-in frame. A subscribe request names a whole feed (L1 for last price and quote, L5 for depth) and REPLACES the list the server holds for it, so every change sends the complete list of that feed,
a feed with nothing left is cancelled by an unsubscribe of the whole feed. Frames are binary: a header (length, version 1, compression 100 for zlib) and packets, each a length, a type and key/value fields in the broker's own units (prices as integers a segment divisor apart)."""

import json
import struct
import threading
import zlib

NOW_MS = 1_793_700_000_123
BROKER = "tradejini"
AUTH_TOKEN = "tj-access-token"
ENV = {"BROKER_API_KEY": "tj-app-api-key"}


def row(symbol, brsymbol, exchange, token, instrument="EQ", brexchange=None):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "EQT_SBIN_EQ_NSE", "NSE", 3045), row("INFY", "EQT_INFY_EQ_NSE", "NSE", 1594), row("RELIANCE", "EQT_RELIANCE_EQ_NSE", "NSE", 2885), row("TATAMOTORS", "EQT_TATAMOTORS_EQ_BSE", "BSE", 500570),
    row("SBINBSE", "EQT_SBIN_EQ_BSE", "BSE", 3045), row("NIFTY25NOV26FUT", "FUT_NIFTY_25NOV26_NFO", "NFO", 49500, "FUT"), row("SENSEX26NOVFUT", "FUT_SENSEX_26NOV26_BFO", "BFO", 60001, "FUT"),
    row("USDINR25NOV26FUT", "FUT_USDINR_25NOV26_CDS", "CDS", 90001, "FUT"), row("GOLDM25NOV26FUT", "FUT_GOLDM_25NOV26_MCX", "MCX", 80001, "FUT"), row("NIFTY", "IDX_NIFTY_NSE", "NSE_INDEX", 26000, "INDEX", "NSE"),
    row("SENSEX", "IDX_SENSEX_BSE", "BSE_INDEX", 19000, "INDEX", "BSE"),
]

# ---- the broker's packets -----------------------------------------------------------------------------------------------
FORMATS = {26: "B", 27: "i", 28: "B", 29: "i", 30: "i", 31: "i", 32: "i", 33: "i", 34: "i", 35: "i", 36: "i", 37: "i", 38: "i", 39: "<I", 40: "<I", 41: "d", 42: "i", 43: "i", 44: "<I", 45: "i", 46: "i", 47: "<I", 48: "<I", 49: "i", 50: "<I",
           51: "<I", 52: "i", 53: "<I", 54: "<I", 55: "B", 56: "H", 57: "B", 58: "<I", 59: "<I", 60: "<I", 62: "B", 25: "B", 63: "d", 64: "d", 65: "d", 66: "d", 67: "d", 68: "d", 69: "d", 70: "i", 71: "i", 72: "d", 73: "d", 74: "i", 76: "<I"}


def field(key, value):
    if key in (61, 75):
        return bytes([key]) + (value if isinstance(value, bytes) else value.encode())
    return bytes([key]) + struct.pack(FORMATS[key], value)


def packet(kind, *pairs, raw=b""):
    body = bytes([kind]) + b"".join(field(k, v) for k, v in pairs) + raw
    return struct.pack("<h", len(body) + 2) + body


def frame(*packets, version=1, compress=False, total=None):
    payload = b"".join(packets)
    if compress:
        payload = zlib.compress(payload)
    return struct.pack("<i", len(payload) if total is None else total) + bytes([version, 100 if compress else 0]) + payload


def l1(token=3045, segment=1, extra=None):
    values = {29: 80545, 30: 80100, 31: 80950, 32: 79900, 33: 80000, 34: 545, 35: 68, 36: 80410, 39: 25, 40: 1234567, 44: 0, 46: 1793699999, 49: 80540, 50: 100, 51: 3, 52: 80550, 53: 110, 54: 4}
    values.update(extra or {})
    return packet(10, (26, segment), (27, token), *sorted(values.items()))


def level(price, qty, orders):
    return [(49, price), (50, qty), (51, orders)]


def l5(token=3045, segment=1, bids=None, asks=None, count=5, total_buy=5000, total_sell=6000):
    bids = bids if bids is not None else [(80540 - 5 * i, 100 * (i + 1), i + 1) for i in range(5)]
    asks = asks if asks is not None else [(80550 + 5 * i, 110 * (i + 1), 2 * (i + 1)) for i in range(5)]
    pairs = [(26, segment), (27, token), (47, total_buy), (48, total_sell), (55, count)]
    for p, q, n in bids:
        pairs += [(49, p), (50, q), (51, n)]
    for p, q, n in asks:
        pairs += [(52, p), (53, q), (54, n)]
    return packet(11, *pairs)


class Driver:
    """Runs the real adapter and the real stream client with a fake socket; the adapter's flush thread is replaced by a hand-run flush."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.tradejini.streaming.nxtradstream import NxtradStream
        from broker.tradejini.streaming.tradejini_adapter import TradejiniWebSocketAdapter

        self.adapter_cls, self.stream_cls = TradejiniWebSocketAdapter, NxtradStream

    def reset(self):
        self.sent, self.published, self.dirty = [], [], {}
        sent = self.sent

        class Fake:
            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        adapter = self.adapter_cls()
        stream = self.stream_cls("api.tradejini.com", version="3.1", stream_cb=adapter._on_data, connect_cb=adapter._on_connection_event)
        stream.ws = Fake()
        stream.isConnected = True
        adapter.ws_client = stream
        adapter.running = True
        adapter.connected = True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        dirty = self.dirty

        def request(feed, cancel_when_empty=True):
            dirty[feed] = dirty.get(feed, False) or cancel_when_empty

        adapter._request_feed_sync = request
        adapter._schedule_reconnect = lambda: None
        self.adapter, self.stream = adapter, stream

    def _flush(self):
        pending, self.dirty.clear = dict(self.dirty), None
        self.dirty.clear = {}.clear  # replaced below
        return pending

    def _take(self, extra=None):
        out = {"sent": [json.loads(x) for x in self.sent], "published": list(self.published)}
        if extra:
            out.update(extra)
        self.sent.clear()
        self.published.clear()
        return out

    def _run_flush(self):
        pending = dict(self.dirty)
        self.dirty.clear()
        for feed, cancel in pending.items():
            self.adapter._sync_feed(feed, cancel_when_empty=cancel)

    def subscribe(self, items):
        errors = []
        for item in items:
            result = self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
            if result.get("status") != "success":
                errors.append(result.get("error_code"))
        self._run_flush()
        return self._take({"errors": errors} if errors else None)

    def unsubscribe(self, item):
        result = self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        self._run_flush()
        return self._take({"error": result.get("error_code")} if result.get("status") != "success" else None)

    def reconnect(self, _):
        self.adapter._on_connection_event(self.stream, {"s": "connected"})
        self._run_flush()
        return self._take()

    def frame(self, hex_text):
        error = None
        try:
            self.stream._NxtradStream__on_message(None, bytes.fromhex(hex_text))
        except Exception as exc:  # noqa: BLE001
            error = type(exc).__name__
        return self._take({"error": error} if error else None)


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def fr(data):
    return {"frame": data.hex()}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


L = sub("SBIN", "NSE", 1)
Q = sub("SBIN", "NSE", 2)
D = sub("SBIN", "NSE", 3)
IQ = sub("INFY", "NSE", 2)
ID = sub("INFY", "NSE", 3)
RQ = sub("RELIANCE", "NSE", 2)


def un(item):
    return {"unsubscribe": item}


SCENARIOS = [
    # ---- connecting ---------------------------------------------------------------------------------------------
    scenario("a_connection_with_nothing_subscribed_sends_nothing", {"reconnect": {}}),
    scenario("a_connection_repeats_the_quote_feed", {"subscribe": [Q, IQ]}, {"reconnect": {}}),
    scenario("a_connection_repeats_the_depth_feed", {"subscribe": [D]}, {"reconnect": {}}),
    scenario("a_connection_repeats_both_feeds", {"subscribe": [Q, ID]}, {"reconnect": {}}),
    scenario("a_connection_after_everything_was_dropped_sends_nothing", {"subscribe": [Q]}, un(Q), {"reconnect": {}}),
    scenario("a_connection_repeats_a_token_once", {"subscribe": [L, Q]}, {"reconnect": {}}),
    scenario("a_second_connection_repeats_again", {"subscribe": [Q]}, {"reconnect": {}}, {"reconnect": {}}),
    # ---- subscribing --------------------------------------------------------------------------------------------
    scenario("subscribe_ltp", {"subscribe": [L]}),
    scenario("subscribe_quote", {"subscribe": [Q]}),
    scenario("subscribe_depth", {"subscribe": [D]}),
    scenario("subscribe_several_quotes_is_one_request", {"subscribe": [Q, IQ, RQ]}),
    scenario("subscribe_a_quote_and_a_depth_is_a_request_each_feed", {"subscribe": [Q, ID]}),
    scenario("subscribe_one_at_a_time_sends_the_whole_list_each_time", {"subscribe": [Q]}, {"subscribe": [IQ]}, {"subscribe": [RQ]}),
    scenario("subscribe_depth_one_at_a_time", {"subscribe": [D]}, {"subscribe": [ID]}),
    scenario("subscribe_ltp_and_quote_on_one_scrip_names_it_once", {"subscribe": [L, Q]}),
    scenario("subscribe_quote_and_depth_of_one_scrip", {"subscribe": [Q, D]}),
    scenario("subscribe_the_same_scrip_again_sends_the_list_again", {"subscribe": [Q]}, {"subscribe": [Q]}),
    scenario("subscribe_every_exchange", {"subscribe": [Q, sub("TATAMOTORS", "BSE", 2), sub("NIFTY25NOV26FUT", "NFO", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2)]}),
    scenario("subscribe_an_index_uses_its_cash_exchange", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 1)]}),
    scenario("subscribe_one_token_on_two_exchanges", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}),
    scenario("subscribe_depth_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}),
    scenario("subscribe_a_scrip_between_others_keeps_the_order", {"subscribe": [Q, IQ]}, {"subscribe": [sub("SBINBSE", "BSE", 2)]}),
    # ---- unsubscribing ------------------------------------------------------------------------------------------
    scenario("unsubscribe_one_of_two_sends_the_rest", {"subscribe": [Q, IQ]}, un(Q)),
    scenario("unsubscribe_the_last_quote_cancels_the_feed", {"subscribe": [Q]}, un(Q)),
    scenario("unsubscribe_the_last_depth_cancels_the_feed", {"subscribe": [D]}, un(D)),
    scenario("unsubscribe_depth_keeps_the_quote_feed", {"subscribe": [Q, D]}, un(D)),
    scenario("unsubscribe_quote_keeps_the_depth_feed", {"subscribe": [Q, ID]}, un(Q)),
    scenario("unsubscribe_ltp_while_the_quote_stays_resends_the_list", {"subscribe": [L, Q]}, un(L)),
    scenario("unsubscribe_a_scrip_that_was_never_subscribed_resends_the_list", {"subscribe": [Q]}, un(IQ)),
    scenario("unsubscribe_with_nothing_subscribed_cancels_the_feed", un(Q)),
    scenario("unsubscribe_twice", {"subscribe": [Q, IQ]}, un(Q), un(Q)),
    scenario("unsubscribe_both_of_two_quotes", {"subscribe": [Q, IQ]}, un(Q), un(IQ)),
    scenario("unsubscribe_then_subscribe_again", {"subscribe": [Q]}, un(Q), {"subscribe": [Q]}),
    scenario("unsubscribe_a_quote_on_one_exchange_of_two", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}, un(sub("SBINBSE", "BSE", 2))),
    # ---- last price and quote data ------------------------------------------------------------------------------
    scenario("a_quote_packet_for_a_quote_subscription", {"subscribe": [Q]}, fr(frame(l1()))),
    scenario("a_quote_packet_for_an_ltp_subscription", {"subscribe": [L]}, fr(frame(l1()))),
    scenario("a_quote_packet_for_a_depth_subscription_publishes_nothing", {"subscribe": [D]}, fr(frame(l1()))),
    scenario("a_quote_packet_for_ltp_and_quote_publishes_to_both", {"subscribe": [L, Q]}, fr(frame(l1()))),
    scenario("a_second_packet_keeps_what_the_first_carried", {"subscribe": [Q]}, fr(frame(l1())), fr(frame(packet(10, (26, 1), (27, 3045), (29, 80600), (40, 1234999))))),
    scenario("a_packet_with_only_the_last_price", {"subscribe": [Q]}, fr(frame(packet(10, (26, 1), (27, 3045), (29, 80600))))),
    scenario("a_packet_with_no_fields_beyond_the_scrip", {"subscribe": [Q]}, fr(frame(packet(10, (26, 1), (27, 3045))))),
    scenario("a_packet_with_every_field", {"subscribe": [Q]}, fr(frame(l1(extra={37: 90000, 38: 60000, 41: 99999999.5, 42: 88000, 43: 72000, 44: 7000, 45: 123, 58: 6500, 59: 7500, 60: 6000, 70: 80500, 71: 79999, 74: 80420})))),
    scenario("a_currency_packet_uses_its_divisor", {"subscribe": [sub("USDINR25NOV26FUT", "CDS", 2)]}, fr(frame(l1(90001, 5, extra={29: 835000000, 30: 834000000, 31: 836000000, 32: 833000000, 33: 834500000, 49: 834900000, 52: 835100000})))),
    scenario("a_commodity_packet", {"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 2)]}, fr(frame(l1(80001, 8, extra={29: 7500000, 30: 7490000})))),
    scenario("a_derivative_packet", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, fr(frame(l1(49500, 3, extra={29: 2410025, 44: 7000000})))),
    scenario("a_bse_derivative_packet", {"subscribe": [sub("SENSEX26NOVFUT", "BFO", 2)]}, fr(frame(l1(60001, 4)))),
    scenario("an_index_packet", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, fr(frame(l1(26000, 1, extra={29: 2410025})))),
    scenario("a_bse_index_packet", {"subscribe": [sub("SENSEX", "BSE_INDEX", 2)]}, fr(frame(l1(19000, 2)))),
    scenario("a_bse_equity_packet", {"subscribe": [sub("TATAMOTORS", "BSE", 2)]}, fr(frame(l1(500570, 2)))),
    scenario("a_packet_for_a_token_on_another_exchange_is_not_the_scrip", {"subscribe": [Q]}, fr(frame(l1(3045, 2)))),
    scenario("a_packet_for_a_token_nobody_asked_for", {"subscribe": [Q]}, fr(frame(l1(9999)))),
    scenario("a_packet_for_a_token_on_two_exchanges_goes_to_the_right_one", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}, fr(frame(l1(3045, 2)))),
    scenario("negative_values", {"subscribe": [Q]}, fr(frame(l1(extra={29: -80545, 34: -545, 35: -68})))),
    scenario("a_zero_time", {"subscribe": [Q]}, fr(frame(l1(extra={46: 0})))),
    scenario("a_packet_before_anything_is_subscribed", fr(frame(l1()))),
    scenario("two_packets_in_one_frame", {"subscribe": [Q, IQ]}, fr(frame(l1(3045), l1(1594, 1, extra={29: 150010})))),
    scenario("two_packets_for_one_scrip_in_one_frame", {"subscribe": [Q]}, fr(frame(l1(3045, extra={29: 80500}), l1(3045, extra={29: 80600})))),
    scenario("a_compressed_frame", {"subscribe": [Q]}, fr(frame(l1(), compress=True))),
    scenario("a_compressed_frame_of_two_packets", {"subscribe": [Q, IQ]}, fr(frame(l1(3045), l1(1594), compress=True))),
    # ---- depth data ---------------------------------------------------------------------------------------------
    scenario("a_depth_packet_for_a_depth_subscription", {"subscribe": [D]}, fr(frame(l5()))),
    scenario("a_depth_packet_for_a_quote_subscription_publishes_nothing", {"subscribe": [Q]}, fr(frame(l5()))),
    scenario("a_depth_packet_with_a_short_ladder", {"subscribe": [D]}, fr(frame(l5(bids=[(80540, 100, 1), (80535, 200, 2)], asks=[(80550, 110, 2)], count=2)))),
    scenario("a_depth_packet_with_one_level", {"subscribe": [D]}, fr(frame(l5(bids=[(80540, 100, 1)], asks=[(80550, 110, 2)], count=1)))),
    scenario("a_depth_packet_with_no_levels", {"subscribe": [D]}, fr(frame(l5(bids=[], asks=[], count=0)))),
    scenario("a_depth_packet_with_a_level_count_of_zero_but_levels", {"subscribe": [D]}, fr(frame(l5(count=0)))),
    scenario("a_depth_packet_with_more_levels_than_five", {"subscribe": [D]}, fr(frame(l5(bids=[(80540 - i, 100 + i, 1) for i in range(7)], asks=[(80550 + i, 110 + i, 2) for i in range(7)], count=7)))),
    scenario("a_depth_packet_for_a_future", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, fr(frame(l5(49500, 3, total_buy=0, total_sell=0)))),
    scenario("a_depth_packet_for_currency", {"subscribe": [sub("USDINR25NOV26FUT", "CDS", 3)]}, fr(frame(l5(90001, 5, bids=[(834900000, 1000, 1)], asks=[(835100000, 1000, 1)], count=1)))),
    scenario("a_depth_packet_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}, fr(frame(l5(26000, 1)))),
    scenario("a_depth_packet_for_a_token_nobody_asked_for", {"subscribe": [D]}, fr(frame(l5(9999)))),
    scenario("a_depth_packet_with_zero_prices", {"subscribe": [D]}, fr(frame(l5(bids=[(0, 0, 0)] * 5, asks=[(0, 0, 0)] * 5)))),
    scenario("depth_and_quote_packets_on_one_scrip", {"subscribe": [Q, D]}, fr(frame(l1(), l5()))),
    scenario("a_compressed_depth_frame", {"subscribe": [D]}, fr(frame(l5(), compress=True))),
    # ---- frames and packets that are not data ---------------------------------------------------------------------
    scenario("a_frame_of_a_version_the_client_does_not_read_is_ignored", {"subscribe": [Q]}, fr(frame(l1(), version=2))),
    scenario("a_packet_of_length_zero_ends_the_frame", {"subscribe": [Q]}, fr(frame(l1()) + struct.pack("<h", 0) + l1())),
    scenario("a_packet_of_a_type_nobody_knows_is_skipped", {"subscribe": [Q]}, fr(frame(struct.pack("<hb", 3, 99), l1()))),
    scenario("an_auth_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(13, (25, 1))))),
    scenario("a_failed_auth_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(13, (25, 0))))),
    scenario("a_ping_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(16, (62, 1))))),
    scenario("a_market_status_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(14, (56, 2), (26, 1), (57, 1), (26, 3), (57, 2))))),
    scenario("an_event_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(15, (56, 5), (61, b"hello"))))),
    scenario("a_candle_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(12, (26, 1), (27, 3045), (30, 80100), (31, 80950), (32, 79900), (33, 80000), (40, 100), (46, 1793699999), (75, b"1min"))))),
    scenario("a_greeks_packet_publishes_nothing", {"subscribe": [Q]}, fr(frame(packet(17, (26, 1), (27, 3045), (63, 0.5), (64, 0.2), (65, 0.4))))),
    scenario("a_packet_after_a_non_data_packet_is_still_read", {"subscribe": [Q]}, fr(frame(packet(16, (62, 1)), l1()))),
    scenario("a_frame_with_no_packets", {"subscribe": [Q]}, fr(frame())),
    scenario("a_frame_shorter_than_its_header", {"subscribe": [Q]}, fr(b"\x01\x00\x00")),
    # ---- frames and packets the client cannot read --------------------------------------------------------------
    scenario("a_packet_with_an_unknown_field_is_an_error", {"subscribe": [Q]}, fr(frame(packet(10, (26, 1), (27, 3045), raw=b"\x63\x00\x00\x00\x00")))),
    scenario("a_packet_with_an_unknown_segment_is_an_error", {"subscribe": [Q]}, fr(frame(packet(10, (26, 77), (27, 3045), (29, 100))))),
    scenario("a_packet_with_no_segment_is_an_error", {"subscribe": [Q]}, fr(frame(packet(10, (27, 3045), (29, 100))))),
    scenario("a_packet_that_is_cut_short_is_an_error", {"subscribe": [Q]}, fr(struct.pack("<i", 14) + bytes([1, 0]) + struct.pack("<h", 12) + bytes([10, 26, 1, 27, 0xDD, 0x0B]))),
    scenario("a_precision_field_in_a_quote_packet_is_an_error", {"subscribe": [Q]}, fr(frame(packet(10, (26, 1), (27, 3045), (28, 2), (29, 80545))))),
    scenario("a_packet_error_drops_the_rest_of_the_frame", {"subscribe": [Q, IQ]}, fr(frame(l1(3045), packet(10, (26, 77), (27, 1594)), l1(1594)))),
    scenario("a_frame_that_is_not_compressed_but_says_it_is_is_an_error", {"subscribe": [Q]}, fr(struct.pack("<i", 4) + bytes([1, 100]) + b"abcd")),
]
