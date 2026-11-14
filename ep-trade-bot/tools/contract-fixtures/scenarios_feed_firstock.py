"""Firstock feed: JSON over a WebSocket. The server answers a connection with a status frame and nothing is sent before it says success; the adapter then subscribes a scrip (EXCHANGE:TOKEN) once however many modes want it, in one frame for the batch, grouped by exchange. A tick (top level, or wrapped under its EXCHANGE:TOKEN key) goes to every subscription on its scrip, whatever the mode, and a snapshot per scrip holds the last valid value of each field."""

import json
import types

NOW_MS = 1_793_700_000_123
BROKER = "firstock"
ENV = {"BROKER_API_KEY": "AB1234_API", "BROKER_API_SECRET": "fs-secret"}
INVALID = 9223372036854775808


def row(symbol, exchange, token, brexchange=None, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045), row("INFY", "NSE", 1594), row("TATAMOTORS", "BSE", 500570), row("NIFTY25NOV26FUT", "NFO", 49500, "NFO", "FUT"), row("SENSEX26NOVFUT", "BFO", 60001, "BFO", "FUT"),
    row("USDINR25NOV26FUT", "CDS", 3045, "CDS", "FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, "MCX", "FUT"), row("NIFTY", "NSE_INDEX", 26000, "NSE", "INDEX"),
]


