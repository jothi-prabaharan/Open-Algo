"""HDFC Securities feed: the JSON frames the stream is sent to subscribe (LTP or ALL, a hundred scrips a frame) and what the adapter publishes for the protobuf packets it gets back."""

import json
import struct

NOW_MS = 1_793_700_000_123
BROKER = "hdfcsecurities"
AUTH_TOKEN = "hdfc-access-token"
ENV = {"BROKER_API_KEY": "hdfc-api-key", "BROKER_API_SECRET": "hdfc-api-secret"}


def row(symbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0,
            "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 1001), row("INFY", "NSE", 1002), row("SBIN", "BSE", 1001), row("RELIANCE", "BSE", 2003),
    row("NIFTY25NOV26FUT", "NFO", 50001, "FUT"), row("SENSEX26NOVFUT", "BFO", 60001, "FUT"), row("USDINR25NOV26FUT", "CDS", 1001, "FUT"),
    row("NIFTY", "NSE_INDEX", 26000, "INDEX"), row("SENSEX", "BSE_INDEX", 26001, "INDEX"), row("GOLDM25NOV26FUT", "MCX", 80001, "FUT"),
    row("CRUDE25NOV26FUT", "NCO", 90001, "FUT"), row("BADTOKEN", "NSE", "abc"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i) for i in range(1600)]


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
    return varint(n << 3) + varint(v) if v else b""


def dfield(n, v):
    return varint((n << 3) | 1) + struct.pack("<d", v) if v else b""


def lfield(n, payload):
    return varint((n << 3) | 2) + varint(len(payload)) + payload


def level(qty, price, orders, buy):
    return vfield(1, qty) + dfield(2, price) + vfield(3, orders) + vfield(4, 1 if buy else 0)


def mbp(ltp=0, ltt=0, open_=0, high=0, close=0, low=0, volume=0, ltq=0, avg=0, buys=(), sells=(), tbq=0, tsq=0, lower=0, upper=0, oi=0):
    b = dfield(1, ltp) + vfield(2, ltt) + dfield(5, open_) + dfield(6, high) + dfield(7, close) + dfield(8, low) + vfield(9, volume) + vfield(10, ltq) + dfield(11, avg)
    levels = b"".join(lfield(1, level(q, p, o, True)) for p, q, o in buys) + b"".join(lfield(1, level(q, p, o, False)) for p, q, o in sells)
    if levels:
        b += lfield(12, levels)
    return b + vfield(13, tbq) + vfield(14, tsq) + dfield(15, lower) + dfield(16, upper) + vfield(17, oi)


def index(value=0, high=0, low=0, open_=0, close=0, ts=0):
    return dfield(2, value) + dfield(3, high) + dfield(4, low) + dfield(5, open_) + dfield(6, close) + vfield(10, ts)


def dto(iid, ptype, body=None, idx=None, ts=1793699999000, greek=None):
    b = vfield(1, iid)
    if body is not None:
        b += lfield(2, body)
    if idx is not None:
        b += lfield(3, idx)
    b += vfield(8, ts) + vfield(9, ptype)
    if greek is not None:
        b += lfield(10, greek)
    return b


def envelope(*packets):
    return b"".join(lfield(1, p) for p in packets)


NSE_CM, NSE_CD, NSE_INDEX, NSE_FO, BSE_CM, BSE_INDEX, BSE_FO, MCX = 0, 1, 2, 3, 4, 5, 6, 7
ORDER, NSE_CM_CIRC, NSE_CD_CIRC, NSE_CD_OI, NSE_FO_CIRC, NSE_FO_OI, BSE_FO_OI, HEARTBEAT, NSE_FO_GREEK, BSE_FO_GREEK = 8, 10, 11, 12, 13, 14, 15, 16, 17, 18

BUYS = [(805.4 - 0.05 * i, 100 * (i + 1), i + 1) for i in range(5)]
SELLS = [(805.5 + 0.05 * i, 110 * (i + 1), 2 * (i + 1)) for i in range(5)]


def full(ltp=805.45, **kw):
    base = dict(ltt=1793699998000, open_=801.0, high=809.5, close=800.0, low=799.0, volume=1234567, ltq=25, avg=804.1, buys=BUYS, sells=SELLS, tbq=50000, tsq=61000, lower=720.0, upper=880.0, oi=0)
    base.update(kw)
    return mbp(ltp=ltp, **base)


