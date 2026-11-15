"""Kotak Neo feed (SFeed): a JSON control plane and little-endian binary packets in batches. The connection is accepted by an auth frame; nothing is sent before the answer arrives, when the queued subscribe frames go out. Prices are integers divided by a per-exchange divider the answer carries. The adapter keeps a state per scrip, merging a partial tick over it, and publishes by the modes subscribed."""

import json
import struct
import types

NOW_MS = 1_793_700_000_123
BROKER = "kotak"
AUTH_TOKEN = "trade-token:::trade-sid:::https://kotak.test:::access-token:::E43"
ENV = {}
URL = "wss://feed.test/apifeed"


def row(symbol, exchange, token, brexchange, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045, "nse_cm"), row("INFY", "NSE", 1594, "nse_cm"), row("TATAMOTORS", "BSE", 500570, "bse_cm"), row("NIFTY25NOV26FUT", "NFO", 49500, "nse_fo", "FUT"),
    row("SENSEX26NOVFUT", "BFO", 60001, "bse_fo", "FUT"), row("USDINR25NOV26FUT", "CDS", 90001, "cde_fo", "FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, "mcx_fo", "FUT"),
    row("NIFTY", "NSE_INDEX", 26000, "nse_cm", "INDEX"), row("BANKNIFTY", "NSE_INDEX", 26009, "nse_cm", "INDEX"), row("MIDCPNIFTY", "NSE_INDEX", 26074, "nse_cm", "INDEX"),
    row("SENSEX", "BSE_INDEX", 1, "bse_cm", "INDEX"), row("OTHERIDX", "NSE_INDEX", 26099, "nse_cm", "INDEX"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i, "nse_cm") for i in range(30)]


