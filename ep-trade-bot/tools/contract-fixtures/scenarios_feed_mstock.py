"""mStock feed: one socket address carrying the access token, signed in by the text LOGIN:<token> (nothing answers it, so the session is usable the moment it is sent). Subscriptions are JSON frames {action 1|0, params{mode, tokenList[{exchangeType, tokens}]}}.
The adapter keeps one subscription a token at the highest mode asked for; a higher mode drops the old one first and a lower one that is left after an unsubscribe is taken again. Frames are little-endian binary: a bare packet of 51 (last price), 123 (quote) or 379 bytes
(quote with five levels of depth), or a header (count, size) and packets. Every subscription on a token is published a tick, whatever the packet's own mode."""

import json
import struct
import threading

NOW_MS = 1_793_700_000_123
BROKER = "mstock"
AUTH_TOKEN = "mst-session-jwt"
ENV = {"BROKER_API_KEY": "mst-api-key", "BROKER_API_SECRET": "mst-api-secret"}


def row(symbol, brsymbol, exchange, token, instrument="EQ", brexchange=None):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN-EQ", "NSE", 3045), row("INFY", "INFY-EQ", "NSE", 1594), row("RELIANCE", "RELIANCE-EQ", "NSE", 2885), row("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570),
    row("SBINBSE", "SBIN", "BSE", 3045), row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "FUT"), row("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "FUT"),
    row("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "FUT"), row("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "FUT"),
    row("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", 19000, "INDEX"),
    row("CRUDEOIL25NOV26FUT", "CRUDEOIL25NOV26FUT", "NCO", 70001, "FUT"),
]


