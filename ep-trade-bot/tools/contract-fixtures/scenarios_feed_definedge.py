"""Definedge feed: a Noren-style JSON socket. A connection is signed in by a connect frame (user id, account id, TRTP and the user token) and answered with a ck frame; touchline and depth are separate subscriptions, scrips joined by "#". An instrument is on the wire once per kind however many modes want it. Ticks are merged over a cache per scrip (a zero open, high, low, close or average never replaces a held value) and published for the first subscription found on the scrip; a depth tick also publishes the quote and last price modes."""

import json
import threading

NOW_MS = 1_793_700_000_123
BROKER = "definedge"
AUTH_TOKEN = "dg-session-key:::dg-user-token:::dg-api-token"
ENV = {"BROKER_API_KEY": "dg-api-token"}
CLIENT_ID = "U1"


def row(symbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045), row("INFY", "NSE", 1594), row("TATAMOTORS", "BSE", 500570), row("NIFTY25NOV26FUT", "NFO", 49500, "FUT"), row("SENSEX26NOVFUT", "BFO", 60001, "FUT"),
    row("USDINR25NOV26FUT", "CDS", 90001, "FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, "FUT"), row("NIFTY", "NSE_INDEX", 26000, "INDEX"), row("SENSEX", "BSE_INDEX", 1, "INDEX"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i) for i in range(30)]


class Driver:
    """Runs the real adapter and its real client with a fake socket; the batch timer and the resubscribe timer are fired by hand."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.definedge.streaming.definedge_adapter import DefinedgeWebSocketAdapter
        from broker.definedge.streaming.definedge_websocket import DefinedGeWebSocket

        self.adapter_cls, self.client_cls = DefinedgeWebSocketAdapter, DefinedGeWebSocket
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
        client = self.client_cls({"auth_token": AUTH_TOKEN, "feed_token": "dg-user-token", "uid": "U1", "actid": "U1"})
        client.ws = Fake()
        client._start_heartbeat = lambda: None
        client.on_connect = adapter._on_open
        client.on_tick = adapter._on_data
        client.on_depth = adapter._on_depth_data
        adapter.ws_client = client
        adapter.running = True
        adapter._start_batch_timer = lambda: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.client = adapter, client

    def _drain(self):
        self.adapter._process_batch_subscriptions()

    def _fire(self):
        timers, self.timers[:] = list(self.timers), []
        for function in timers:
            function()

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
        self.client.authenticated = False
        self.client._on_open(None)
        return self._take()

    def text(self, text):
        self.client._on_message(None, text if isinstance(text, str) else json.dumps(text))
        if self.client.authenticated:
            self._fire()
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


CK = {"t": "ck", "s": "Ok", "uid": "U1"}


def tick(token="3045", exchange="NSE", kind="tf", **extra):
    d = {"t": kind, "e": exchange, "tk": str(token), "lp": "805.45", "o": "801.00", "h": "809.50", "l": "799.00", "c": "800.00", "v": "1234567", "ap": "804.10", "pc": "0.68", "ft": "1793699999",
         "oi": "7000", "poi": "6500", "toi": "9000", "bp1": "805.40", "bq1": "100", "sp1": "805.50", "sq1": "110"}
    d.update(extra)
    return d


def book(kind="df", token="3045", exchange="NSE", **extra):
    d = {"t": kind, "e": exchange, "tk": str(token), "lp": "805.45", "o": "801.00", "h": "809.50", "l": "799.00", "c": "800.00", "v": "1234567", "ap": "804.10", "pc": "0.68", "ltq": "25", "ltt": "10:20:00",
         "tbq": "50000", "tsq": "61000", "lc": "720.00", "uc": "880.00", "52h": "900", "52l": "600", "oi": "7000", "poi": "6500", "toi": "9000", "ft": "1793699999"}
    for i in range(1, 6):
        d[f"bp{i}"], d[f"bq{i}"], d[f"bo{i}"] = f"{805.4 - 0.05 * (i - 1):.2f}", str(100 * i), str(i)
        d[f"sp{i}"], d[f"sq{i}"], d[f"so{i}"] = f"{805.5 + 0.05 * (i - 1):.2f}", str(110 * i), str(2 * i)
    d.update(extra)
    return d


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


CONNECT = [{"reconnect": {}}, {"text": CK}]


def connected(name, *steps):
    return scenario(name, *CONNECT, *steps)


S = sub("SBIN", "NSE", 2)
SCENARIOS = [
    # ---- connecting ---------------------------------------------------------------------------------------------
    scenario("the_connection_sends_its_connect_frame", {"reconnect": {}}),
    scenario("a_refused_sign_in_leaves_the_session_unauthenticated", {"reconnect": {}}, {"text": {"t": "ck", "s": "Not_Ok", "emsg": "bad"}}, {"subscribe": [S]}),
    scenario("the_other_spelling_of_ok_is_accepted", {"reconnect": {}}, {"text": {"t": "ck", "s": "OK"}}, {"subscribe": [S]}),
    scenario("subscribe_before_the_sign_in_sends_nothing", {"reconnect": {}}, {"subscribe": [S]}),
    scenario("subscribe_before_connecting_is_asked_for_on_connect", {"subscribe": [S]}, *CONNECT),
    # ---- subscribing --------------------------------------------------------------------------------------------
    connected("subscribe_quote_is_a_touchline", {"subscribe": [S]}),
    connected("subscribe_ltp_is_a_touchline", {"subscribe": [sub("SBIN", "NSE", 1)]}),
    connected("subscribe_depth_is_a_depth", {"subscribe": [sub("SBIN", "NSE", 3)]}),
    connected("subscribe_several_is_one_frame_a_kind", {"subscribe": [S, sub("INFY", "NSE", 3), sub("NIFTY25NOV26FUT", "NFO", 2), sub("TATAMOTORS", "BSE", 1), sub("INFY", "NSE", 2)]}),
    connected("subscribe_every_exchange", {"subscribe": [sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2), sub("TATAMOTORS", "BSE", 2)]}),
    connected("subscribe_an_index_asks_the_cash_exchange", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 1)]}),
    connected("a_second_mode_of_a_kind_already_held_is_not_sent", {"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 1)]}),
    connected("a_depth_after_a_touchline_is_sent", {"subscribe": [S]}, {"subscribe": [sub("SBIN", "NSE", 3)]}),
    connected("the_same_scrip_twice_in_one_step_is_sent_once", {"subscribe": [S, sub("SBIN", "NSE", 1)]}),
    connected("thirty_in_one_frame", {"subscribe": [sub(f"TKN{i}", "NSE", 2) for i in range(30)]}),
    # ---- unsubscribing ------------------------------------------------------------------------------------------
    connected("unsubscribe_quote", {"subscribe": [S]}, {"unsubscribe": S}),
    connected("unsubscribe_depth", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    connected("unsubscribe_one_of_two_touchline_modes_sends_nothing", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"unsubscribe": sub("SBIN", "NSE", 1)}),
    connected("unsubscribe_the_last_touchline_mode", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"unsubscribe": S}),
    connected("unsubscribe_depth_keeps_the_touchline", {"subscribe": [S, sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}),
    connected("unsubscribe_something_never_subscribed", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    connected("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    connected("unsubscribe_then_subscribe_again", {"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}),
    scenario("unsubscribe_before_the_sign_in_sends_nothing", {"reconnect": {}}, {"unsubscribe": S}),
    # ---- reconnecting -------------------------------------------------------------------------------------------
    connected("reconnect_sends_the_connect_frame_and_waits", {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}),
    scenario("reconnect_asks_again_once_the_server_answers", *CONNECT, {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}, {"text": CK}),
    scenario("reconnect_asks_each_kind_once_per_scrip", *CONNECT, {"subscribe": [sub("SBIN", "NSE", 1), S, sub("SBIN", "NSE", 3)]}, {"reconnect": {}}, {"text": CK}),
    scenario("reconnect_drops_what_was_unsubscribed", *CONNECT, {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"unsubscribe": S}, {"reconnect": {}}, {"text": CK}),
    scenario("reconnect_with_nothing_subscribed", *CONNECT, {"reconnect": {}}, {"text": CK}),
    scenario("reconnect_keeps_the_cache", *CONNECT, {"subscribe": [S]}, {"text": tick()}, {"reconnect": {}}, {"text": CK}, {"text": tick(o="0", h="0")}),
    scenario("a_subscribe_after_a_reconnect_waits_for_the_sign_in", *CONNECT, {"subscribe": [S]}, {"reconnect": {}}, {"subscribe": [sub("INFY", "NSE", 3)]}, {"text": CK}),
    # ---- touchline ticks ----------------------------------------------------------------------------------------
    connected("a_quote_tick", {"subscribe": [S]}, {"text": tick()}),
    connected("an_ltp_tick", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick()}),
    connected("the_snapshot_that_answers_a_subscribe", {"subscribe": [S]}, {"text": tick(kind="tk")}),
    connected("a_snapshot_with_no_prices", {"subscribe": [S]}, {"text": tick(kind="tk", o="0", h="0", l="0", c="0")}),
    connected("a_zero_price_holds_the_last_one", {"subscribe": [S]}, {"text": tick()}, {"text": tick(o="0", h="", l="0.0", c=None, ap="0")}),
    connected("a_zero_last_price_is_taken", {"subscribe": [S]}, {"text": tick()}, {"text": tick(lp="0")}),
    connected("a_missing_field_keeps_the_cached_value", {"subscribe": [S]}, {"text": tick()}, {"text": {"t": "tf", "e": "NSE", "tk": "3045", "lp": "806"}}),
    connected("a_dash_is_zero", {"subscribe": [S]}, {"text": tick(lp="-", v="-", o="-")}),
    connected("text_that_is_not_a_number_is_zero", {"subscribe": [S]}, {"text": tick(v="n/a", o="x")}),
    connected("a_tick_with_a_last_price_that_is_not_a_number_for_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": tick(lp="-")}),
    connected("a_tick_without_a_last_price_for_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": {"t": "tf", "e": "NSE", "tk": "3045", "ft": "1"}}),
    connected("a_tick_without_a_time_for_an_ltp_subscriber", {"subscribe": [sub("SBIN", "NSE", 1)]}, {"text": {"t": "tf", "e": "NSE", "tk": "3045", "lp": "805"}}),
    connected("a_tick_for_a_scrip_nobody_asked_for", {"subscribe": [S]}, {"text": tick("9999")}),
    connected("the_same_token_on_another_exchange_is_not_the_scrip", {"subscribe": [S]}, {"text": tick("3045", "BSE")}),
    connected("a_tick_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"text": tick()}),
    connected("an_unsubscribe_forgets_the_cache", {"subscribe": [S]}, {"text": tick()}, {"unsubscribe": S}, {"subscribe": [S]}, {"text": tick(o="0")}),
    connected("an_unsubscribe_of_one_mode_keeps_the_cache", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": tick()}, {"unsubscribe": sub("SBIN", "NSE", 1)}, {"text": tick(o="0")}),
    connected("two_modes_on_one_scrip_publish_for_the_first_only", {"subscribe": [sub("SBIN", "NSE", 1), S]}, {"text": tick()}),
    connected("a_depth_subscriber_and_a_quote_subscriber_on_one_scrip", {"subscribe": [sub("SBIN", "NSE", 3), S]}, {"text": tick()}),
    connected("a_future_tick", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"text": tick("49500", "NFO", lp="24005.5")}),
    connected("an_index_tick", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"text": tick("26000", "NSE", lp="24100.25")}),
    connected("a_tick_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    connected("a_message_of_another_type_is_ignored", {"subscribe": [S]}, {"text": {"t": "xx", "e": "NSE", "tk": "3045"}}),
    connected("an_unsubscribe_acknowledgement_is_ignored", {"subscribe": [S]}, {"text": {"t": "uk", "e": "NSE", "tk": "3045"}}, {"text": {"t": "udk", "e": "NSE", "tk": "3045"}}),
    # ---- depth ticks --------------------------------------------------------------------------------------------
    connected("a_depth_tick", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": book()}),
    connected("a_depth_tick_also_publishes_the_quote_and_price_modes", {"subscribe": [sub("SBIN", "NSE", 3), S, sub("SBIN", "NSE", 1)]}, {"text": book()}),
    connected("a_depth_acknowledgement_with_prices", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": book(kind="dk")}),
    connected("a_depth_acknowledgement_with_zero_prices", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": book(kind="dk", o="0", h="0", l="0", c="0")}),
    connected("a_depth_tick_with_missing_levels", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"t": "df", "e": "NSE", "tk": "3045", "lp": "805", "bp1": "805", "bq1": "10", "sp1": "806"}}),
    connected("a_depth_tick_with_zero_prices_keeps_the_held_ones", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": book()}, {"text": book(o="0", h="0", l="0", c="0")}),
    connected("a_depth_tick_for_a_scrip_nobody_asked_for", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": book(token="9999")}),
    connected("a_depth_tick_after_an_unsubscribe_is_dropped", {"subscribe": [sub("SBIN", "NSE", 3), sub("INFY", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"text": book()}),
    connected("a_depth_acknowledgement_with_no_price_keys_is_ignored", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"t": "dk", "e": "NSE", "tk": "3045", "lp": "805.45"}}),
    connected("a_depth_acknowledgement_with_empty_prices_is_ignored", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"t": "dk", "e": "NSE", "tk": "3045", "lp": "805.45", "o": "", "h": 0, "l": 0.0, "c": None}}),
    connected("a_depth_acknowledgement_with_one_price_is_taken", {"subscribe": [sub("SBIN", "NSE", 3)]}, {"text": {"t": "dk", "e": "NSE", "tk": "3045", "lp": "805.45", "h": "810"}}),
    connected("a_zero_text_price_is_the_held_one_only_when_it_is_a_zero", {"subscribe": [S]}, {"text": tick()}, {"text": tick(o="0.00", h="0.0", l="0", c=0, ap=0.0)}),
    connected("numbers_may_arrive_as_numbers", {"subscribe": [S]}, {"text": {"t": "tf", "e": "NSE", "tk": "3045", "lp": 805.45, "v": 1500, "o": 801, "bq1": 12.9}}),
    connected("a_tick_without_a_token_is_dropped", {"subscribe": [S]}, {"text": {"t": "tf", "e": "NSE", "lp": "805"}}),
    connected("a_tick_that_is_a_list_is_dropped", {"subscribe": [S]}, {"text": [1, 2]}),
    connected("a_second_scrip_has_its_own_cache", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": tick()}, {"text": tick("1594", lp="1500", o="0")}),
    connected("a_depth_future_tick", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, {"text": book(token="49500", exchange="NFO")}),
]
