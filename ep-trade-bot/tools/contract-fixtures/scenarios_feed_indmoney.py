"""IndMoney feed: one socket address carrying the access token. Subscriptions are JSON frames {action, mode ltp|quote, instruments [SEGMENT:TOKEN]}; a burst is grouped by mode and segment (a frame that mixed segments delivered ticks for the first only),
at most 1000 in a frame and 3000 on a connection (a request that would pass it sends nothing). There is no depth stream: mode 3 and an exchange without a segment prefix are refused. Ticks are JSON, matched by the full instrument or a bare token that only one subscription holds;
a zero or missing field keeps the last value the symbol had. Every field is read as it comes, so a number sent as text is published as text."""

import json
import threading

NOW_MS = 1_793_700_000_123
BROKER = "indmoney"
AUTH_TOKEN = "ind-access-token"
ENV = {"BROKER_API_KEY": "ind-client-id"}


def row(symbol, exchange, token, brexchange, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045, "NSE"), row("INFY", "NSE", 1594, "NSE"), row("RELIANCE", "NSE", 2885, "NSE"), row("TATAMOTORS", "BSE", 500570, "BSE"), row("SBINBSE", "BSE", 3045, "BSE"),
    row("NIFTY25NOV26FUT", "NFO", 49500, "NSE", "FUT"), row("NIFTY25NOV2624000CE", "NFO", 49599, "NSE", "CE"), row("SENSEX26NOVFUT", "BFO", 60001, "BSE", "FUT"),
    row("USDINR25NOV26FUT", "CDS", 90001, "NSE", "FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, "MCX", "FUT"),
    row("NIFTY", "NSE_INDEX", 13, "NSE", "INDEX"), row("SENSEX", "BSE_INDEX", 51, "BSE", "INDEX"),
] + [row(f"T{i:04d}", "NSE", 100000 + i, "NSE") for i in range(3100)]