class Driver:
    """Runs the real adapter and its real client with a fake socket; the batch timer is fired by hand."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.mstock.api.mstockwebsocket import MstockWebSocket
        from broker.mstock.streaming.mstock_adapter import MstockWebSocketAdapter

        self.adapter_cls, self.ws_cls = MstockWebSocketAdapter, MstockWebSocket
        self.timers = []
        driver = self

        class Timer:
            def __init__(self, interval, function, *a, **k):
                self.function = function
                self.daemon = False

            def start(self):
                driver.timers.append(self.function)

            def cancel(self):
                pass

        threading.Timer = Timer

    def reset(self):
        self.sent, self.published, self.timers[:] = [], [], []
        sent = self.sent

        class Fake:
            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        adapter = self.adapter_cls()
        ws = self.ws_cls(AUTH_TOKEN)
        ws.ws = Fake()
        ws._connected = True
        ws._logged_in = True
        ws.running = True
        ws.data_callback = adapter._on_data
        ws.resync_callback = adapter._resync_subscriptions
        adapter.ws_client = ws
        adapter.running = True
        adapter.connected = True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _fire(self):
        timers, self.timers[:] = list(self.timers), []
        for function in timers:
            function()

    def _take(self, extra=None):
        frames = []
        for text in self.sent:
            try:
                frames.append(json.loads(text))
            except ValueError:
                frames.append(text)
        out = {"sent": frames, "published": list(self.published)}
        if extra:
            out.update(extra)
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        errors = []
        for item in items:
            result = self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
            if result.get("status") != "success":
                errors.append(result.get("error_code"))
        self._fire()
        return self._take({"errors": errors} if errors else None)

    def unsubscribe(self, item):
        result = self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take({"error": result.get("error_code")} if result.get("status") != "success" else None)

    def reconnect(self, _):
        self.ws._logged_in = False
        self.ws._on_ws_open(self.ws.ws)
        self._fire()
        return self._take()

    def frame(self, hex_text):
        self.ws._on_ws_message(None, bytes.fromhex(hex_text))
        self._fire()
        return self._take()

    def text(self, text):
        self.ws._on_ws_message(None, text)
        self._fire()
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def token_bytes(token):
    return token if isinstance(token, bytes) else str(token).encode().ljust(25, b"\x00")


def ltp_packet(token="3045", mode=1, exchange=1, ltp=80545, seq=7, stamp=1793699999):
    return bytes([mode, exchange]) + token_bytes(token) + struct.pack("<QQQ", seq, stamp, ltp)


def quote_packet(token="3045", mode=2, exchange=1, ltp=80545, ltq=25, avg=80410, volume=1234567, buy=45000.0, sell=52000.0, o=80100, h=80950, low=79900, close=80000):
    return ltp_packet(token, mode, exchange, ltp) + struct.pack("<QQQ", ltq, avg, volume) + struct.pack("<dd", buy, sell) + struct.pack("<QQQQ", o, h, low, close)


def level(qty, price, orders):
    return b"\x00\x00" + struct.pack("<QQH", qty, price, orders)


def snap_packet(token="3045", mode=3, exchange=1, bids=None, asks=None, ltt=1793699998, oi=9000, oi_pct=1234, upper=88000, lower=72000, high52=95000, low52=61000, **quote):
    bids = bids if bids is not None else [(100 * i, 80540 - 5 * (i - 1), i) for i in range(1, 6)]
    asks = asks if asks is not None else [(110 * i, 80550 + 5 * (i - 1), 2 * i) for i in range(1, 6)]
    depth = b"".join(level(*b) for b in bids).ljust(100, b"\x00") + b"".join(level(*a) for a in asks).ljust(100, b"\x00")
    return quote_packet(token, mode, exchange, **quote) + struct.pack("<QQQ", ltt, oi, oi_pct) + depth + struct.pack("<QQQQ", upper, lower, high52, low52)


def framed(packets, count=None, size=None):
    first = packets[0]
    return struct.pack("<HH", len(packets) if count is None else count, len(first) if size is None else size) + b"".join(packets)


def fr(data):
    return {"frame": data.hex()}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


L = sub("SBIN", "NSE", 1)
Q = sub("SBIN", "NSE", 2)
D = sub("SBIN", "NSE", 3)
IQ = sub("INFY", "NSE", 2)
ID = sub("INFY", "NSE", 3)
IL = sub("INFY", "NSE", 1)
RQ = sub("RELIANCE", "NSE", 2)
UD = {"symbol": "SBIN", "exchange": "NSE"}


def un(item):
    return {"unsubscribe": item}


SCENARIOS = [
    # ---- connecting ---------------------------------------------------------------------------------------------
    scenario("a_connection_sends_its_login_and_nothing_else", {"reconnect": {}}),
    scenario("a_connection_repeats_what_was_subscribed", {"subscribe": [Q, ID]}, {"reconnect": {}}),
    scenario("a_connection_repeats_each_token_at_its_highest_mode", {"subscribe": [L, Q, D, IQ]}, {"reconnect": {}}),
    scenario("a_connection_repeats_one_frame_a_mode", {"subscribe": [Q, IQ, RQ, sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX", "BSE_INDEX", 3)]}, {"reconnect": {}}),
    scenario("a_connection_repeats_nothing_after_everything_was_dropped", {"subscribe": [Q]}, un(Q), {"reconnect": {}}),
    scenario("a_connection_repeats_a_downgraded_token_at_the_lower_mode", {"subscribe": [Q, D]}, un(D), {"reconnect": {}}),
    scenario("a_second_connection_repeats_again", {"subscribe": [Q]}, {"reconnect": {}}, {"reconnect": {}}),
    scenario("after_a_connection_a_subscribe_sends_a_frame", {"subscribe": [Q]}, {"reconnect": {}}, {"subscribe": [IQ]}),
    scenario("after_a_connection_an_unsubscribe_sends_a_frame", {"subscribe": [Q, D]}, {"reconnect": {}}, un(D)),
    scenario("after_a_connection_an_upgrade_drops_the_old_subscription", {"subscribe": [Q]}, {"reconnect": {}}, {"subscribe": [D]}),
    # ---- subscribing --------------------------------------------------------------------------------------------
    scenario("subscribe_ltp", {"subscribe": [L]}),
    scenario("subscribe_quote", {"subscribe": [Q]}),
    scenario("subscribe_depth", {"subscribe": [D]}),
    scenario("subscribe_several_in_one_mode_is_one_frame", {"subscribe": [Q, IQ, RQ]}),
    scenario("subscribe_several_modes_is_one_frame_a_mode", {"subscribe": [L, IQ, RQ, sub("TATAMOTORS", "BSE", 3), sub("SENSEX", "BSE_INDEX", 1)]}),
    scenario("subscribe_every_exchange", {"subscribe": [Q, sub("TATAMOTORS", "BSE", 2), sub("NIFTY25NOV26FUT", "NFO", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2),
                                                          sub("GOLDM25NOV26FUT", "MCX", 2), sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 2)]}),
    scenario("subscribe_groups_tokens_by_exchange_in_the_order_met", {"subscribe": [Q, sub("NIFTY25NOV26FUT", "NFO", 2), IQ, sub("TATAMOTORS", "BSE", 2), RQ]}),
    scenario("subscribe_an_exchange_the_stream_does_not_map_goes_as_nse", {"subscribe": [sub("CRUDEOIL25NOV26FUT", "NCO", 2)]}),
    scenario("subscribe_the_same_token_on_two_exchanges", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}),
    scenario("subscribe_the_same_scrip_again_sends_nothing", {"subscribe": [Q]}, {"subscribe": [Q]}),
    scenario("subscribe_a_lower_mode_after_a_higher_one_sends_nothing", {"subscribe": [D]}, {"subscribe": [Q]}, {"subscribe": [L]}),
    scenario("subscribe_one_scrip_at_a_time", {"subscribe": [Q]}, {"subscribe": [IQ]}, {"subscribe": [RQ]}),
    scenario("subscribe_a_higher_mode_drops_the_old_subscription_first", {"subscribe": [Q]}, {"subscribe": [D]}),
    scenario("subscribe_a_higher_mode_from_ltp", {"subscribe": [L]}, {"subscribe": [Q]}, {"subscribe": [D]}),
    scenario("subscribe_two_modes_together_sends_the_higher_only", {"subscribe": [Q, D]}),
    scenario("subscribe_three_modes_together_sends_the_highest_only", {"subscribe": [L, Q, D]}),
    scenario("subscribe_two_modes_in_the_other_order_sends_the_highest_only", {"subscribe": [D, Q, L]}),
    scenario("subscribe_upgrades_in_one_step_for_two_scrips", {"subscribe": [Q, IQ, D, sub("INFY", "NSE", 1)]}),
    scenario("subscribe_an_upgrade_for_several_tokens_drops_them_in_one_frame", {"subscribe": [Q, IQ, RQ]}, {"subscribe": [D, ID]}),
    scenario("subscribe_an_upgrade_for_tokens_on_different_modes", {"subscribe": [L, IQ]}, {"subscribe": [Q, ID]}),
    # ---- unsubscribing ------------------------------------------------------------------------------------------
    scenario("unsubscribe_the_only_mode_of_a_token", {"subscribe": [Q]}, un(Q)),
    scenario("unsubscribe_one_of_two_tokens", {"subscribe": [Q, IQ]}, un(Q)),
    scenario("unsubscribe_the_higher_mode_takes_the_lower_again", {"subscribe": [Q, D]}, un(D)),
    scenario("unsubscribe_the_highest_of_three_takes_the_next", {"subscribe": [L, Q, D]}, un(D)),
    scenario("unsubscribe_the_middle_mode_changes_nothing", {"subscribe": [L, Q, D]}, un(Q)),
    scenario("unsubscribe_the_lower_mode_changes_nothing", {"subscribe": [Q, D]}, un(Q)),
    scenario("unsubscribe_every_mode_in_turn", {"subscribe": [L, Q, D]}, un(D), un(Q), un(L)),
    scenario("unsubscribe_a_scrip_that_was_never_subscribed", {"subscribe": [Q]}, un(IQ)),
    scenario("unsubscribe_a_mode_that_was_never_asked_for", {"subscribe": [Q]}, un(D)),
    scenario("unsubscribe_twice", {"subscribe": [Q]}, un(Q), un(Q)),
    scenario("unsubscribe_then_subscribe_again", {"subscribe": [Q]}, un(Q), {"subscribe": [Q]}),
    scenario("unsubscribe_a_downgrade_then_upgrade_again", {"subscribe": [Q, D]}, un(D), {"subscribe": [D]}),
    scenario("unsubscribe_one_of_two_exchanges_on_the_same_token", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}, un(Q)),
    scenario("unsubscribe_stops_the_ticks_of_that_scrip", {"subscribe": [Q, IQ]}, un(Q), {"frame": quote_packet("3045").hex()}, {"frame": quote_packet("1594").hex()}),
    scenario("unsubscribe_the_higher_mode_keeps_the_ticks_of_the_lower", {"subscribe": [Q, D]}, un(D), {"frame": quote_packet("3045").hex()}),
    # ---- data: one bare packet ----------------------------------------------------------------------------------
    scenario("a_last_price_packet_for_an_ltp_subscription", {"subscribe": [L]}, fr(ltp_packet())),
    scenario("a_last_price_packet_for_a_quote_subscription", {"subscribe": [Q]}, fr(ltp_packet())),
    scenario("a_last_price_packet_for_a_depth_subscription", {"subscribe": [D]}, fr(ltp_packet())),
    scenario("a_quote_packet_for_a_quote_subscription", {"subscribe": [Q]}, fr(quote_packet())),
    scenario("a_quote_packet_for_an_ltp_subscription_keeps_what_it_carries", {"subscribe": [L]}, fr(quote_packet())),
    scenario("a_quote_packet_for_a_depth_subscription_has_no_ladder", {"subscribe": [D]}, fr(quote_packet())),
    scenario("a_snapshot_packet_for_a_depth_subscription", {"subscribe": [D]}, fr(snap_packet())),
    scenario("a_snapshot_packet_for_a_quote_subscription", {"subscribe": [Q]}, fr(snap_packet())),
    scenario("a_snapshot_packet_for_an_ltp_subscription", {"subscribe": [L]}, fr(snap_packet())),
    scenario("a_snapshot_packet_with_a_short_ladder", {"subscribe": [D]}, fr(snap_packet(bids=[(100, 80540, 1)], asks=[]))),
    scenario("a_snapshot_packet_with_an_empty_ladder", {"subscribe": [D]}, fr(snap_packet(bids=[], asks=[]))),
    scenario("a_snapshot_packet_with_a_future_instrument", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, fr(snap_packet("49500", exchange=2, oi=7000000, ltp=2410025, avg=2409000))),
    scenario("a_snapshot_packet_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, fr(quote_packet("26000", ltp=2410025, volume=0, o=2400000, h=2420000, low=2395000, close=2399000))),
    scenario("a_packet_in_the_mode_it_was_not_asked_for_is_published_to_the_subscribers_of_the_token", {"subscribe": [L, Q, D]}, fr(quote_packet())),
    scenario("two_modes_on_one_token_are_each_published_to", {"subscribe": [L, D]}, fr(snap_packet())),
    scenario("the_same_token_on_two_exchanges_is_published_to_both", {"subscribe": [Q, sub("SBINBSE", "BSE", 2)]}, fr(quote_packet())),
    scenario("a_packet_for_a_token_nobody_asked_for", {"subscribe": [Q]}, fr(quote_packet("9999"))),
    scenario("a_packet_before_anything_is_subscribed", fr(quote_packet())),
    scenario("a_packet_with_no_token", {"subscribe": [Q]}, fr(quote_packet(b"\x00" * 25))),
    scenario("a_packet_whose_token_is_not_text", {"subscribe": [Q]}, fr(quote_packet(b"\xff\xfe" + b"\x00" * 23))),
    scenario("a_packet_whose_token_has_spaces_keeps_them", {"subscribe": [Q]}, fr(quote_packet(b" 3045".ljust(25, b"\x00")))),
    scenario("a_packet_whose_token_has_a_null_inside_keeps_the_rest", {"subscribe": [Q]}, fr(quote_packet(b"30\x0045".ljust(25, b"\x00")))),
    scenario("a_packet_with_every_value_zero", {"subscribe": [Q]}, fr(quote_packet(ltp=0, ltq=0, avg=0, volume=0, buy=0.0, sell=0.0, o=0, h=0, low=0, close=0))),
    scenario("a_packet_with_fractional_totals_cuts_them", {"subscribe": [Q]}, fr(quote_packet(buy=45000.9, sell=52000.5))),
    scenario("a_packet_with_negative_totals_cuts_toward_zero", {"subscribe": [Q]}, fr(quote_packet(buy=-45000.9, sell=-2.5))),
    scenario("a_packet_with_totals_that_are_not_numbers_publishes_the_price_only", {"subscribe": [Q]}, fr(quote_packet(buy=float("nan")))),
    scenario("a_packet_with_totals_that_are_infinite_publishes_the_price_only", {"subscribe": [Q]}, fr(quote_packet(sell=float("inf")))),
    scenario("a_packet_with_a_large_volume", {"subscribe": [Q]}, fr(quote_packet(volume=9_000_000_000_000))),
    scenario("a_packet_with_a_price_in_paise_not_dividing_evenly", {"subscribe": [Q]}, fr(quote_packet(ltp=80547, avg=80413, o=80101))),
    scenario("a_packet_whose_own_mode_is_zero", {"subscribe": [Q]}, fr(quote_packet(mode=0))),
    scenario("a_packet_whose_own_mode_is_above_three", {"subscribe": [D]}, fr(snap_packet(mode=4))),
    scenario("a_snapshot_packet_whose_own_mode_is_two_has_no_ladder", {"subscribe": [D]}, fr(snap_packet(mode=2))),
    scenario("a_packet_with_an_exchange_byte_nobody_knows", {"subscribe": [Q]}, fr(quote_packet(exchange=99))),
    # ---- data: framed packets -----------------------------------------------------------------------------------
    scenario("a_framed_last_price_packet", {"subscribe": [L]}, fr(framed([ltp_packet()]))),
    scenario("a_framed_quote_packet", {"subscribe": [Q]}, fr(framed([quote_packet()]))),
    scenario("a_framed_snapshot_packet", {"subscribe": [D]}, fr(framed([snap_packet()]))),
    scenario("a_frame_of_two_packets", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), quote_packet("1594", ltp=150010)]))),
    scenario("a_frame_of_three_snapshot_packets", {"subscribe": [D, ID, sub("RELIANCE", "NSE", 3)]}, fr(framed([snap_packet("3045"), snap_packet("1594", ltp=150010), snap_packet("2885", ltp=290055)]))),
    scenario("a_frame_of_two_packets_for_one_token", {"subscribe": [Q]}, fr(framed([quote_packet(ltp=80545), quote_packet(ltp=80600)]))),
    scenario("a_frame_claiming_more_packets_than_it_holds", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), quote_packet("1594")], count=5))),
    scenario("a_frame_claiming_fewer_packets_than_it_holds", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), quote_packet("1594")], count=1))),
    scenario("a_frame_claiming_no_packets", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), quote_packet("1594")], count=0))),
    scenario("a_frame_with_a_size_larger_than_the_data_is_one_packet", {"subscribe": [Q]}, fr(framed([quote_packet()], size=500))),
    scenario("a_frame_with_a_size_of_zero_is_one_packet", {"subscribe": [Q]}, fr(framed([quote_packet()], size=0))),
    scenario("a_frame_with_a_size_that_cuts_the_packet_gives_nothing", {"subscribe": [Q]}, fr(framed([quote_packet()], size=60))),
    scenario("a_frame_of_two_packets_with_a_size_of_zero_is_one_long_packet", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), quote_packet("1594")], size=0))),
    scenario("a_frame_with_only_a_header", {"subscribe": [Q]}, fr(struct.pack("<HH", 1, 123))),
    scenario("a_frame_shorter_than_a_header", {"subscribe": [Q]}, fr(b"\x01\x00\x7b")),
    scenario("an_empty_frame", {"subscribe": [Q]}, fr(b"")),
    scenario("a_frame_of_unknown_size", {"subscribe": [Q]}, fr(b"\x02" * 80)),
    scenario("a_frame_of_a_packet_and_a_bad_one", {"subscribe": [Q, IQ]}, fr(framed([quote_packet("3045"), b"\x01" * 123], count=2))),
    scenario("a_frame_with_a_packet_longer_than_a_snapshot_reads_its_first_bytes", {"subscribe": [D]}, fr(framed([b"\x00\x00\x00\x00" + snap_packet() + b"\x00" * 6], size=389))),
    scenario("a_frame_of_a_snapshot_packet_after_four_odd_bytes", {"subscribe": [D]}, fr(b"\x09\x09\x09\x09" + snap_packet())),
    scenario("a_frame_of_a_packet_between_sizes", {"subscribe": [Q]}, fr(quote_packet() + b"\x00" * 3)),
    # ---- text ---------------------------------------------------------------------------------------------------
    scenario("a_text_message_publishes_nothing", {"subscribe": [Q]}, {"text": "ok"}),
    scenario("a_text_message_that_looks_like_json_publishes_nothing", {"subscribe": [Q]}, {"text": "{\"status\":\"ok\"}"}),
    scenario("a_text_message_does_not_change_the_subscriptions", {"subscribe": [Q]}, {"text": "ok"}, un(Q)),
]
