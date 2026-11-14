"""Samco feed: JSON over a WebSocket. Every subscribe frame restates the whole set (once per stream: quote2 for the depth ladder, quote for prices), so a burst of subscribes is one frame pair. A token has one mode on the connection, the last asked for."""

import json

NOW_MS = 1_793_700_000_123
BROKER = "samco"
AUTH_TOKEN = "samco-session-token"
ENV = {"BROKER_API_KEY": "samco-api-key", "BROKER_API_SECRET": "samco-api-secret"}


def row(symbol, exchange, token, brexchange, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": f"{token}_{brexchange}", "expiry": "", "strike": -1.0, "lotsize": 1,
            "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045, "NSE"), row("INFY", "NSE", 1594, "NSE"), row("TATAMOTORS", "BSE", 500570, "BSE"), row("NIFTY25NOV26FUT", "NFO", 49500, "NFO", "FUT"),
    row("NIFTY25NOV2624000CE", "NFO", 49599, "NFO", "CE"), row("SENSEX26NOVFUT", "BFO", 60001, "BFO", "FUT"), row("USDINR25NOV26FUT", "CDS", 90001, "CDS", "FUT"),
    row("GOLDM25NOV26FUT", "MCX", 80001, "MFO", "FUT"), row("NIFTY", "NSE_INDEX", 26000, "NSE", "INDEX"), row("SENSEX", "BSE_INDEX", 26001, "BSE", "INDEX"),
]
SYMBOLS[8]["token"] = "NIFTY_50"
SYMBOLS[9]["token"] = "SENSEX"
LISTING = {("NIFTY", "NSE_INDEX"): "-23", ("SENSEX", "BSE_INDEX"): "-5"}


