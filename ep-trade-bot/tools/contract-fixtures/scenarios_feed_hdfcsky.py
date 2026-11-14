"""HDFC Sky feed: the same JSON frames and GenericDTO packets as HDFC Securities, but one mode per token, no exchange from the packet type, no merging of circuit and open-interest packets, and 300 scrips a connection."""

import json

import scenarios_feed_hdfcsecurities as h
from scenarios_feed_hdfcsecurities import (BSE_CM, BSE_FO, BSE_FO_GREEK, BSE_FO_OI, BSE_INDEX, HEARTBEAT, MCX, NSE_CD, NSE_CD_CIRC, NSE_CD_OI, NSE_CM, NSE_CM_CIRC, NSE_FO, NSE_FO_CIRC,  # noqa: F401
                                            NSE_FO_GREEK, NSE_FO_OI, NSE_INDEX, ORDER, bare, dfield, dto, envelope, frame, full, index, lfield, mbp, sub, vfield, wide_envelope)

NOW_MS = h.NOW_MS
BROKER = "hdfcsky"
AUTH_TOKEN = "sky-access-token"
ENV = {"BROKER_API_KEY": "sky-api-key", "BROKER_API_SECRET": "sky-api-secret"}
row = h.row

SYMBOLS = [r for r in h.SYMBOLS if not r["symbol"].startswith("TKN")] + [row(f"TKN{i}", "NSE", 900000 + i) for i in range(320)]