class Driver:
    """Runs the real adapter and its real client with a fake socket, the subscription thread replaced by explicit calls."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.hdfcsecurities.streaming.hdfcsecurities_adapter import HDFCSecuritiesWebSocketAdapter
        from broker.hdfcsecurities.streaming.hdfcsecurities_websocket import HDFCSecuritiesWebSocket

        self.adapter_cls, self.ws_cls = HDFCSecuritiesWebSocketAdapter, HDFCSecuritiesWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(AUTH_TOKEN, on_ticks=adapter._on_ticks, user_id="u")
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
        ws.SUBSCRIPTION_COLLECT_DELAY = 0
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": [json.loads(x) for x in self.sent], "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.ws._process_pending_subscriptions()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        self.ws._process_pending_subscriptions()
        return self._take()

    def reconnect(self, _):
        self.ws._resubscribe_all()
        return self._take()

    def frame(self, hex_text):
        self.ws._on_ws_message(self.ws.ws, bytes.fromhex(hex_text))
        return self._take()

    def text(self, text):
        self.ws._on_ws_message(self.ws.ws, text)
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(*packets):
    return {"frame": envelope(*packets).hex()}


def bare(packet):
    return {"frame": packet.hex()}


S = sub("SBIN", "NSE", 1)
SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}]},
    {"name": "several_symbols_are_grouped_by_type_in_one_window", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY", "NSE_INDEX", 3), sub("SENSEX26NOVFUT", "BFO", 2)]}]},
    {"name": "every_streamed_exchange_and_its_prefix", "steps": [{"subscribe": [
        sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("USDINR25NOV26FUT", "CDS", 1),
        sub("GOLDM25NOV26FUT", "MCX", 1), sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1), sub("CRUDE25NOV26FUT", "NCO", 1)]}]},
    {"name": "a_higher_mode_changes_the_type_and_a_lower_one_does_not_change_it_back", "steps": [{"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [S]}]},
    {"name": "quote_and_depth_in_one_window_ask_twice_for_the_same_type", "steps": [{"subscribe": [sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)]}]},
    {"name": "ltp_then_depth_in_one_window_sends_only_the_wider_type", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]}]},
    {"name": "dropping_the_highest_mode_subscribes_again_at_the_next", "steps": [{"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"unsubscribe": S}]},
    {"name": "dropping_a_lower_mode_subscribes_again_at_the_same_type", "steps": [{"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": S}]},
    {"name": "dropping_the_last_mode_unsubscribes_with_the_type_that_was_sent", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}]},
    {"name": "dropping_a_ltp_subscription_names_its_type", "steps": [{"subscribe": [S]}, {"unsubscribe": S}]},
    {"name": "unsubscribe_of_an_instrument_never_subscribed_still_sends_one", "steps": [{"unsubscribe": sub("INFY", "NSE", 1)}]},
    {"name": "unsubscribe_of_a_mode_never_subscribed_asks_again_at_the_held_type", "steps": [{"subscribe": [S]}, {"unsubscribe": sub("SBIN", "NSE", 3)}]},
    {"name": "subscribe_again_after_unsubscribing_sends_the_subscribe_again", "steps": [{"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}]},
    {"name": "the_same_token_on_two_exchanges_is_two_subscriptions", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 1), sub("USDINR25NOV26FUT", "CDS", 2)]}, {"unsubscribe": sub("SBIN", "BSE", 1)}]},
    {"name": "reconnect_repeats_every_subscription_grouped_by_type", "steps": [{"subscribe": [S, sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY", "NSE_INDEX", 1)]}, {"reconnect": None}]},
    {"name": "reconnect_after_an_unsubscribe_does_not_bring_it_back", "steps": [{"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": None}]},
    {"name": "reconnect_with_nothing_subscribed_sends_nothing", "steps": [{"reconnect": None}]},
    {"name": "a_hundred_and_fifty_scrips_go_in_frames_of_a_hundred", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(150)]}, {"reconnect": None}]},
    {"name": "fifteen_hundred_are_taken_and_the_next_is_refused", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1500)]}, {"subscribe": [sub("TKN1500", "NSE", 1)]}, {"subscribe": [sub("TKN5", "NSE", 3)]}, frame(dto(900000 + 1500, NSE_CM, full()))]},
    {"name": "a_freed_place_under_the_limit_can_be_taken", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(1500)]}, {"unsubscribe": sub("TKN7", "NSE", 1)}, {"subscribe": [sub("TKN1500", "NSE", 1)]}]},
    {"name": "a_symbol_without_a_numeric_token_is_refused", "steps": [{"subscribe": [sub("BADTOKEN", "NSE", 1)]}]},
    {"name": "ltp_packet_for_a_ltp_subscription", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)))]},
    {"name": "ltp_packet_without_a_trade_time_or_close", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45), ts=0))]},
    {"name": "quote_packet_for_a_quote_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(dto(1001, NSE_CM, full(oi=1500)))]},
    {"name": "quote_packet_carries_open_interest_zero_when_none_was_sent", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(dto(1001, NSE_CM, full()))]},
    {"name": "depth_packet_fans_out_to_depth_quote_and_ltp", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(dto(50001, NSE_FO, full(2400.55, oi=12000)))]},
    {"name": "depth_packet_for_a_depth_only_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full()))]},
    {"name": "a_packet_without_levels_has_no_depth", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(buys=(), sells=())))]},
    {"name": "a_packet_with_one_side_only", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(sells=())))]},
    {"name": "a_packet_with_seven_levels_a_side_is_cut_to_five", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(buys=[(805.4 - 0.05 * i, 10, 1) for i in range(7)], sells=[(805.5 + 0.05 * i, 10, 1) for i in range(7)])))]},
    {"name": "a_packet_without_a_band_publishes_no_circuit_limits", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(lower=0, upper=0)))]},
    {"name": "a_packet_with_a_zero_close_has_no_change", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "a_fall_below_the_close_is_a_negative_change", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=780.35, close=800.0)))]},
    {"name": "each_packet_type_names_its_exchange", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("USDINR25NOV26FUT", "CDS", 1), sub("GOLDM25NOV26FUT", "MCX", 1)]},
        frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0))), frame(dto(1001, BSE_CM, mbp(ltp=805.5, close=800.0))), frame(dto(50001, NSE_FO, mbp(ltp=2400.5, close=2390.0))),
        frame(dto(60001, BSE_FO, mbp(ltp=75000.5, close=74900.0))), frame(dto(1001, NSE_CD, mbp(ltp=83.2525, close=83.0))), frame(dto(80001, MCX, mbp(ltp=75000.0, close=74500.0)))]},
    {"name": "a_packet_is_matched_by_its_own_exchange_not_by_the_token", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, frame(dto(1001, BSE_CM, mbp(ltp=805.5, close=800.0))), frame(dto(1001, NSE_CD, mbp(ltp=83.2, close=83.0)))]},
    {"name": "index_packets_for_both_exchanges", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 2)]},
        frame(dto(26000, NSE_INDEX, idx=index(24050.5, 24100.0, 23900.0, 23950.0, 23980.0, ts=1793699997000))),
        frame(dto(26001, BSE_INDEX, idx=index(79050.5, 79500.0, 78900.0, 78950.0, 78980.0)))]},
    {"name": "an_index_packet_for_a_depth_subscription", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}, frame(dto(26000, NSE_INDEX, idx=index(24050.5, 24100.0, 23900.0, 23950.0, 23980.0)))]},
    {"name": "a_circuit_packet_updates_the_last_snapshot", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full())), frame(dto(1001, NSE_CM_CIRC, mbp(lower=700.0, upper=900.0), ts=1793700000500))]},
    {"name": "an_open_interest_packet_updates_the_last_snapshot", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, frame(dto(50001, NSE_FO, full(2400.55, oi=12000))), frame(dto(50001, NSE_FO_OI, mbp(oi=12500), ts=1793700000500))]},
    {"name": "every_partial_packet_type_is_merged", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3), sub("SBIN", "NSE", 3), sub("USDINR25NOV26FUT", "CDS", 3), sub("SENSEX26NOVFUT", "BFO", 3)]},
        frame(dto(50001, NSE_FO, full())), frame(dto(1001, NSE_CM, full())), frame(dto(1001, NSE_CD, full())), frame(dto(60001, BSE_FO, full())),
        frame(dto(50001, NSE_FO_CIRC, mbp(lower=1.5, upper=2.5))), frame(dto(1001, NSE_CD_CIRC, mbp(lower=3.5, upper=4.5))), frame(dto(1001, NSE_CD_OI, mbp(oi=7))),
        frame(dto(60001, BSE_FO_OI, mbp(oi=9))), frame(dto(1001, NSE_CM_CIRC, mbp(lower=5.5, upper=6.5)))]},
    {"name": "a_partial_packet_before_any_full_one_publishes_nothing", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM_CIRC, mbp(lower=700.0, upper=900.0))), frame(dto(1001, NSE_CD_OI, mbp(oi=5)))]},
    {"name": "a_partial_packet_after_everything_was_unsubscribed_publishes_nothing", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full())), {"unsubscribe": sub("SBIN", "NSE", 3)}, {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM_CIRC, mbp(lower=700.0, upper=900.0)))]},
    {"name": "a_partial_packet_carries_the_new_time_but_keeps_the_trade_time", "steps": [
        {"subscribe": [S]}, frame(dto(1001, NSE_CM, full())), frame(dto(1001, NSE_CM_CIRC, mbp(lower=700.0, upper=900.0), ts=1793700001000))]},
    {"name": "greek_packets_are_not_published", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(dto(50001, NSE_FO_GREEK, greek=dfield(1, 0.5) + dfield(2, 0.01)))]},
    {"name": "a_heartbeat_packet_carries_nothing", "steps": [{"subscribe": [S]}, frame(dto(1001, HEARTBEAT))]},
    {"name": "a_packet_without_an_instrument_is_dropped", "steps": [{"subscribe": [S]}, frame(dto(0, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "an_unmapped_packet_type_falls_back_to_a_unique_token", "steps": [{"subscribe": [sub("INFY", "NSE", 1)]}, frame(dto(1002, ORDER, mbp(ltp=1500.5, close=1490.0)))]},
    {"name": "an_unmapped_packet_type_with_a_shared_token_is_dropped", "steps": [{"subscribe": [S, sub("SBIN", "BSE", 1)]}, frame(dto(1001, ORDER, mbp(ltp=805.45, close=800.0)))]},
    {"name": "an_unmapped_packet_type_without_a_quote_is_dropped", "steps": [{"subscribe": [sub("INFY", "NSE", 1)]}, frame(dto(1002, ORDER))]},
    {"name": "several_packets_in_one_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1), sub("NIFTY", "NSE_INDEX", 1)]},
        frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)), dto(1002, NSE_CM, mbp(ltp=1500.5, close=1490.0)), dto(26000, NSE_INDEX, idx=index(24050.5, close=23980.0)), dto(0, HEARTBEAT))]},
    {"name": "a_bare_packet_is_read_as_one", "steps": [{"subscribe": [S]}, bare(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)))]},
    {"name": "a_packet_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [S]}, frame(dto(1002, NSE_CM, mbp(ltp=1500.5)))]},
    {"name": "a_packet_after_everything_was_unsubscribed", "steps": [{"subscribe": [S]}, {"unsubscribe": S}, frame(dto(1001, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "a_packet_after_a_mode_was_dropped_publishes_the_modes_left", "steps": [
        {"subscribe": [S, sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, frame(dto(1001, NSE_CM, full()))]},
    {"name": "an_unknown_field_inside_a_packet_is_skipped", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)) + vfield(30, 5) + lfield(31, b"zz"))]},
    {"name": "an_empty_frame_carries_nothing", "steps": [{"subscribe": [S]}, {"frame": ""}]},
    {"name": "a_frame_cut_off_in_the_middle_is_dropped", "steps": [{"subscribe": [S]}, {"frame": envelope(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0))).hex()[:-8]}]},
    {"name": "garbage_is_dropped", "steps": [{"subscribe": [S]}, {"frame": "ffffffffffffffffffffffff"}]},
    {"name": "a_text_acknowledgement_carries_no_ticks", "steps": [{"subscribe": [S]}, {"text": json.dumps({"status": "success", "message": "subscribed"})}]},
    {"name": "a_text_error_carries_no_ticks", "steps": [{"subscribe": [S]}, {"text": json.dumps({"status": "error", "message": "rate limited"})}]},
    {"name": "text_that_is_not_json_carries_nothing", "steps": [{"subscribe": [S]}, {"text": "hello"}]},
]