class Driver:
    """Runs the real adapter and its real client with a fake socket; the subscription flusher thread is replaced by one explicit send per step."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.samco.streaming.samco_adapter import SamcoWebSocketAdapter
        from broker.samco.streaming.samcoWebSocket import SamcoWebSocket

        self.adapter_cls, self.ws_cls = SamcoWebSocketAdapter, SamcoWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(AUTH_TOKEN, "u")
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(text)

        ws.ws, ws.connected, ws.running = FakeSocket(), True, True
        ws.on_data = adapter._on_data
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        adapter._get_index_listing_id = lambda symbol, exchange: LISTING[(symbol, exchange)]
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": [json.loads(x) for x in self.sent], "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def _flush(self):
        """What the flusher thread does once a burst settles: send the whole set, but only when something marked it changed."""
        if self.ws._sub_dirty.is_set():
            self.ws._sub_dirty.clear()
            self.ws._send_subscription_state()

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
        self.ws._resubscribe_all()
        self.adapter._on_open(None)
        self._flush()
        return self._take()

    def text(self, text):
        self.ws._on_message(self.ws.ws, text if isinstance(text, str) else json.dumps(text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def quote(sym="3045_NSE", ltp="805.45", **extra):
    d = {"sym": sym, "ltp": ltp, "ltq": "25", "o": "801.00", "h": "809.50", "l": "799.00", "c": "800.00", "vol": "1,234,567", "avgPr": "804.10", "ch": "5.45", "chPer": "0.68", "bPr": "805.40", "bSz": "100",
         "aPr": "805.50", "aSz": "110", "tBQ": "50000", "tSQ": "61000", "oI": "0", "lTrdT": "03-Nov-2026 10:20:01", "streaming_type": "quote"}
    d.update(extra)
    return d


def depth(sym="3045_NSE", buy=5, sell=5, **extra):
    d = {"symbol": sym, "bidValues": [{"price": f"{805.4 - 0.05 * i:.2f}", "qty": str(100 * (i + 1)), "no": str(i + 1)} for i in range(buy)],
         "askValues": [{"price": f"{805.5 + 0.05 * i:.2f}", "qty": str(110 * (i + 1)), "no": str(2 * (i + 1))} for i in range(sell)], "tbq": "5000", "taq": "6100"}
    d.update(extra)
    return {"response": {"data": d, "streaming_type": extra.pop("_type", "quote2")}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


S = sub("SBIN", "NSE", 2)
SCENARIOS = [
    scenario("subscribe_quote", {"subscribe": [S]}),
    scenario("subscribe_ltp", {"subscribe": [sub("SBIN", "NSE", 1)]}),
    scenario("subscribe_depth_goes_on_both_streams", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    scenario("subscribe_several_is_one_frame_pair", {"subscribe": [S, sub("INFY", "NSE", 2), sub("TATAMOTORS", "BSE", 3), sub("NIFTY25NOV26FUT", "NFO", 1)]}),
    scenario("subscribe_every_exchange", {"subscribe": [sub("NIFTY25NOV2624000CE", "NFO", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2)]}),
    scenario("subscribe_an_index_uses_its_listing_id", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 3)]}),
    scenario("subscribe_one_after_another_restates_the_set", {"subscribe": [S]}, {"subscribe": [sub("INFY", "NSE", 3)]}),
    scenario("subscribe_the_same_token_in_two_modes_keeps_the_last", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [sub("SBIN", "NSE", 1)]}),
    scenario("subscribe_the_same_symbol_twice", {"subscribe": [S, S]}),
    scenario("unsubscribe_quote_leaves_the_rest", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}),
    scenario("unsubscribe_the_last_symbol", {"subscribe": [S]}, {"unsubscribe": S}),
    scenario("unsubscribe_depth_cancels_both_streams", {"subscribe": [sub("SBIN", "NSE", 3), sub("INFY", "NSE", 2)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    scenario("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), S]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    scenario("unsubscribe_something_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    scenario("unsubscribe_one_mode_of_two_forgets_the_token", {"subscribe": [sub("SBIN", "NSE", 3), sub("SBIN", "NSE", 2)]}, {"unsubscribe": sub("SBIN", "NSE", 2)}),
    scenario("reconnect_repeats_the_set", {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}),
    scenario("reconnect_with_nothing_subscribed", {"reconnect": {}}),
    scenario("reconnect_keeps_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": {}}),
    scenario("reconnect_after_two_modes_of_one_token", {"subscribe": [sub("SBIN", "NSE", 3), sub("SBIN", "NSE", 1)]}, {"reconnect": {}}),
    scenario("reconnect_after_the_modes_are_asked_in_another_order", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"subscribe": [sub("SBIN", "NSE", 1)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"reconnect": {}}),
    scenario("reconnect_after_a_depth_unsubscribe", {"subscribe": [sub("SBIN", "NSE", 3), sub("INFY", "NSE", 2)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"reconnect": {}}),
    scenario("reconnect_after_an_unsubscribe", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}),
    scenario("quote_frame_for_a_quote_subscriber", {"subscribe": [S]}, {"text": quote()}),
    scenario("quote_frame_for_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": quote()}),
    scenario("quote_frame_reaches_ltp_and_quote_subscribers", {"subscribe": [sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)]}, {"text": quote()}),
    scenario("quote_frame_is_not_for_a_depth_subscriber", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": quote()}),
    scenario("depth_frame_for_a_depth_subscriber", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth()}),
    scenario("depth_frame_is_not_for_a_quote_subscriber", {"subscribe": [S]}, {"text": depth()}),
    scenario("depth_frame_keeps_the_prices_the_quote_frame_brought", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": quote()}, {"text": depth()}),
    scenario("quote_frame_keeps_the_depth_totals_the_depth_frame_brought", {"subscribe": [sub("SBIN", "NSE", 3), sub("SBIN", "NSE", 2)]}, {"text": depth()}, {"text": quote()}),
    scenario("market_depth_type_is_depth", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"response": {"data": {"symbol": "3045_NSE", "bidValues": [{"price": "805.4", "qty": "100", "no": "1"}], "askValues": []}, "streaming_type": "marketDepth"}}}),
    scenario("fewer_than_five_levels", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth(buy=2, sell=1)}),
    scenario("more_than_five_levels", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth(buy=7, sell=7)}),
    scenario("depth_without_levels_has_no_ladder", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth(buy=0, sell=0)}),
    scenario("quote_best_prices_come_from_the_flat_fields", {"subscribe": [S]}, {"text": quote(bPr="900", bSz="9", aPr="901", aSz="8")}),
    scenario("depth_best_prices_come_from_the_first_level", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"text": depth()}),
    scenario("numbers_that_are_text_with_commas", {"subscribe": [S]}, {"text": quote(ltp="1,805.45", o="1,801", vol="12,34,567", oI="1,000")}),
    scenario("numbers_that_are_not_numbers_are_zero", {"subscribe": [S]}, {"text": quote(ltp="x", o=None, h="", l="--", vol="n/a")}),
    scenario("a_missing_field_keeps_the_last_value", {"subscribe": [S]}, {"text": quote()}, {"text": {"sym": "3045_NSE", "ltp": "810.00", "streaming_type": "quote"}}),
    scenario("an_empty_field_does_not_overwrite", {"subscribe": [S]}, {"text": quote()}, {"text": {"sym": "3045_NSE", "ltp": "", "o": None, "h": "811", "streaming_type": "quote"}}),
    scenario("totals_prefer_the_depth_names", {"subscribe": [S]}, {"text": quote(tbq="7", taq="8")}),
    scenario("totals_fall_back_to_the_quote_names", {"subscribe": [S]}, {"text": quote(tbq=0, taq=0)}),
    scenario("last_traded_time_names", {"subscribe": [S]}, {"text": quote(lTrdT="", ltt="10:21:00")}),
    scenario("a_frame_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"text": quote("-23", ltp="24100.25")}),
    scenario("a_frame_for_a_commodity_token", {"subscribe": [sub("GOLDM25NOV26FUT", "MCX", 2)]}, {"text": quote("80001_MFO", ltp="75000")}),
    scenario("a_frame_matched_by_the_script_code", {"subscribe": [S]}, {"text": quote("3045_BSE")}),
    scenario("a_frame_for_a_symbol_nobody_asked_for", {"subscribe": [S]}, {"text": quote("9999_NSE")}),
    scenario("a_frame_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"text": quote()}),
    scenario("an_unsubscribe_forgets_the_merged_prices", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": quote()}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": depth()}),
    scenario("a_frame_without_a_symbol", {"subscribe": [S]}, {"text": {"ltp": "805", "streaming_type": "quote"}}),
    scenario("an_empty_wrapper", {"subscribe": [S]}, {"text": {"response": {"data": {}, "streaming_type": "quote"}}}),
    scenario("a_wrapper_whose_data_is_not_an_object", {"subscribe": [S]}, {"text": {"response": {"data": [1], "streaming_type": "quote"}}}),
    scenario("a_frame_of_another_type", {"subscribe": [S]}, {"text": {"sym": "3045_NSE", "ltp": "805", "streaming_type": "heartbeat"}}),
    scenario("a_frame_with_no_type", {"subscribe": [S]}, {"text": {"sym": "3045_NSE", "ltp": "805"}}),
    scenario("a_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    scenario("a_frame_that_is_a_list", {"subscribe": [S]}, {"text": [1, 2]}),
]