class Driver:
    """Runs the real adapter and its real client with a fake socket, the subscription thread replaced by explicit calls."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.hdfcsky.streaming.hdfcsky_adapter import HDFCSkyWebSocketAdapter
        from broker.hdfcsky.streaming.hdfcsky_websocket import HDFCSkyWebSocket

        self.adapter_cls, self.ws_cls = HDFCSkyWebSocketAdapter, HDFCSkyWebSocket

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


S = sub("SBIN", "NSE", 1)
SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [{"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}]},
    {"name": "several_symbols_are_grouped_by_type_in_one_window", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 1), sub("NIFTY", "NSE_INDEX", 3), sub("SENSEX26NOVFUT", "BFO", 2)]}]},
    {"name": "every_streamed_exchange_and_its_prefix", "steps": [{"subscribe": [
        sub("SBIN", "NSE", 1), sub("RELIANCE", "BSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("USDINR25NOV26FUT", "CDS", 1),
        sub("GOLDM25NOV26FUT", "MCX", 1), sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1), sub("CRUDE25NOV26FUT", "NCO", 1)]}]},
    {"name": "the_last_mode_asked_for_is_the_one_kept", "steps": [{"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [S]}]},
    {"name": "two_modes_in_one_window_send_the_token_twice", "steps": [{"subscribe": [sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)]}]},
    {"name": "unsubscribing_any_mode_drops_the_token_with_the_type_that_was_sent", "steps": [{"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": S}]},
    {"name": "unsubscribe_names_a_ltp_type", "steps": [{"subscribe": [S]}, {"unsubscribe": S}]},
    {"name": "unsubscribe_names_the_all_type", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}]},
    {"name": "unsubscribe_of_an_instrument_never_subscribed_still_sends_one", "steps": [{"unsubscribe": sub("INFY", "NSE", 1)}]},
    {"name": "subscribe_again_after_unsubscribing_sends_the_subscribe_again", "steps": [{"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}]},
    {"name": "the_same_token_on_two_exchanges_is_one_subscription_state", "steps": [{"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "BSE", 2)]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0))), frame(dto(1001, BSE_CM, mbp(ltp=805.5, close=800.0)))]},
    {"name": "reconnect_repeats_every_subscription_grouped_by_type", "steps": [{"subscribe": [S, sub("INFY", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY", "NSE_INDEX", 1)]}, {"reconnect": None}]},
    {"name": "reconnect_after_an_unsubscribe_does_not_bring_it_back", "steps": [{"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": None}]},
    {"name": "reconnect_with_nothing_subscribed_sends_nothing", "steps": [{"reconnect": None}]},
    {"name": "a_hundred_and_fifty_scrips_go_in_frames_of_a_hundred", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(150)]}, {"reconnect": None}]},
    {"name": "three_hundred_are_taken_and_the_next_is_refused", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(300)]}, {"subscribe": [sub("TKN300", "NSE", 1)]}, {"subscribe": [sub("TKN5", "NSE", 3)]}, frame(dto(900000 + 300, NSE_CM, full()))]},
    {"name": "a_freed_place_under_the_limit_can_be_taken", "steps": [
        {"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(300)]}, {"unsubscribe": sub("TKN7", "NSE", 1)}, {"subscribe": [sub("TKN300", "NSE", 1)]}]},
    {"name": "a_symbol_without_a_numeric_token_is_refused", "steps": [{"subscribe": [sub("BADTOKEN", "NSE", 1)]}]},
    {"name": "ltp_packet_for_a_ltp_subscription", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)))]},
    {"name": "ltp_packet_without_a_trade_time_or_close", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45), ts=0))]},
    {"name": "quote_packet_for_a_quote_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(dto(1001, NSE_CM, full(oi=1500)))]},
    {"name": "quote_packet_carries_open_interest_zero_when_none_was_sent", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, frame(dto(1001, NSE_CM, full()))]},
    {"name": "depth_packet_for_a_depth_subscription", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(dto(50001, NSE_FO, full(2400.55, oi=12000)))]},
    {"name": "a_packet_without_levels_has_no_depth", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(buys=(), sells=())))]},
    {"name": "a_packet_with_one_side_only", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(sells=())))]},
    {"name": "a_packet_with_seven_levels_a_side_is_cut_to_five", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(buys=[(805.4 - 0.05 * i, 10, 1) for i in range(7)], sells=[(805.5 + 0.05 * i, 10, 1) for i in range(7)])))]},
    {"name": "a_packet_without_a_band_publishes_no_circuit_limits", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full(lower=0, upper=0)))]},
    {"name": "a_packet_with_a_zero_close_has_no_change", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "a_fall_below_the_close_is_a_negative_change", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=780.35, close=800.0)))]},
    {"name": "each_packet_type_is_a_quote", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("RELIANCE", "BSE", 1), sub("GOLDM25NOV26FUT", "MCX", 1)]},
        frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0))), frame(dto(50001, NSE_FO, mbp(ltp=2400.5, close=2390.0))), frame(dto(60001, BSE_FO, mbp(ltp=75000.5, close=74900.0))),
        frame(dto(2003, BSE_CM, mbp(ltp=2500.5, close=2490.0))), frame(dto(80001, MCX, mbp(ltp=75000.0, close=74500.0)))]},
    {"name": "index_packets_for_both_exchanges", "steps": [
        {"subscribe": [sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 2)]},
        frame(dto(26000, NSE_INDEX, idx=index(24050.5, 24100.0, 23900.0, 23950.0, 23980.0, ts=1793699997000))),
        frame(dto(26001, BSE_INDEX, idx=index(79050.5, 79500.0, 78900.0, 78950.0, 78980.0)))]},
    {"name": "an_index_packet_for_a_depth_subscription", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}, frame(dto(26000, NSE_INDEX, idx=index(24050.5, 24100.0, 23900.0, 23950.0, 23980.0)))]},
    {"name": "a_circuit_packet_is_published_as_a_quote_of_zeros", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, full())), frame(dto(1001, NSE_CM_CIRC, mbp(lower=700.0, upper=900.0), ts=1793700000500))]},
    {"name": "an_open_interest_packet_is_published_as_a_quote_of_zeros", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, frame(dto(50001, NSE_FO, full(2400.55, oi=12000))), frame(dto(50001, NSE_FO_OI, mbp(oi=12500), ts=1793700000500))]},
    {"name": "every_partial_packet_type_is_published_as_it_is", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3), sub("SBIN", "NSE", 3), sub("USDINR25NOV26FUT", "CDS", 3), sub("SENSEX26NOVFUT", "BFO", 3)]},
        frame(dto(50001, NSE_FO_CIRC, mbp(lower=1.5, upper=2.5))), frame(dto(1001, NSE_CM_CIRC, mbp(lower=5.5, upper=6.5))), frame(dto(1001, NSE_CD_CIRC, mbp(lower=3.5, upper=4.5))),
        frame(dto(1001, NSE_CD_OI, mbp(oi=7))), frame(dto(60001, BSE_FO_OI, mbp(oi=9))), frame(dto(50001, NSE_FO_OI, mbp(oi=11)))]},
    {"name": "greek_packets_are_not_published", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(dto(50001, NSE_FO_GREEK, greek=dfield(1, 0.5) + dfield(2, 0.01)))]},
    {"name": "a_greek_packet_with_a_body_is_not_published", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3), sub("SENSEX26NOVFUT", "BFO", 3)]},
        frame(dto(50001, NSE_FO_GREEK, full(), greek=dfield(1, 0.5))), frame(dto(60001, BSE_FO_GREEK := 18, full(), greek=dfield(1, 0.5)))]},
    {"name": "a_malformed_greek_packet_drops_the_whole_frame", "steps": [{"subscribe": [S, sub("NIFTY25NOV26FUT", "NFO", 3)]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45)), dto(50001, NSE_FO_GREEK, greek=b"\x09\x01"))]},
    {"name": "a_heartbeat_packet_carries_nothing", "steps": [{"subscribe": [S]}, frame(dto(1001, HEARTBEAT))]},
    {"name": "a_heartbeat_packet_with_a_quote_inside_is_still_a_heartbeat", "steps": [{"subscribe": [S]}, frame(dto(1001, HEARTBEAT, mbp(ltp=805.45, close=800.0)))]},
    {"name": "a_packet_without_an_instrument_is_dropped", "steps": [{"subscribe": [S]}, frame(dto(0, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "a_packet_for_token_zero_is_dropped", "steps": [{"subscribe": [sub("ZERO", "NSE", 1)]}, frame(dto(0, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "an_unmapped_packet_type_with_a_quote_is_published", "steps": [{"subscribe": [sub("INFY", "NSE", 1)]}, frame(dto(1002, ORDER, mbp(ltp=1500.5, close=1490.0)))]},
    {"name": "an_unmapped_packet_type_without_a_quote_is_dropped", "steps": [{"subscribe": [sub("INFY", "NSE", 1)]}, frame(dto(1002, ORDER))]},
    {"name": "a_quote_packet_type_without_a_body_is_a_zero_quote", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3), sub("NIFTY25NOV26FUT", "NFO", 3), sub("SENSEX26NOVFUT", "BFO", 3), sub("RELIANCE", "BSE", 3), sub("USDINR25NOV26FUT", "CDS", 3), sub("GOLDM25NOV26FUT", "MCX", 3)]},
        frame(dto(1001, NSE_CM)), frame(dto(50001, NSE_FO)), frame(dto(60001, BSE_FO)), frame(dto(2003, BSE_CM)), frame(dto(1001, NSE_CD)), frame(dto(80001, MCX))]},
    {"name": "a_circuit_or_open_interest_packet_type_without_a_body_is_a_zero_quote", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 3), sub("NIFTY25NOV26FUT", "NFO", 3), sub("USDINR25NOV26FUT", "CDS", 3), sub("SENSEX26NOVFUT", "BFO", 3)]},
        frame(dto(1001, NSE_CM_CIRC)), frame(dto(1001, NSE_CD_CIRC)), frame(dto(50001, NSE_FO_CIRC)), frame(dto(1001, NSE_CD_OI)), frame(dto(50001, NSE_FO_OI)), frame(dto(60001, BSE_FO_OI))]},
    {"name": "several_packets_in_one_frame", "steps": [
        {"subscribe": [sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1), sub("NIFTY", "NSE_INDEX", 1)]},
        frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)), dto(1002, NSE_CM, mbp(ltp=1500.5, close=1490.0)), dto(26000, NSE_INDEX, idx=index(24050.5, close=23980.0)), dto(0, HEARTBEAT))]},
    {"name": "a_bare_packet_is_read_as_one", "steps": [{"subscribe": [S]}, bare(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)))]},
    {"name": "a_packet_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [S]}, frame(dto(1002, NSE_CM, mbp(ltp=1500.5)))]},
    {"name": "a_packet_after_everything_was_unsubscribed", "steps": [{"subscribe": [S]}, {"unsubscribe": S}, frame(dto(1001, NSE_CM, mbp(ltp=805.45)))]},
    {"name": "a_buy_flag_above_one_is_still_a_buy", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45) + lfield(12, lfield(1, vfield(1, 5) + dfield(2, 805.4) + vfield(3, 1) + vfield(4, 2)))))]},
    {"name": "an_unknown_field_inside_a_packet_is_skipped", "steps": [{"subscribe": [S]}, frame(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0)) + vfield(30, 5) + lfield(31, b"zz"))]},
    {"name": "an_empty_frame_carries_nothing", "steps": [{"subscribe": [S]}, {"frame": ""}]},
    {"name": "a_frame_cut_off_in_the_middle_is_dropped", "steps": [{"subscribe": [S]}, {"frame": envelope(dto(1001, NSE_CM, mbp(ltp=805.45, close=800.0))).hex()[:-8]}]},
    {"name": "garbage_is_dropped", "steps": [{"subscribe": [S]}, {"frame": "ffffffffffffffffffffffff"}]},
    {"name": "a_varint_of_eleven_bytes_drops_the_frame", "steps": [{"subscribe": [S]}, {"frame": wide_envelope(11, 0)}]},
    {"name": "a_ten_byte_varint_with_overflow_bits_is_read_as_protobuf_does", "steps": [{"subscribe": [S]}, {"frame": wide_envelope(10, 2)}]},
    {"name": "a_fixed64_field_is_skipped", "steps": [{"subscribe": [S]}, {"frame": "49" + "01" * 8 + envelope(dto(1001, NSE_CM, mbp(ltp=805.45))).hex()}]},
    {"name": "a_fixed32_field_is_skipped", "steps": [{"subscribe": [S]}, {"frame": "4d" + "01" * 4 + envelope(dto(1001, NSE_CM, mbp(ltp=805.45))).hex()}]},
    {"name": "a_group_field_drops_the_frame", "steps": [{"subscribe": [S]}, {"frame": "0b" + envelope(dto(1001, NSE_CM, mbp(ltp=805.45))).hex()}]},
    {"name": "a_zero_tag_drops_the_frame", "steps": [{"subscribe": [S]}, {"frame": "0000" + envelope(dto(1001, NSE_CM, mbp(ltp=805.45))).hex()}]},
    {"name": "a_varint_field_one_at_the_top_is_skipped_not_read_as_a_packet", "steps": [{"subscribe": [S]}, {"frame": "0805" + envelope(dto(1001, NSE_CM, mbp(ltp=805.45))).hex()}]},
    {"name": "a_text_acknowledgement_carries_no_ticks", "steps": [{"subscribe": [S]}, {"text": json.dumps({"status": "success", "message": "subscribed"})}]},
    {"name": "a_text_error_carries_no_ticks", "steps": [{"subscribe": [S]}, {"text": json.dumps({"status": "error", "message": "rate limited"})}]},
]