class Driver:
    """Runs the real adapter and the real SFeed client with a fake socket; the batch timer is one explicit drain after each step."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.kotak.streaming import kotak_adapter
        from broker.kotak.streaming.sfeed_websocket import KotakSFeedWebSocket

        self.adapter_cls, self.client_cls = kotak_adapter.KotakWebSocketAdapter, KotakSFeedWebSocket

    def _client(self):
        client = self.client_cls({"auth_token": "trade-token", "sid": "trade-sid", "hs_server_id": "https://kotak.test", "access_token": "access-token", "data_center": "E43"}, ws_url=URL, ucc=None)
        client._ws = self.fake
        client._watch_auth = lambda ws: None
        return client

    def reset(self):
        self.sent, self.published = [], []
        sent = self.sent

        class Fake:
            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        self.fake = Fake()
        adapter = self.adapter_cls()
        adapter._ws_client = self._client()
        adapter._max_batch_size = adapter._ws_client.MAX_BATCH_SIZE
        adapter._setup_internal_callbacks()
        adapter._running = True
        adapter._start_batch_timer = lambda: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter = adapter

    def _drain(self):
        self.adapter._process_batch_subscriptions()

    def _take(self):
        out = {"sent": [json.loads(x) for x in self.sent], "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self._drain()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        self._drain()
        return self._take()

    def reconnect(self, _):
        adapter = self.adapter
        saved = dict(adapter.subscriptions)
        adapter._ws_client = self._client()
        adapter._setup_internal_callbacks()
        with adapter._lock:
            adapter._symbol_state.clear()
        adapter._ws_client._handle_open(self.fake)
        for info in saved.values():
            adapter.subscribe(info["symbol"], info["exchange"], info["mode"])
        self._drain()
        return self._take()

    def text(self, text):
        self.adapter._ws_client._handle_message(None, text if isinstance(text, str) else json.dumps(text))
        return self._take()

    def frame(self, hex_text):
        self.adapter._ws_client._handle_message(None, bytes.fromhex(hex_text))
        return self._take()


# ---- wire builders -----------------------------------------------------------------------------------------------

SEG = {"nse_cm": 1, "nse_fo": 2, "cde_fo": 3, "bse_cm": 5, "bse_fo": 6, "mcx_fo": 9}
EXCHANGES = {"nse_cm": {"value": 1, "divider": 100}, "nse_fo": {"value": 2, "divider": 100}, "cde_fo": {"value": 3, "divider": 10000}, "bse_cm": {"value": 5, "divider": 100},
             "bse_fo": {"value": 6, "divider": 100}, "mcx_fo": {"value": 9, "divider": 100}}
AUTH = {"message_code": 1119, "format": "native_batch", "exchanges": EXCHANGES}


def packet(code, segment, level, body, depth=b"", auction=0):
    return struct.pack("<HHbBBBB", 9 + len(body) + len(depth), code, SEG[segment], level, auction, 0, 0) + body + depth


def scrip(token, segment="nse_cm", level=4, ltp=80545, open_=80100, high=80950, low=79900, close=80000, volume=1234567, buy=None, sell=None, last_update=1793699999, **extra):
    buy = [(100, 80540, 1)] if buy is None else buy
    sell = [(110, 80550, 2)] if sell is None else sell
    body = struct.pack("<IqqqqqIIIIIqIIIIhiIdiIIIIIBI", int(token), 50000, 61000, volume, 1793699990, last_update, open_, close, high, low, ltp, 25, 80410, 0, len(buy), len(sell), 0, 25, 7000, 99999.5, 45,
                       88000, 72000, 90000, 60000, 1, 2, 1)
    rows = b"".join(struct.pack("<qii", q, p, o) for q, p, o in [*buy, *sell])
    return packet(extra.get("code", 7208), segment, level, body, rows, extra.get("auction", 0))


def mini(token, segment="nse_cm", ltp=80545, close=80000):
    return packet(0, segment, 1, struct.pack("<IqIqIiiIBI", int(token), 1793699999, ltp, 25, close, 12, 545, 1, 2, 100))


def index(token, name, value=2410025, segment="nse_cm", open_=2400000, close=2399000, high=2420000, low=2395000):
    return packet(7207, segment, 0, struct.pack("<IiiiiiQiiidBi21s", int(token), open_, close, high, low, value, 1793699999, 2500000, 2000000, 12, 0.0, 2, 100, name.encode()))


def hexed(*packets):
    return {"frame": b"".join(packets).hex()}


def depth_rows(n, base=80540, step=5):
    return [(100 * (i + 1), base - step * i, i + 1) for i in range(n)]


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


CONNECT = [{"reconnect": {}}, {"text": AUTH}]


def connected(name, *steps):
    return scenario(name, *CONNECT, *steps)


ACK = {"message_code": 1109, "trading_symbols": {"nse_cm|3045": "SBIN-EQ", "nse_cm|1594": "INFY-EQ", "nse_fo|49500": "NIFTY25NOV26FUT"}}
S = sub("SBIN", "NSE", 2)


def depth_packet(token, segment="nse_cm", n=5, **kw):
    return scrip(token, segment, level=8, buy=depth_rows(n), sell=depth_rows(n, 80550, -5), **kw)


SCENARIOS = [
    # ---- connecting ---------------------------------------------------------------------------------------------
    scenario("the_connection_sends_its_auth_frame", {"reconnect": {}}),
    scenario("subscribe_waits_for_the_auth_answer", *CONNECT[:1], {"subscribe": [S]}, CONNECT[1]),
    scenario("subscribe_before_connecting_is_replayed_on_connect", {"subscribe": [S]}, *CONNECT),
    scenario("the_documented_auth_code_is_accepted", *CONNECT[:1], {"subscribe": [S]}, {"text": {**AUTH, "message_code": 1117}}),
    scenario("another_code_is_not_the_auth_answer", *CONNECT[:1], {"subscribe": [S]}, {"text": {**AUTH, "message_code": 1118}}),
    scenario("a_downgraded_connection_is_not_accepted", *CONNECT[:1], {"subscribe": [S]}, {"text": {**AUTH, "format": "native_fallback"}}),
    scenario("dividers_default_to_one_hundred", *CONNECT[:1], {"text": {"message_code": 1119, "format": "native_batch", "exchanges": {"nse_cm": {"value": 1}}}}, {"subscribe": [S]}, hexed(scrip(3045))),
    scenario("a_divider_for_an_exchange_the_answer_names_only_by_its_name", *CONNECT[:1], {"text": {"message_code": 1119, "format": "native_batch", "exchanges": {"nse_cm": {"divider": 1000}}}}, {"subscribe": [S]}, hexed(scrip(3045))),
    scenario("an_exchange_entry_that_is_not_an_object_is_skipped", *CONNECT[:1], {"text": {"message_code": 1119, "format": "native_batch", "exchanges": {"nse_cm": 5, "nse_fo": {"value": 2, "divider": 100}}}}, {"subscribe": [S]}, hexed(scrip(3045))),
    scenario("a_frame_before_the_auth_answer_is_dropped", *CONNECT[:1], {"subscribe": [S]}, hexed(scrip(3045)), CONNECT[1], hexed(scrip(3045))),
    # ---- subscribing --------------------------------------------------------------------------------------------
    connected("subscribe_quote", {"subscribe": [S]}),
    connected("subscribe_ltp", {"subscribe": [sub("SBIN", "NSE", 1)]}),
    connected("subscribe_depth_goes_as_depth_then_quote", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    connected("subscribe_several_groups_by_kind", {"subscribe": [S, sub("INFY", "NSE", 3), sub("NIFTY25NOV26FUT", "NFO", 2), sub("TATAMOTORS", "BSE", 1), sub("GOLDM25NOV26FUT", "MCX", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("SENSEX26NOVFUT", "BFO", 2)]}),
    connected("subscribe_an_index_by_its_name", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}),
    connected("subscribe_an_index_for_depth_asks_the_same_name", {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}),
    connected("subscribe_the_bse_index", {"subscribe": [sub("SENSEX", "BSE_INDEX", 1)]}),
    connected("subscribe_an_index_with_several_names_asks_the_first", {"subscribe": [sub("MIDCPNIFTY", "NSE_INDEX", 2)]}),
    connected("subscribe_an_index_with_no_known_name_asks_its_symbol", {"subscribe": [sub("OTHERIDX", "NSE_INDEX", 2)]}),
    connected("subscribe_a_symbol_the_master_does_not_hold", {"subscribe": [sub("SBIN", "NSE", 2)]}, {"subscribe": [S]}),
    connected("subscribe_the_same_symbol_twice_sends_both", {"subscribe": [S]}, {"subscribe": [S]}),
    connected("subscribe_quote_then_depth_in_one_step_keeps_each_kind", {"subscribe": [S, sub("SBIN", "NSE", 3)]}),
    connected("subscribe_thirty_in_one_frame", {"subscribe": [sub(f"TKN{i}", "NSE", 2) for i in range(30)]}),
    # ---- unsubscribing ------------------------------------------------------------------------------------------
    connected("unsubscribe_quote", {"subscribe": [S]}, {"unsubscribe": S}),
    connected("unsubscribe_depth_sends_both_kinds", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    connected("unsubscribe_ltp_keeps_quote", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"unsubscribe": sub("SBIN", "NSE", 1)}),
    connected("unsubscribe_the_last_quote_mode", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"unsubscribe": S}),
    connected("unsubscribe_depth_keeps_the_quote_another_mode_needs", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    connected("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    connected("unsubscribe_an_index_depth", {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 3)}),
    connected("unsubscribe_something_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    connected("subscribe_after_unsubscribe_in_one_batch_keeps_the_last", {"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}),
    # ---- reconnecting -------------------------------------------------------------------------------------------
    connected("reconnect_sends_auth_and_waits", {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}),
    scenario("reconnect_replays_every_subscription_after_the_answer", *CONNECT, {"subscribe": [S, sub("INFY", "NSE", 3), sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": {}}, {"text": AUTH}),
    scenario("reconnect_drops_what_was_unsubscribed", *CONNECT, {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}, {"text": AUTH}),
    scenario("reconnect_with_nothing_subscribed", *CONNECT, {"reconnect": {}}, {"text": AUTH}),
    scenario("reconnect_forgets_the_symbols_from_acks", *CONNECT, {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), {"reconnect": {}}, {"text": AUTH}, hexed(scrip(3045))),
    scenario("reconnect_forgets_the_merged_state", *CONNECT, {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), {"reconnect": {}}, {"text": AUTH}, {"text": ACK}, hexed(mini(3045, ltp=0))),
    # ---- the subscribe acknowledgement ---------------------------------------------------------------------------
    connected("an_ack_gives_the_symbol_a_tick_carries", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045))),
    connected("an_ack_under_the_other_spelling_of_its_key", {"subscribe": [S]}, {"text": {"message_code": 1109, "tradingSymbols": {"nse_cm|3045": "SBIN-EQ"}}}, hexed(scrip(3045))),
    connected("an_ack_that_is_not_a_map_is_ignored", {"subscribe": [S]}, {"text": {"message_code": 1109, "trading_symbols": [1, 2]}}, hexed(scrip(3045))),
    connected("an_unsubscribe_forgets_the_symbol", {"subscribe": [S]}, {"text": ACK}, {"unsubscribe": S}, {"subscribe": [S]}, hexed(scrip(3045))),
    connected("an_unsubscribe_of_one_kind_keeps_the_symbol", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, {"unsubscribe": sub("SBIN", "NSE", 3)}, hexed(scrip(3045))),
    connected("a_text_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    connected("a_text_frame_that_is_a_list", {"subscribe": [S]}, {"text": [1, 2]}),
    connected("a_control_frame_nobody_reads", {"subscribe": [S]}, {"text": {"message_code": 1234, "x": 1}}),
    # ---- ticks --------------------------------------------------------------------------------------------------
    connected("a_quote_tick", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045))),
    connected("an_ltp_tick", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": ACK}, hexed(scrip(3045))),
    connected("a_depth_tick", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045))),
    connected("a_tick_reaches_every_mode", {"subscribe": [sub("SBIN", "NSE", 1), S, sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045))),
    connected("a_touch_tick_carries_the_best_bid_and_ask_only", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045))),
    connected("a_depth_with_fewer_than_five_levels_is_padded", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045, n=2))),
    connected("a_depth_with_more_than_five_levels_is_cut", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045, n=7))),
    connected("a_full_depth_tick", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045, level=16, buy=depth_rows(5), sell=depth_rows(5, 80550, -5)))),
    connected("a_level_two_tick_is_a_quote", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045, level=2))),
    connected("a_mini_tick", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": ACK}, hexed(mini(3045))),
    connected("a_mini_tick_keeps_the_state_a_quote_built", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), hexed(mini(3045, ltp=80600))),
    connected("a_mini_tick_with_no_price_publishes_nothing", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), hexed(mini(3045, ltp=0))),
    connected("a_tick_with_no_price_and_no_depth_publishes_nothing", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": ACK}, hexed(scrip(3045, ltp=0))),
    connected("a_zero_field_in_a_complete_tick_is_published_as_zero", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), hexed(scrip(3045, open_=0, high=0))),
    connected("a_tick_before_its_symbol_is_known_merges_nothing", {"subscribe": [S]}, hexed(scrip(3045)), {"text": ACK}, hexed(scrip(3045))),
    connected("a_tick_for_a_token_nobody_asked_for", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(9999))),
    connected("a_tick_on_another_segment", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045, "bse_cm"))),
    connected("a_future_tick", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"text": ACK}, hexed(scrip(49500, "nse_fo", ltp=2400550))),
    connected("a_currency_tick_uses_its_own_divider", {"subscribe": [sub("USDINR25NOV26FUT", "CDS", 2)]}, hexed(scrip(90001, "cde_fo", ltp=835000, open_=834000, high=836000, low=833000, close=834500))),
    connected("a_batch_of_several_packets_in_one_frame", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": ACK}, hexed(scrip(3045), scrip(1594, ltp=150050))),
    connected("a_truncated_packet_ends_the_scan_and_keeps_the_ones_before", {"subscribe": [S]}, {"text": ACK}, {"frame": (scrip(3045) + scrip(1594)[:40]).hex()}),
    connected("a_packet_too_short_for_its_body_stops_the_frame", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": ACK}, {"frame": (packet(7208, "nse_cm", 4, b"\x00" * 20) + scrip(3045)).hex()}),
    connected("a_packet_with_an_unknown_level_is_ignored", {"subscribe": [S]}, {"text": ACK}, {"frame": packet(7208, "nse_cm", 3, b"\x00" * 135).hex()}),
    connected("a_market_status_packet_is_ignored", {"subscribe": [S]}, {"text": ACK}, {"frame": packet(6511, "nse_cm", 0, b"").hex()}),
    connected("a_market_status_with_a_body_is_ignored", {"subscribe": [S]}, {"text": ACK}, {"frame": packet(105, "nse_cm", 0, struct.pack("<H5s", 1, b"open")).hex()}),
    connected("a_closing_auction_packet_is_ignored", {"subscribe": [S]}, {"text": ACK}, {"frame": packet(104, "nse_cm", 0, struct.pack("<IIqq", 3045, 80000, 10, 5)).hex()}),
    connected("a_negative_update_time_is_zero", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045, last_update=-2208988800))),
    # ---- merging ------------------------------------------------------------------------------------------------
    connected("a_depth_tick_with_a_zero_level_keeps_the_previous_level", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045)), hexed(scrip(3045, level=8, buy=[(0, 0, 0)] * 5, sell=[(110, 80550, 2)] * 5))),
    connected("a_depth_tick_with_fewer_levels_keeps_the_rest", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045)), hexed(depth_packet(3045, n=2))),
    connected("a_quote_after_a_depth_publishes_the_cached_depth", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045)), hexed(scrip(3045, ltp=80600))),
    connected("a_depth_subscriber_with_no_depth_yet_gets_nothing", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(mini(3045))),
    connected("the_last_price_is_cached_for_a_tick_without_one", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(depth_packet(3045)), hexed(scrip(3045, ltp=0, buy=depth_rows(5), sell=depth_rows(5, 80550, -5))), hexed(mini(3045, ltp=0))),
    connected("an_unsubscribe_forgets_the_cached_values", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), {"unsubscribe": S}, {"subscribe": [S]}, {"text": ACK}, hexed(mini(3045, ltp=0))),
    connected("an_unsubscribe_of_one_mode_keeps_the_cached_values", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": ACK}, hexed(scrip(3045)), {"unsubscribe": sub("SBIN", "NSE", 1)}, hexed(mini(3045, ltp=0))),
    # ---- indices ------------------------------------------------------------------------------------------------
    connected("an_index_tick_by_its_token", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, hexed(index(26000, "Nifty 50"))),
    connected("an_index_tick_by_its_name", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, hexed(index(1, "Nifty 50"))),
    connected("an_index_tick_for_an_ltp_subscriber", {"subscribe": [sub("NIFTY", "NSE_INDEX", 1)]}, hexed(index(26000, "Nifty 50"))),
    connected("an_index_depth_subscriber_gets_an_empty_book", {"subscribe": [sub("NIFTY", "NSE_INDEX", 3)]}, hexed(index(26000, "Nifty 50"))),
    connected("an_index_tick_with_no_value_publishes_nothing", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, hexed(index(26000, "Nifty 50", value=0))),
    connected("an_index_tick_for_a_name_the_symbol_has_among_its_candidates", {"subscribe": [sub("MIDCPNIFTY", "NSE_INDEX", 2)]}, hexed(index(5, "Nifty Midcap Sel"))),
    connected("an_index_tick_for_the_bse_index", {"subscribe": [sub("SENSEX", "BSE_INDEX", 2)]}, hexed(index(1, "SENSEX", 8000025, "bse_cm", 7990000, 7995000, 8010000, 7980000))),
    connected("an_index_tick_for_an_index_nobody_asked_for", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, hexed(index(26009, "Nifty Bank"))),
    # ---- partial ticks ------------------------------------------------------------------------------------------
    connected("a_tick_with_no_price_and_no_symbol_merges_over_the_state", {"subscribe": [S]}, hexed(scrip(3045)), hexed(mini(3045, ltp=80600, close=0))),
    connected("a_partial_tick_with_no_state_builds_one", {"subscribe": [S]}, hexed(mini(3045, ltp=80600, close=0))),
    connected("a_partial_tick_without_a_price_publishes_the_held_one_to_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, hexed(scrip(3045)), hexed(mini(3045, ltp=0, close=0))),
    connected("a_blank_volume_marker_in_a_first_partial_tick_is_not_kept", {"subscribe": [S]}, hexed(scrip(3045, ltp=0, open_=0, high=0, low=0, close=0, volume=2147483648, buy=[], sell=[])), hexed(mini(3045, ltp=80600, close=0))),
    connected("a_tick_with_prices_and_no_symbol_is_not_partial", {"subscribe": [S]}, hexed(scrip(3045)), hexed(scrip(3045, high=0))),
    connected("a_tick_with_a_price_and_a_symbol_is_not_partial", {"subscribe": [S]}, {"text": ACK}, hexed(scrip(3045)), hexed(mini(3045, ltp=80600, close=0))),
    connected("an_ltp_subscriber_gets_nothing_from_a_depth_tick_with_no_price", {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045, level=8, ltp=0, buy=depth_rows(5), sell=depth_rows(5, 80550, -5)))),
    connected("a_depth_tick_with_no_price_publishes_the_cached_price_to_a_quote_subscriber", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045)), hexed(scrip(3045, level=8, ltp=0, buy=depth_rows(5), sell=depth_rows(5, 80550, -5)))),
    connected("a_depth_tick_with_no_price_publishes_the_cached_price_to_a_depth_subscriber", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045)), hexed(scrip(3045, level=8, ltp=0, buy=depth_rows(5), sell=depth_rows(5, 80550, -5)))),
    connected("depth_is_cached_only_with_a_depth_tick", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045, level=8, ltp=0, buy=depth_rows(5), sell=depth_rows(5, 80550, -5))), hexed(scrip(3045))),
    connected("a_level_two_tick_with_two_levels_each_side", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045, level=2, buy=depth_rows(2), sell=depth_rows(2, 80550, -5)))),
    connected("a_touch_tick_with_several_levels_keeps_one_each_side", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": ACK}, hexed(scrip(3045, level=4, buy=depth_rows(3), sell=depth_rows(3, 80550, -5)))),
    connected("a_subscribe_and_an_unsubscribe_of_one_scrip_in_one_batch", {"subscribe": [S]}, {"unsubscribe": S}),
    connected("an_index_unsubscribe_depth_and_quote_in_one_step", {"subscribe": [sub("NIFTY", "NSE_INDEX", 1), sub("NIFTY", "NSE_INDEX", 3)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 3)}),
    connected("the_ack_under_the_other_spelling_when_the_first_is_empty", {"subscribe": [S]}, {"text": {"message_code": 1109, "trading_symbols": {}, "tradingSymbols": {"nse_cm|3045": "SBIN-EQ"}}}, hexed(scrip(3045))),
    connected("a_connection_that_resubscribes_after_an_unsubscribe_of_a_state_holder", {"subscribe": [S]}, hexed(scrip(3045)), {"reconnect": {}}, {"text": AUTH}, hexed(mini(3045, ltp=80600, close=0))),
]