class Driver:
    """Runs the real adapter and its real client with a fake socket. The batch timer is replaced by one explicit drain after each step, which is the timer firing."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.firstock.streaming import firstock_adapter
        from broker.firstock.streaming.firstock_websocket import FirstockWebSocket

        class Timer:
            def __init__(self, *a, **k):
                self.daemon = False

            def start(self):
                pass

            def cancel(self):
                pass

        real = firstock_adapter._real_threading
        firstock_adapter._real_threading = types.SimpleNamespace(Lock=real.Lock, Timer=Timer)
        self.adapter_cls, self.ws_cls = firstock_adapter.FirstockWebSocketAdapter, FirstockWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls("AB1234", "jkey")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        ws.wsapp, ws.connection_state, ws.is_running, ws.authenticated = FakeSocket(), ws.CONNECTED, True, False
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        ws.on_data = adapter._on_data
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _drain(self):
        self.adapter._process_subscription_batch()

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
        return self._take()

    def reconnect(self, _):
        self.ws.authenticated = False
        self.adapter._on_open(None)
        self._drain()
        return self._take()

    def text(self, text):
        self.ws._on_message(self.ws.wsapp, text if isinstance(text, str) else json.dumps(text))
        self._drain()
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def tick(token="3045", exchange="NSE", **extra):
    d = {"c_symbol": token, "c_exch_seg": exchange, "i_last_traded_price": 80545, "i_last_trade_time": 1793699999, "i_volume_traded_today": 1234567, "i_open_price": 80100, "i_high_price": 80950, "i_low_price": 79900,
         "i_closing_price": 80000, "i_last_trade_quantity": 25, "i_average_trade_price": 80410, "i_total_buy_quantity": 50000, "i_total_sell_quantity": 61000, "i_open_interest": 7000, "i_total_open_interest": 9000,
         "i_upper_circuit_limit": 88000, "i_lower_circuit_limit": 72000, "c_net_change_indicator": "+"}
    d.update(extra)
    return d


def book(buy=5, sell=5, **extra):
    d = {"best_buy": [{"price": 80540 - 5 * i, "quantity": 100 * (i + 1), "orders": i + 1} for i in range(buy)], "best_sell": [{"price": 80550 + 5 * i, "quantity": 110 * (i + 1), "orders": 2 * (i + 1)} for i in range(sell)]}
    d.update(extra)
    return d


def wrapped(key, **extra):
    d = tick(**extra)
    del d["c_symbol"], d["c_exch_seg"]
    return {key: d}


OK = {"text": {"status": "success", "message": "Authenticated"}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


def signed(name, *steps):
    """A scenario whose connection has already been accepted."""
    return scenario(name, OK, *steps)


S = sub("SBIN", "NSE", 2)
SCENARIOS = [
    # ---- the sign-in handshake ---------------------------------------------------------------------------------
    scenario("subscribe_before_the_sign_in_sends_nothing", {"subscribe": [S]}),
    scenario("the_sign_in_sends_what_was_waiting", {"subscribe": [S, sub("INFY", "NSE", 1)]}, OK),
    scenario("a_second_success_sends_nothing_more", {"subscribe": [S]}, OK, OK),
    scenario("an_unknown_status_is_ignored", {"subscribe": [S]}, {"text": {"status": "pending"}}, OK),
    scenario("a_message_with_no_status_is_ignored", {"subscribe": [S]}, {"text": {"message": "unauthenticated"}}, OK),
    scenario("a_failed_sign_in_sends_nothing_after", {"subscribe": [S]}, {"text": {"status": "failed", "message": "bad key"}}, {"subscribe": [sub("INFY", "NSE", 2)]}),
    scenario("an_unauthenticated_message_with_a_status_is_a_failed_sign_in", {"subscribe": [S]}, {"text": {"status": "error", "message": "unauthenticated"}}, OK),
    signed("an_unsubscribe_after_a_failed_sign_in_sends_nothing", {"subscribe": [S]}, {"text": {"status": "failed"}}, {"unsubscribe": S}),
    signed("a_failure_after_the_sign_in_stops_later_subscribes", {"subscribe": [S]}, {"text": {"status": "failed"}}, {"subscribe": [sub("INFY", "NSE", 2)]}),
    # ---- subscribing -------------------------------------------------------------------------------------------
    signed("subscribe_quote", {"subscribe": [S]}),
    signed("subscribe_ltp", {"subscribe": [sub("SBIN", "NSE", 1)]}),
    signed("subscribe_depth", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    signed("subscribe_several_is_one_frame_grouped_by_exchange", {"subscribe": [S, sub("NIFTY25NOV26FUT", "NFO", 2), sub("INFY", "NSE", 1), sub("TATAMOTORS", "BSE", 3), sub("GOLDM25NOV26FUT", "MCX", 2),
                                                                                  sub("USDINR25NOV26FUT", "CDS", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("NIFTY", "NSE_INDEX", 2)]}),
    signed("two_modes_of_one_symbol_in_one_step_are_sent_once", {"subscribe": [S, sub("SBIN", "NSE", 3)]}),
    signed("a_second_mode_on_a_scrip_already_held_sends_nothing", {"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 3)]}),
    signed("each_step_is_its_own_frame", {"subscribe": [S]}, {"subscribe": [sub("INFY", "NSE", 2)]}),
    signed("the_same_token_on_another_exchange_is_its_own_scrip", {"subscribe": [S]}, {"subscribe": [sub("USDINR25NOV26FUT", "CDS", 2)]}),
    scenario("scrips_waiting_for_the_sign_in_go_once_each", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"subscribe": [S]}, OK),
    # ---- unsubscribing -----------------------------------------------------------------------------------------
    signed("unsubscribe_quote", {"subscribe": [S]}, {"unsubscribe": S}),
    signed("unsubscribe_one_of_two_modes_sends_nothing", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": S}),
    signed("unsubscribe_the_last_mode_sends_the_frame", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": S}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    signed("unsubscribe_something_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    signed("unsubscribe_a_mode_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    signed("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    signed("unsubscribe_one_exchange_keeps_the_other", {"subscribe": [S, sub("USDINR25NOV26FUT", "CDS", 2)]}, {"unsubscribe": S}),
    scenario("unsubscribe_before_it_was_sent_sends_nothing", {"subscribe": [S]}, {"unsubscribe": S}, OK),
    scenario("unsubscribe_one_of_two_waiting_leaves_the_other", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, OK),
    signed("a_scrip_can_be_subscribed_again_after_an_unsubscribe", {"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}),
    # ---- reconnecting ------------------------------------------------------------------------------------------
    signed("reconnect_waits_for_the_sign_in", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"reconnect": {}}),
    signed("reconnect_then_the_sign_in_repeats_every_scrip_once", {"subscribe": [S, sub("SBIN", "NSE", 3), sub("INFY", "NSE", 1)]}, {"reconnect": {}}, OK),
    signed("reconnect_groups_by_exchange", {"subscribe": [S, sub("NIFTY25NOV26FUT", "NFO", 2), sub("INFY", "NSE", 2), sub("TATAMOTORS", "BSE", 2)]}, {"reconnect": {}}, OK),
    signed("reconnect_with_nothing_subscribed", {"reconnect": {}}, OK),
    signed("reconnect_after_an_unsubscribe", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}, OK),
    signed("reconnect_after_one_of_two_modes_is_unsubscribed", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": S}, {"reconnect": {}}, OK),
    signed("a_subscribe_between_the_reconnect_and_the_sign_in", {"subscribe": [S]}, {"reconnect": {}}, {"subscribe": [sub("INFY", "NSE", 2)]}, OK),
    signed("an_unsubscribe_between_the_reconnect_and_the_sign_in", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"reconnect": {}}, {"unsubscribe": S}, OK),
    signed("reconnect_keeps_the_held_values", {"subscribe": [S]}, {"text": tick()}, {"reconnect": {}}, OK, {"text": tick(i_last_traded_price=0)}),
    # ---- ticks -------------------------------------------------------------------------------------------------
    signed("a_quote_tick", {"subscribe": [S]}, {"text": tick()}),
    signed("an_ltp_tick", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick()}),
    signed("a_depth_tick", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}),
    signed("a_tick_reaches_every_mode_on_the_scrip", {"subscribe": [sub("SBIN", "NSE", 1), S, sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}),
    signed("a_float_price", {"subscribe": [S]}, {"text": tick(i_last_traded_price=80545.5, i_high_price=80950.25)}),
    signed("a_wrapped_tick", {"subscribe": [S]}, {"text": wrapped("NSE:3045")}),
    signed("a_wrapped_tick_for_a_depth_subscriber", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, {"text": wrapped("NFO:49500", **book())}),
    signed("a_wrapped_frame_with_two_scrips", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": {**wrapped("NSE:3045"), **wrapped("NSE:1594", i_last_traded_price=150050)}}),
    signed("a_wrapped_tick_that_names_its_own_token", {"subscribe": [S]}, {"text": {"NSE:9999": {"c_symbol": "3045", "c_exch_seg": "NSE", "i_last_traded_price": 81000}}}),
    signed("a_wrapped_value_that_is_not_an_object_is_ignored", {"subscribe": [S]}, {"text": {"NSE:3045": 5}}),
    signed("a_wrapped_key_with_no_colon_is_ignored", {"subscribe": [S]}, {"text": {"3045": {"i_last_traded_price": 80000}}}),
    signed("a_token_after_the_first_colon", {"subscribe": [S]}, {"text": wrapped("NSE:3045:x")}),
    signed("a_tick_for_a_token_nobody_asked_for", {"subscribe": [S]}, {"text": tick("9999")}),
    signed("a_tick_for_the_right_token_on_another_exchange", {"subscribe": [S]}, {"text": tick("3045", "BSE")}),
    signed("a_tick_whose_token_is_a_number", {"subscribe": [S]}, {"text": tick(3045)}),
    signed("a_tick_with_no_exchange", {"subscribe": [S]}, {"text": {"c_symbol": "3045", "i_last_traded_price": 80545}}),
    signed("a_tick_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"text": tick()}),
    signed("a_tick_goes_to_the_exchange_it_names", {"subscribe": [S, sub("USDINR25NOV26FUT", "CDS", 2)]}, {"text": tick("3045", "CDS")}),
    signed("an_order_update_is_ignored", {"subscribe": [S]}, {"text": {"norenordno": "250101000001", "status": "OPEN"}}),
    # ---- what a snapshot holds ---------------------------------------------------------------------------------
    signed("a_zero_price_holds_the_last_one", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_last_traded_price=0, i_open_price=0, i_high_price=0, i_low_price=0)}),
    signed("every_price_field_holds", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_last_traded_price=0, i_open_price=0, i_high_price=0, i_low_price=0, i_closing_price=0, i_average_trade_price=0, i_upper_circuit_limit=0, i_lower_circuit_limit=0)}),
    signed("a_missing_price_holds_the_last_one", {"subscribe": [S]}, {"text": tick()}, {"text": {"c_symbol": "3045", "c_exch_seg": "NSE", "i_volume_traded_today": 5}}),
    signed("the_invalid_marker_holds_a_price", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_last_traded_price=INVALID, i_high_price=INVALID)}),
    signed("the_invalid_marker_holds_a_quantity", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_volume_traded_today=INVALID, i_total_buy_quantity=INVALID, i_open_interest=INVALID)}),
    signed("the_invalid_marker_as_a_float_holds", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_last_traded_price=9.223372036854776e18)}),
    signed("a_zero_quantity_is_taken", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_volume_traded_today=0, i_last_trade_quantity=0, i_total_buy_quantity=0, i_open_interest=0, i_total_open_interest=0)}),
    signed("a_zero_price_with_nothing_before_it", {"subscribe": [S]}, {"text": tick(i_last_traded_price=0)}),
    signed("a_frame_with_no_fields_at_all", {"subscribe": [S]}, {"text": {"c_symbol": "3045", "c_exch_seg": "NSE"}}),
    signed("the_snapshot_is_shared_by_the_modes", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": tick()}, {"text": tick(i_last_traded_price=0)}),
    signed("the_snapshot_is_kept_per_scrip_not_per_token", {"subscribe": [S, sub("USDINR25NOV26FUT", "CDS", 2)]}, {"text": tick(i_last_traded_price=80545)}, {"text": tick("3045", "CDS", i_last_traded_price=9150)}, {"text": tick(i_last_traded_price=0)},
           {"text": tick("3045", "CDS", i_last_traded_price=0)}),
    signed("an_unsubscribe_forgets_the_snapshot", {"subscribe": [S]}, {"text": tick()}, {"unsubscribe": S}, {"subscribe": [S]}, {"text": tick(i_last_traded_price=0)}),
    signed("an_unsubscribe_of_one_mode_keeps_the_snapshot", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": tick()}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"text": tick(i_last_traded_price=0)}),
    signed("an_unsubscribe_of_another_exchange_keeps_the_snapshot", {"subscribe": [S, sub("USDINR25NOV26FUT", "CDS", 2)]}, {"text": tick()}, {"unsubscribe": sub("USDINR25NOV26FUT", "CDS", 2)}, {"text": tick(i_last_traded_price=0)}),
    signed("a_time_that_is_text_is_kept_as_text", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick(i_last_trade_time="12:30:45")}),
    signed("a_time_that_is_text_then_a_number", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick(i_last_trade_time="12:30:45")}, {"text": tick(i_last_trade_time=1793700000)}),
    signed("the_invalid_marker_holds_the_time", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick()}, {"text": tick(i_last_trade_time=INVALID)}),
    signed("no_time_at_all_is_zero", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": {"c_symbol": "3045", "c_exch_seg": "NSE", "i_last_traded_price": 80545}}),
    signed("a_price_that_is_text_is_read_as_a_number", {"subscribe": [S]}, {"text": tick(i_last_traded_price="80545")}),
    signed("a_price_that_is_not_a_number_publishes_nothing", {"subscribe": [S]}, {"text": tick(i_last_traded_price="n/a")}, {"text": tick()}),
    signed("a_null_price_publishes_nothing", {"subscribe": [S]}, {"text": tick(i_last_traded_price=None)}, {"text": tick()}),
    signed("a_price_that_is_true_is_one", {"subscribe": [S]}, {"text": tick(i_last_traded_price=True)}),
    signed("a_price_that_is_false_holds", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_last_traded_price=False)}),
    signed("a_quantity_that_is_false_is_taken", {"subscribe": [S]}, {"text": tick()}, {"text": tick(i_volume_traded_today=False)}),
    signed("a_bad_price_stops_the_modes_after_it", {"subscribe": [sub("SBIN", "NSE", 1), S, sub("SBIN", "NSE", 3)]}, {"text": tick(i_open_price="n/a")}),
    # ---- depth -------------------------------------------------------------------------------------------------
    signed("depth_with_fewer_than_five_levels", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book(2, 1))}),
    signed("depth_with_more_than_five_levels_is_cut", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book(8, 7))}),
    signed("a_level_with_no_price_and_no_quantity_is_dropped", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": 0, "quantity": 0, "orders": 0}, {"price": 80540, "quantity": 100, "orders": 1}], best_sell=[{"price": 80550, "quantity": 110, "orders": 2}, {"price": 0, "quantity": 0, "orders": 0}])}),
    signed("a_level_with_a_quantity_and_no_price_is_kept", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": 0, "quantity": 500, "orders": 3}], best_sell=[{"price": 80550, "quantity": 0, "orders": 0}])}),
    signed("a_level_with_a_price_and_no_quantity_is_kept", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": 80540, "quantity": 0, "orders": 0}], best_sell=[{"price": 80550, "quantity": 100, "orders": 1}])}),
    signed("a_negative_price_with_no_quantity_is_dropped", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": -5, "quantity": 0, "orders": 0}], best_sell=[{"price": 80550, "quantity": 100, "orders": 1}])}),
    signed("a_level_with_the_invalid_marker_is_dropped", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": INVALID, "quantity": 100, "orders": 1}, {"price": 80540, "quantity": INVALID, "orders": 1}, {"price": 80535, "quantity": 100, "orders": INVALID}, {"price": 80530, "quantity": 50, "orders": 1}],
                                                                                                                  best_sell=[{"price": 80550, "quantity": 100, "orders": 1}])}),
    signed("a_level_missing_its_fields", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[{"price": 80540}, {"quantity": 100}, {}], best_sell=[{"price": 80550, "quantity": 100}])}),
    signed("an_empty_book_after_a_full_one_is_held", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick(best_buy=[], best_sell=[])}),
    signed("a_book_of_zeros_after_a_full_one_is_held", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick(best_buy=[{"price": 0, "quantity": 0, "orders": 0}] * 5, best_sell=[{"price": 0, "quantity": 0, "orders": 0}] * 5)}),
    signed("an_empty_buy_side_holds_only_the_buy_side", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick(best_buy=[], best_sell=[{"price": 80560, "quantity": 7, "orders": 1}])}),
    signed("an_empty_sell_side_holds_only_the_sell_side", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick(best_buy=[{"price": 80500, "quantity": 7, "orders": 1}], best_sell=[])}),
    signed("an_empty_book_with_nothing_before_it", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[], best_sell=[])}),
    signed("a_tick_with_only_one_side_leaves_the_book_alone", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick(best_buy=[{"price": 80500, "quantity": 7, "orders": 1}])}),
    signed("a_tick_with_no_book_leaves_the_book_alone", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"text": tick()}),
    signed("a_tick_with_no_book_for_a_depth_subscriber_that_never_had_one", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick()}),
    signed("the_book_is_shared_by_the_modes", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}),
    signed("an_unsubscribe_forgets_the_book", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(**book())}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": tick(best_buy=[], best_sell=[])}),
    signed("depth_uses_the_total_open_interest", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"text": tick(i_open_interest=111, i_total_open_interest=222, **book())}),
    # ---- frames that are not ticks -----------------------------------------------------------------------------
    signed("a_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    signed("a_frame_that_is_a_json_string", {"subscribe": [S]}, {"text": '"hello"'}),
    signed("a_frame_that_is_a_number", {"subscribe": [S]}, {"text": 5}),
    signed("a_frame_that_is_a_list", {"subscribe": [S]}, {"text": [tick()]}),
    signed("an_empty_object", {"subscribe": [S]}, {"text": {}}),
]