class Driver:
    """Runs the real adapter and its real client with a fake socket; the batch timer is fired by hand."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.indmoney.streaming.indmoney_adapter import IndmoneyWebSocketAdapter
        from broker.indmoney.streaming.indWebSocket import IndWebSocket

        self.adapter_cls, self.ws_cls = IndmoneyWebSocketAdapter, IndWebSocket
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

        self.fake = Fake

        adapter = self.adapter_cls()
        adapter.cleanup_zmq = lambda: None
        ws = self.ws_cls(AUTH_TOKEN)
        ws.wsapp = Fake()
        ws.on_open, ws.on_data, ws.on_error, ws.on_close, ws.on_message = adapter._on_open, adapter._on_data, adapter._on_error, adapter._on_close, adapter._on_message
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
        frames = [json.loads(text) for text in self.sent]
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
        if self.ws.wsapp is None:
            self.ws.wsapp = self.fake()
        self.ws._on_open(self.ws.wsapp)
        self._fire()
        return self._take()

    def text(self, text):
        self.ws._on_message(self.ws.wsapp, text if isinstance(text, str) else json.dumps(text))
        self._fire()
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def tick(instrument="NSE:3045", mode="quote", ts=1793699999000, **data):
    base = {"ltp": 805.45, "open": 801.0, "high": 809.5, "low": 799.0, "close": 800.0, "volume": 1234567, "bid_price": 805.4, "bid_qty": 100, "ask_price": 805.5, "ask_qty": 110, "average_price": 804.1, "oi": 0, "oi_change": 0}
    if mode == "ltp":
        base = {"ltp": 805.45}
    base.update(data)
    return {"mode": mode, "instrument": instrument, "timestamp": ts, "data": base}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


S, L = sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 1)
many = lambda n, mode=1, start=0: [sub(f"T{i:04d}", "NSE", mode) for i in range(start, start + n)]  # noqa: E731
SCENARIOS = [
    scenario("subscribe_quote", {"subscribe": [S]}),
    scenario("subscribe_ltp", {"subscribe": [L]}),
    scenario("subscribe_depth_is_refused", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    scenario("subscribe_an_invalid_mode_is_refused", {"subscribe": [sub("SBIN", "NSE", 4)]}),
    scenario("subscribe_every_segment_goes_in_a_frame_of_its_own", {"subscribe": [S, sub("TATAMOTORS", "BSE", 2), sub("NIFTY25NOV26FUT", "NFO", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 2)]}),
    scenario("subscribe_several_of_one_segment_is_one_frame", {"subscribe": [S, sub("INFY", "NSE", 2), sub("RELIANCE", "NSE", 2)]}),
    scenario("subscribe_modes_and_segments_group_apart", {"subscribe": [S, sub("TATAMOTORS", "BSE", 1), sub("INFY", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("RELIANCE", "NSE", 2)]}),
    scenario("subscribe_both_modes_of_one_symbol", {"subscribe": [S, L]}),
    scenario("subscribe_the_same_symbol_twice_in_a_burst", {"subscribe": [S, S]}),
    scenario("subscribe_the_same_symbol_again_later_sends_nothing", {"subscribe": [S]}, {"subscribe": [S]}),
    scenario("subscribe_one_after_another", {"subscribe": [S]}, {"subscribe": [sub("INFY", "NSE", 2)]}),
    scenario("subscribe_an_unsupported_exchange_is_refused", {"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 2), sub("USDINR25NOV26FUT", "CDS", 2)]}),
    scenario("subscribe_the_good_among_the_refused", {"subscribe": [sub("INFY", "NSE", 3), S, sub("GOLDM25NOV26FUT", "MCX", 2), sub("SBIN", "NSE", 3)]}),
    scenario("subscribe_the_same_token_on_two_segments", {"subscribe": [S, sub("SBINBSE", "BSE", 2)]}),
    scenario("subscribe_a_thousand_is_one_frame", {"subscribe": many(1000)}),
    scenario("subscribe_past_a_thousand_is_split", {"subscribe": many(1001)}),
    scenario("subscribe_exactly_the_limit", {"subscribe": many(3000)}),
    scenario("subscribe_past_the_limit_sends_nothing", {"subscribe": many(3001)}),
    scenario("subscribe_past_the_limit_in_a_second_step", {"subscribe": many(2000)}, {"subscribe": many(1001, 1, 2000)}),
    scenario("subscribe_the_limit_counts_both_modes", {"subscribe": many(2000)}, {"subscribe": many(1001, 2)}),
    scenario("subscribe_to_the_limit_in_two_steps", {"subscribe": many(2000)}, {"subscribe": many(1000, 1, 2000)}),
    scenario("subscribe_a_repeated_symbol_does_not_count_twice", {"subscribe": many(2000)}, {"subscribe": many(1000, 1, 1000)}),
    scenario("unsubscribe_quote_leaves_the_rest", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}),
    scenario("unsubscribe_the_last_symbol", {"subscribe": [S]}, {"unsubscribe": S}),
    scenario("unsubscribe_one_mode_of_two", {"subscribe": [S, L]}, {"unsubscribe": L}),
    scenario("unsubscribe_a_bse_symbol_and_an_index", {"subscribe": [sub("TATAMOTORS", "BSE", 2), sub("NIFTY", "NSE_INDEX", 2), S]}, {"unsubscribe": sub("TATAMOTORS", "BSE", 2)}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    scenario("unsubscribe_something_never_subscribed_still_sends", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    scenario("unsubscribe_a_depth_request_sends_ltp_and_keeps_the_symbol", {"subscribe": [S, L]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"reconnect": {}}),
    scenario("unsubscribe_an_unsupported_exchange", {"subscribe": [S]}, {"unsubscribe": sub("GOLDM25NOV26FUT", "MCX", 2)}),
    scenario("unsubscribe_frees_room_under_the_limit", {"subscribe": many(3000)}, {"unsubscribe": many(1)[0]}, {"subscribe": [sub("T3000", "NSE", 1)]}),
    scenario("reconnect_repeats_the_set", {"subscribe": [S, sub("INFY", "NSE", 2), sub("TATAMOTORS", "BSE", 1)]}, {"reconnect": {}}),
    scenario("reconnect_with_nothing_subscribed", {"reconnect": {}}),
    scenario("reconnect_repeats_both_modes_in_the_order_they_began", {"subscribe": [L]}, {"subscribe": [S]}, {"reconnect": {}}),
    scenario("reconnect_after_the_modes_are_asked_in_another_order", {"subscribe": [S]}, {"subscribe": [sub("INFY", "NSE", 1)]}, {"reconnect": {}}),
    scenario("reconnect_after_an_unsubscribe", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}),
    scenario("reconnect_splits_a_thousand_and_one", {"subscribe": many(1001)}, {"reconnect": {}}),
    scenario("reconnect_takes_what_the_limit_refused", {"subscribe": many(2000)}, {"subscribe": many(1001, 1, 2000)}, {"reconnect": {}}),
    scenario("reconnect_after_a_depth_unsubscribe_asks_for_ltp_twice", {"subscribe": [S, L]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"reconnect": {}}),
    scenario("reconnect_after_the_last_unsubscribe", {"subscribe": [S]}, {"unsubscribe": S}, {"reconnect": {}}),
    scenario("a_quote_tick_for_a_quote_subscriber", {"subscribe": [S]}, {"text": tick()}),
    scenario("an_ltp_tick_for_an_ltp_subscriber", {"subscribe": [L]}, {"text": tick(mode="ltp")}),
    scenario("a_quote_tick_for_an_ltp_subscriber_is_a_quote_topic", {"subscribe": [L]}, {"text": tick()}),
    scenario("an_ltp_tick_for_a_quote_subscriber_is_an_ltp_topic", {"subscribe": [S]}, {"text": tick(mode="ltp")}),
    scenario("a_tick_for_two_modes_carries_the_mode_of_the_first_subscription", {"subscribe": [L, S]}, {"text": tick()}, {"text": tick(mode="ltp")}),
    scenario("a_tick_for_two_modes_when_quote_came_first", {"subscribe": [S, L]}, {"text": tick(ltp=810)}),
    scenario("a_tick_after_the_first_mode_is_unsubscribed", {"subscribe": [L, S]}, {"unsubscribe": L}, {"text": tick()}),
    scenario("a_tick_for_a_bse_symbol", {"subscribe": [sub("TATAMOTORS", "BSE", 2)]}, {"text": tick("BSE:500570", ltp=500.5)}),
    scenario("a_tick_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1)]}, {"text": tick("NIDX:13", "ltp", ltp=24100.25)}, {"text": tick("BIDX:51", "ltp", ltp=80100.5)}),
    scenario("a_tick_for_a_future", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"text": tick("NFO:49500", ltp=24150, oi=120000, oi_change=-500)}),
    scenario("a_tick_matched_by_a_bare_token", {"subscribe": [sub("RELIANCE", "NSE", 2)]}, {"text": tick("2885", ltp=2900)}),
    scenario("a_bare_token_two_segments_hold_is_dropped", {"subscribe": [S, sub("SBINBSE", "BSE", 2)]}, {"text": tick("3045")}),
    scenario("a_full_instrument_is_told_from_the_same_token_elsewhere", {"subscribe": [S, sub("SBINBSE", "BSE", 2)]}, {"text": tick("BSE:3045", ltp=900)}),
    scenario("a_tick_for_an_instrument_nobody_asked_for", {"subscribe": [S]}, {"text": tick("NSE:9999")}),
    scenario("a_tick_for_the_wrong_segment", {"subscribe": [S]}, {"text": tick("BSE:3045")}),
    scenario("a_tick_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"text": tick()}),
    scenario("an_instrument_that_is_a_number_matches_nothing", {"subscribe": [S]}, {"text": tick(3045)}),
    scenario("a_missing_field_keeps_the_last_value", {"subscribe": [S]}, {"text": tick()}, {"text": {"mode": "quote", "instrument": "NSE:3045", "timestamp": 1793700000000, "data": {"ltp": 810}}}),
    scenario("a_zero_field_keeps_the_last_value", {"subscribe": [S]}, {"text": tick()}, {"text": tick(ltp=811, high=0, volume=0, bid_price=0.0)}),
    scenario("a_zero_field_with_nothing_before_is_zero", {"subscribe": [S]}, {"text": tick(ltp=0, high=0)}),
    scenario("a_field_that_is_null_is_published_as_null_and_remembered", {"subscribe": [S]}, {"text": tick(open=None)}, {"text": tick(open=0)}),
    scenario("a_field_that_is_text_is_published_as_text", {"subscribe": [S]}, {"text": tick(ltp="805.5", volume="1,234")}),
    scenario("a_field_that_is_the_text_zero_is_not_zero", {"subscribe": [S]}, {"text": tick()}, {"text": tick(ltp="0")}),
    scenario("a_field_that_is_false_counts_as_zero_and_true_does_not", {"subscribe": [S]}, {"text": tick()}, {"text": tick(ltp=False, high=True)}),
    scenario("the_remembered_values_are_shared_by_both_modes", {"subscribe": [L, S]}, {"text": tick()}, {"text": tick(mode="ltp", ltp=0)}),
    scenario("an_unsubscribe_forgets_the_remembered_values", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": tick()}, {"unsubscribe": S}, {"subscribe": [S]}, {"text": tick(ltp=0, high=0)}),
    scenario("an_unsubscribe_of_one_mode_forgets_the_values_of_the_other", {"subscribe": [S, L]}, {"text": tick()}, {"unsubscribe": L}, {"text": tick(ltp=0, high=0)}),
    scenario("an_ltp_tick_carries_only_the_price", {"subscribe": [L]}, {"text": tick(mode="ltp", open=1, high=2)}),
    scenario("a_tick_with_no_timestamp_uses_the_time_now", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "NSE:3045", "data": {"ltp": 805}}}),
    scenario("a_tick_whose_timestamp_is_null", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "NSE:3045", "timestamp": None, "data": {"ltp": 805}}}),
    scenario("a_tick_with_no_data_is_all_zeros", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "NSE:3045", "timestamp": 1793699999000}}),
    scenario("a_tick_whose_data_is_a_list_is_dropped", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "NSE:3045", "timestamp": 1793699999000, "data": [1]}}),
    scenario("a_tick_whose_data_is_null_is_dropped", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "NSE:3045", "timestamp": 1793699999000, "data": None}}),
    scenario("a_tick_of_another_mode_is_a_quote_topic_with_no_fields", {"subscribe": [S]}, {"text": {"mode": "depth", "instrument": "NSE:3045", "timestamp": 1793699999000, "data": {"ltp": 805}}}),
    scenario("a_tick_of_another_mode_whose_data_is_a_list", {"subscribe": [S]}, {"text": {"mode": "depth", "instrument": "NSE:3045", "timestamp": 1793699999000, "data": [1]}}),
    scenario("a_tick_with_no_mode_is_dropped", {"subscribe": [S]}, {"text": {"instrument": "NSE:3045", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_with_an_empty_mode_is_dropped", {"subscribe": [S]}, {"text": {"mode": "", "instrument": "NSE:3045", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_with_no_instrument_is_dropped", {"subscribe": [S]}, {"text": {"mode": "quote", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_with_an_empty_instrument_is_dropped", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": "", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_whose_mode_is_true_is_a_quote_topic_with_no_fields", {"subscribe": [S]}, {"text": {"mode": True, "instrument": "NSE:3045", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_whose_mode_is_a_number_is_a_quote_topic_with_no_fields", {"subscribe": [S]}, {"text": {"mode": 5, "instrument": "NSE:3045", "timestamp": 1, "data": {"ltp": 805}}}),
    scenario("a_tick_whose_mode_is_zero_or_false_is_dropped", {"subscribe": [S]}, {"text": {"mode": 0, "instrument": "NSE:3045", "timestamp": 1, "data": {}}}, {"text": {"mode": False, "instrument": "NSE:3045", "timestamp": 1, "data": {}}}),
    scenario("a_tick_whose_mode_is_an_empty_list_or_object_is_dropped", {"subscribe": [S]}, {"text": {"mode": [], "instrument": "NSE:3045", "timestamp": 1, "data": {}}}, {"text": {"mode": {}, "instrument": "NSE:3045", "timestamp": 1, "data": {}}}),
    scenario("a_tick_whose_mode_is_a_list_is_a_quote_topic", {"subscribe": [S]}, {"text": {"mode": ["x"], "instrument": "NSE:3045", "timestamp": 1, "data": {}}}),
    scenario("a_tick_whose_instrument_is_true_is_dropped", {"subscribe": [S]}, {"text": {"mode": "quote", "instrument": True, "timestamp": 1, "data": {}}}),
    scenario("a_pong_is_ignored", {"subscribe": [S]}, {"text": "pong"}),
    scenario("a_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    scenario("a_frame_that_is_a_list", {"subscribe": [S]}, {"text": [1, 2]}),
    scenario("a_frame_that_is_a_json_string", {"subscribe": [S]}, {"text": "\"NSE:3045\""}),
    scenario("a_frame_that_is_a_number", {"subscribe": [S]}, {"text": "5"}),
    scenario("a_frame_that_is_an_empty_object", {"subscribe": [S]}, {"text": {}}),
]
