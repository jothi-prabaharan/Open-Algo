"""5paisa feed: JSON over a WebSocket. Scrips go in frames per method (MarketFeedV3 for prices, MarketDepthService for the ladder), fifty a frame; a depth subscriber is on both methods, and a method another subscription on the token already holds is not sent again. A tick goes to every subscription on its token, each holding the last non-zero value of a price field."""

import json

NOW_MS = 1_793_700_000_123
BROKER = "fivepaisa"
AUTH_TOKEN = "fp-access-token"
ENV = {"BROKER_API_KEY": "fp-app-key:::fp-user:::4001234", "BROKER_API_SECRET": "fp-secret"}


def row(symbol, exchange, token, brexchange=None, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045), row("INFY", "NSE", 1594), row("TATAMOTORS", "BSE", 500570), row("NIFTY25NOV26FUT", "NFO", 49500, "NFO", "FUT"), row("SENSEX26NOVFUT", "BFO", 60001, "BFO", "FUT"),
    row("USDINR25NOV26FUT", "CDS", 90001, "CDS", "FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, "MCX", "FUT"), row("NIFTY", "NSE_INDEX", 999920000, "NSE_INDEX", "INDEX"), row("SENSEX", "BSE_INDEX", 999901, "BSE_INDEX", "INDEX"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i) for i in range(60)]


class Driver:
    """Runs the real adapter and its real client with a fake socket; the batching thread is replaced by one explicit drain per step."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.fivepaisa.streaming.fivepaisa_adapter import FivepaisaWebSocketAdapter
        from broker.fivepaisa.streaming.fivepaisa_websocket import FivePaisaWebSocket

        self.adapter_cls, self.ws_cls = FivepaisaWebSocketAdapter, FivePaisaWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(AUTH_TOKEN, "4001234")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(text)

        class Alive:
            def is_alive(self):
                return True

        ws.wsapp, ws.connected = FakeSocket(), True
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        ws.on_data = adapter._on_data
        adapter._stop_event.wait = lambda timeout=None: False
        adapter._sub_thread = Alive()
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws, self.alive = adapter, ws, Alive

    def _drain(self):
        self.adapter._process_pending_subscriptions()
        self.adapter._sub_thread = self.alive()

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
        self.adapter._on_open(None)
        self._drain()
        return self._take()

    def text(self, text):
        self.ws._on_message(self.ws.wsapp, text if isinstance(text, str) else json.dumps(text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def feed(token=3045, **extra):
    d = {"Exch": "N", "ExchType": "C", "Token": token, "LastRate": 805.45, "LastQty": 25, "TotalQty": 1234567, "High": 809.5, "Low": 799.0, "OpenRate": 801.0, "PClose": 800.0, "AvgRate": 804.1,
         "BidRate": 805.4, "BidQty": 100, "OffRate": 805.5, "OffQty": 110, "TBidQ": 50000, "TOffQ": 61000, "TickDt": "/Date(1793699999000)/", "Time": 1793699999}
    d.update(extra)
    return d


def depth(token=3045, buy=5, sell=5, **extra):
    details = [{"BbBuySellFlag": 66, "Price": round(805.4 - 0.05 * i, 2), "Quantity": 100 * (i + 1), "NumberOfOrders": i + 1} for i in range(buy)]
    details += [{"BbBuySellFlag": 83, "Price": round(805.5 + 0.05 * i, 2), "Quantity": 110 * (i + 1), "NumberOfOrders": 2 * (i + 1)} for i in range(sell)]
    d = {"Exch": "N", "ExchType": "C", "Token": token, "TBidQ": 5000, "TOffQ": 6100, "Details": details, "Time": "/Date(1793699999000)/"}
    d.update(extra)
    return d


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


S = sub("SBIN", "NSE", 2)
SCENARIOS = [
    scenario("subscribe_quote", {"subscribe": [S]}),
    scenario("subscribe_ltp", {"subscribe": [sub("SBIN", "NSE", 1)]}),
    scenario("subscribe_depth_goes_on_both_methods", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    scenario("subscribe_several_is_one_frame_a_method", {"subscribe": [S, sub("INFY", "NSE", 1), sub("TATAMOTORS", "BSE", 3), sub("NIFTY25NOV26FUT", "NFO", 2)]}),
    scenario("subscribe_every_exchange", {"subscribe": [sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2), sub("TATAMOTORS", "BSE", 2)]}),
    scenario("subscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 1)]}),
    scenario("subscribe_more_than_fifty_goes_in_several_frames", {"subscribe": [sub(f"TKN{i}", "NSE", 2) for i in range(60)]}),
    scenario("subscribe_a_method_already_held_is_not_sent_again", {"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 3)]}),
    scenario("subscribe_the_same_symbol_twice", {"subscribe": [S, S]}),
    scenario("subscribe_quote_after_depth_sends_nothing", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [S]}),
    scenario("unsubscribe_quote", {"subscribe": [S]}, {"unsubscribe": S}),
    scenario("unsubscribe_depth_sends_both_methods", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    scenario("unsubscribe_depth_keeps_the_price_method_another_mode_needs", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    scenario("unsubscribe_quote_keeps_the_method_depth_needs", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"unsubscribe": S}),
    scenario("unsubscribe_something_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    scenario("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    scenario("reconnect_repeats_every_method_once", {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}),
    scenario("reconnect_with_two_modes_on_one_token", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"reconnect": {}}),
    scenario("reconnect_with_nothing_subscribed", {"reconnect": {}}),
    scenario("reconnect_after_an_unsubscribe", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}),
    scenario("reconnect_in_several_frames", {"subscribe": [sub(f"TKN{i}", "NSE", 3) for i in range(30)]}, {"reconnect": {}}),
    scenario("a_feed_frame_for_a_quote_subscriber", {"subscribe": [S]}, {"text": feed()}),
    scenario("a_feed_frame_for_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": feed()}),
    scenario("a_feed_frame_reaches_every_mode_on_the_token", {"subscribe": [sub("SBIN", "NSE", 1), S, sub("SBIN", "NSE", 3)]}, {"text": feed()}),
    scenario("a_depth_frame_for_a_depth_subscriber", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth()}),
    scenario("a_depth_frame_reaches_a_quote_subscriber_too", {"subscribe": [S]}, {"text": depth()}),
    scenario("a_depth_frame_keeps_the_last_price_a_feed_frame_brought", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": feed()}, {"text": depth()}),
    scenario("fewer_than_five_levels", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth(buy=2, sell=1)}),
    scenario("more_than_five_levels_are_cut", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth(buy=8, sell=8)}),
    scenario("a_depth_frame_with_no_details", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"Token": 3045, "TBidQ": 1, "TOffQ": 2}}),
    scenario("a_zero_price_holds_the_last_one", {"subscribe": [S]}, {"text": feed()}, {"text": feed(LastRate=0, High=0, BidRate=0)}),
    scenario("a_missing_price_holds_the_last_one", {"subscribe": [S]}, {"text": feed()}, {"text": {"Token": 3045, "TotalQty": 5}}),
    scenario("a_null_price_holds_the_last_one", {"subscribe": [S]}, {"text": feed()}, {"text": feed(LastRate=None, OpenRate=None)}),
    scenario("a_zero_price_with_nothing_before_it", {"subscribe": [S]}, {"text": feed(LastRate=0)}),
    scenario("the_held_price_is_kept_per_mode", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": feed()}, {"text": feed(LastRate=0)}),
    scenario("an_unsubscribe_forgets_the_held_price", {"subscribe": [S]}, {"text": feed()}, {"unsubscribe": S}, {"subscribe": [S]}, {"text": feed(LastRate=0)}),
    scenario("an_unsubscribe_of_one_mode_keeps_the_other_modes_held_price", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": feed()}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"text": feed(LastRate=0)}),
    scenario("a_resubscribed_mode_starts_with_nothing_held", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": feed()}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": feed(LastRate=0)}),
    scenario("the_time_is_read_from_the_date_text", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": feed(TickDt="/Date(1793699888000+0530)/")}),
    scenario("a_time_that_is_not_a_date_is_zero", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": feed(TickDt="yesterday")}),
    scenario("no_time_is_zero", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": {"Token": 3045, "LastRate": 805.0}}),
    scenario("a_frame_that_is_a_list_of_frames", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": [feed(), feed(1594, LastRate=1500.5)]}),
    scenario("a_frame_for_a_token_nobody_asked_for", {"subscribe": [S]}, {"text": feed(9999)}),
    scenario("a_frame_with_no_token", {"subscribe": [S]}, {"text": {"LastRate": 805.0}}),
    scenario("a_token_that_is_text", {"subscribe": [S]}, {"text": feed("3045")}),
    scenario("a_frame_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"text": feed()}),
    scenario("a_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    scenario("a_frame_that_is_a_number", {"subscribe": [S]}, {"text": 5}),
]
