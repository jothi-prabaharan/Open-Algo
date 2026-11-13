"""Delta Exchange feed: what the public market-data socket is sent to subscribe, and what the adapter publishes for the JSON frames it gets back."""

import json

BROKER = "deltaexchange"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "delta-api-key", "BROKER_API_SECRET": "delta-api-secret"}


def row(symbol, token, instrument="FUTURES", brsymbol=None):
    return {"symbol": symbol, "brsymbol": brsymbol or symbol, "name": symbol, "exchange": "CRYPTO", "brexchange": "CRYPTO", "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.5}


SYMBOLS = [
    row("BTCUSD", "27"),
    row("ETHUSD", "3136"),
    row("SOLPERP", "14823", brsymbol="SOLUSD"),
    row("BTC_INR", "90001", "SPOT"),
]


def ticker(symbol="BTCUSD", mark="63838.06", **extra):
    entry = {"s": symbol, "m": mark, "ohlc": [64025.0, 64475.5, 63217.5, 63839.0], "oi": ["1407713", "307138.71"], "q": ["63834", "718", "63833", "4388", None]}
    entry.update(extra)
    return {"type": "ticker", "sy": symbol, "sp": "63860.7", "ts": 1786521801671015, "d": [entry]}


def book(symbol="BTCUSD", levels=6):
    return {"type": "ob_l2", "sy": symbol, "ts": 1786521801671015,
            "a": [[str(63834 + i), str(700 + i)] for i in range(levels)], "b": [[str(63833 - i), str(4000 + i)] for i in range(levels)]}


class Driver:
    """Runs the real adapter and its real public socket client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.deltaexchange.streaming.delta_adapter import DeltaWebSocketAdapter
        from broker.deltaexchange.streaming.delta_websocket import DeltaWebSocket

        self.adapter_cls, self.ws_cls = DeltaWebSocketAdapter, DeltaWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls("delta-api-key", "delta-api-secret", url=self.ws_cls.PUBLIC_WS_URL, authenticate=False, name="public")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        ws.wsapp = FakeSocket()
        ws._connected = True
        ws.on_message = adapter._on_data
        adapter.public_ws, adapter.running, adapter.connected = ws, True, True
        # A batch is flushed by the step, not by a timer.
        self._hold = object()
        adapter.batch_timer = self._hold
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def _flush(self):
        self.adapter._process_batch()
        self.adapter.batch_timer = self._hold

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self._flush()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        self._flush()
        return self._take()

    def reconnect(self, _):
        self.ws._ws_on_open(self.ws.wsapp)
        return self._take()

    def text(self, message):
        self.ws._ws_on_message(self.ws.wsapp, message if isinstance(message, str) else json.dumps(message))
        return self._take()


def sub(symbol, mode, exchange="CRYPTO"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(message):
    return {"text": message}


SCENARIOS = [
    {"name": "subscribe_in_each_mode", "steps": [{"subscribe": [sub("BTCUSD", 1), sub("ETHUSD", 2), sub("SOLPERP", 3)]}]},
    {"name": "depth_subscribes_the_book_and_the_ticker", "steps": [{"subscribe": [sub("BTCUSD", 3)]}]},
    {"name": "the_book_costs_one_message_per_symbol", "steps": [{"subscribe": [sub("BTCUSD", 3), sub("ETHUSD", 3), sub("SOLPERP", 3)]}]},
    {"name": "two_modes_of_one_symbol_in_one_batch", "steps": [{"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 3)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, {"subscribe": [sub("BTCUSD", 3)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 1)]}]},
    {"name": "a_mode_the_exchange_does_not_have", "steps": [{"subscribe": [sub("BTCUSD", 4)]}, {"subscribe": [sub("BTCUSD", 0)]}]},
    {"name": "a_spot_pair", "steps": [{"subscribe": [sub("BTC_INR", 2)]}]},
    {"name": "unsubscribe_sends_a_remove", "steps": [{"subscribe": [sub("BTCUSD", 1), sub("ETHUSD", 2)]}, {"unsubscribe": sub("BTCUSD", 1)}, {"unsubscribe": sub("BTCUSD", 1)}]},
    {"name": "unsubscribe_depth_keeps_the_ticker_while_another_mode_needs_it", "steps": [
        {"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 3)]}, {"unsubscribe": sub("BTCUSD", 3)}]},
    {"name": "unsubscribe_ltp_keeps_everything_while_depth_remains", "steps": [
        {"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 3)]}, {"unsubscribe": sub("BTCUSD", 1)}]},
    {"name": "unsubscribe_depth_alone_removes_both_channels", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, {"unsubscribe": sub("BTCUSD", 3)}]},
    {"name": "unsubscribe_something_never_subscribed", "steps": [{"unsubscribe": sub("BTCUSD", 1)}]},
    {"name": "reconnect_repeats_every_subscription_sorted", "steps": [
        {"subscribe": [sub("SOLPERP", 1), sub("BTCUSD", 3), sub("ETHUSD", 2)]}, {"unsubscribe": sub("ETHUSD", 2)}, {"reconnect": None}]},
    {"name": "ticker_frame", "steps": [{"subscribe": [sub("BTCUSD", 2)]}, frame(ticker())]},
    {"name": "ticker_frame_for_ltp_mode", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame(ticker())]},
    {"name": "ticker_frame_without_a_mark_price_uses_the_spot_price", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame(ticker(m=None))]},
    {"name": "ticker_frame_with_no_price_at_all", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame({"type": "ticker", "sy": "BTCUSD", "d": [{"s": "BTCUSD"}]})]},
    {"name": "ticker_frame_with_an_empty_side_of_the_book", "steps": [{"subscribe": [sub("BTCUSD", 2)]}, frame(ticker(q=[None, None, "63833", "4388", None]))]},
    {"name": "ticker_frame_without_the_quote_array_keeps_the_last_quote", "steps": [
        {"subscribe": [sub("BTCUSD", 2)]}, frame(ticker()), frame(ticker(mark="63900.5", q=None))]},
    {"name": "ticker_frame_with_odd_numbers", "steps": [{"subscribe": [sub("BTCUSD", 2)]}, frame(ticker(m="abc", ohlc=["x", None, 1, 2], oi=["12.7"], q=["1.5", "2.9", "3", "4.2", None]))]},
    {"name": "ticker_frame_carrying_two_contracts", "steps": [
        {"subscribe": [sub("BTCUSD", 2), sub("ETHUSD", 2)]},
        frame({"type": "ticker", "sy": "BTCUSD", "d": [ticker()["d"][0], ticker("ETHUSD", "3100.5")["d"][0]]})]},
    {"name": "ticker_frame_names_the_contract_in_the_frame_only", "steps": [
        {"subscribe": [sub("BTCUSD", 1)]}, frame({"type": "ticker", "sy": "BTCUSD", "d": [{"m": "63000.5"}]})]},
    {"name": "ticker_frame_with_no_entries", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame({"type": "ticker", "sy": "BTCUSD", "d": None}), frame({"type": "ticker", "sy": "BTCUSD", "d": ["x"]})]},
    {"name": "two_modes_of_one_symbol_each_get_a_tick", "steps": [{"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 2)]}, frame(ticker())]},
    {"name": "book_frame_in_depth_mode", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, frame(book())]},
    {"name": "book_frame_with_fewer_than_five_levels", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, frame(book(levels=2))]},
    {"name": "book_frame_with_no_levels", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, frame({"type": "ob_l2", "sy": "BTCUSD"})]},
    {"name": "book_frame_with_odd_levels", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, frame({"type": "ob_l2", "sy": "BTCUSD", "a": [["1.9", "2.7"], ["bad"], "x", ["5", None]], "b": [[None, "3"]]})]},
    {"name": "depth_mode_merges_the_ticker_and_the_book", "steps": [{"subscribe": [sub("BTCUSD", 3)]}, frame(ticker()), frame(book())]},
    {"name": "book_frame_reaches_a_symbol_subscribed_in_ltp_mode_only", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame(book())]},
    {"name": "unsubscribing_one_mode_keeps_the_cached_values", "steps": [
        {"subscribe": [sub("BTCUSD", 1), sub("BTCUSD", 2)]}, frame(ticker()), {"unsubscribe": sub("BTCUSD", 1)},
        frame({"type": "ticker", "sy": "BTCUSD", "d": [{"s": "BTCUSD", "m": "64000.5"}]})]},
    {"name": "a_contract_whose_exchange_name_differs_from_the_broker_symbol", "steps": [
        {"subscribe": [sub("SOLPERP", 2)]}, frame(ticker("SOLUSD", "155.25")), frame(ticker("SOLPERP", "1.0"))]},
    {"name": "frame_for_a_symbol_nobody_subscribed", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, frame(ticker("ETHUSD", "3100.5"))]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [{"subscribe": [sub("BTCUSD", 1)]}, {"unsubscribe": sub("BTCUSD", 1)}, frame(ticker())]},
    {"name": "the_cache_is_forgotten_with_the_last_subscription", "steps": [
        {"subscribe": [sub("BTCUSD", 2)]}, frame(ticker()), {"unsubscribe": sub("BTCUSD", 2)}, {"subscribe": [sub("BTCUSD", 2)]}, frame({"type": "ticker", "sy": "BTCUSD", "d": [{"s": "BTCUSD", "m": "1.5"}]})]},
    {"name": "frames_that_carry_no_market_data", "steps": [
        {"subscribe": [sub("BTCUSD", 1)]}, frame("not json"), frame({"type": "subscriptions", "channels": []}), frame({"type": "heartbeat"}), frame({"type": "key-auth"}), frame({"type": "error", "message": "bad"})]},
]
