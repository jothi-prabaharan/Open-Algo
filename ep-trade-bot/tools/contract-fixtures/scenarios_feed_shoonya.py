"""Shoonya feed (Noren websocket): what the socket is sent to sign in and subscribe, and what the adapter publishes for the JSON frames it gets back."""

import json

BROKER = "shoonya"
NOW_MS = 1_793_700_000_123
TOKEN = "shoonya-session-token"
ENV = {"BROKER_API_KEY": "FA12345:::shoonya-api-key", "BROKER_API_SECRET": "shoonya-secret"}


def row(symbol, token, exchange="NSE", brexchange=None, brsymbol=None, instrument="EQ", name=None):
    return {"symbol": symbol, "brsymbol": brsymbol or symbol + "-EQ", "name": name or symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "3045"),
    row("INFY", "1594"),
    row("RELIANCE", "2885"),
    row("TCS", "11536"),
    row("NIFTY", "26000", "NSE_INDEX", "NSE_INDEX", "Nifty 50", "INDEX"),
    row("SENSEX", "1", "BSE_INDEX", "BSE_INDEX", "SENSEX", "INDEX"),
    row("NIFTY25NOV26FUT", "35001", "NFO", "NFO", "NIFTY25NOV26F", "FUTIDX"),
    row("CRUDEOIL", "50001", "MCX", "MCX", "CRUDEOIL", "FUTCOM"),
    # The same token under another exchange: a feed frame without an exchange cannot be told apart.
    row("SBIN", "3045", "BSE", "BSE", "SBIN"),
    row("ODD", "9", "NCO", "NCO", "ODD", "FUTCOM"),
    # A second name for one instrument: both share its scrip.
    row("SBIN2", "3045", brsymbol="SBIN-EQ"),
] + [row(f"S{i:03d}", str(5000 + i)) for i in range(101)]


def touch(token="3045", exchange="NSE", t="tf", **extra):
    d = {"t": t, "e": exchange, "tk": token, "ft": "1793700000", "lp": "805.45", "o": "802.45", "h": "809.45", "l": "800.45", "c": "803.95", "ap": "805.1", "v": "1234567", "pc": "0.19",
         "ltq": "25", "ltt": "10:20:30", "tbq": "5000", "tsq": "4000", "bp1": "805.4", "sp1": "805.5"}
    d.update(extra)
    return {k: v for k, v in d.items() if v is not None}


def depth(token="3045", exchange="NSE", t="df", **extra):
    d = {"t": t, "e": exchange, "tk": token, "ft": "1793700001", "lp": "805.45", "o": "802.45", "h": "809.45", "l": "800.45", "c": "803.95", "ap": "805.1", "v": "1234567", "pc": "0.19",
         "ltq": "25", "ltt": "10:20:30", "tbq": "5000", "tsq": "4000", "uc": "884.0", "lc": "723.9", "52h": "912.0", "52l": "600.5", "toi": "1500"}
    for i in range(1, 6):
        d[f"bp{i}"] = str(round(805.4 - 0.05 * (i - 1), 2))
        d[f"bq{i}"] = str(100 * i)
        d[f"bo{i}"] = str(i)
        d[f"sp{i}"] = str(round(805.5 + 0.05 * (i - 1), 2))
        d[f"sq{i}"] = str(110 * i)
        d[f"so{i}"] = str(2 * i)
    d.update(extra)
    return {k: v for k, v in d.items() if v is not None}


class Driver:
    """Runs the real adapter and its real socket client with a fake socket underneath."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        import broker.shoonya.streaming.shoonya_adapter as adapter_module
        from broker.shoonya.streaming.shoonya_adapter import ShoonyaWebSocketAdapter
        from broker.shoonya.streaming.shoonya_websocket import ShoonyaWebSocket

        self.module, self.adapter_cls, self.ws_cls = adapter_module, ShoonyaWebSocketAdapter, ShoonyaWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

        adapter.actid, adapter.susertoken = "FA12345", TOKEN
        ws = self.ws_cls(user_id="FA12345", actid="FA12345", susertoken=TOKEN, on_message=adapter._on_message, on_error=adapter._on_error, on_close=adapter._on_close, on_open=adapter._on_open)
        ws.SUBSCRIPTION_DELAY = 0
        ws.ws = FakeSocket()
        ws.running = ws.connected = True
        ws._start_heartbeat = lambda: None
        # The queue is drained on the spot, not by a worker thread.
        original = ws._enqueue_subscriptions

        def inline(msg_type, scrips):
            with ws._subscription_lock:
                for scrip in scrips:
                    if scrip:
                        ws._pending_subscriptions.append((msg_type, scrip))
            ws._process_pending_subscriptions()

        ws._enqueue_subscriptions = inline
        adapter.ws_client = ws
        adapter.running = adapter.connected = True
        # A burst of subscribes waits for one flush, which the step makes; no timer runs.
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})

        class NoTimer:
            def __init__(self, *a, **k):
                pass

            daemon = True

            def start(self):
                pass

            def cancel(self):
                pass

        self.module.threading = type("T", (), {**{k: getattr(self.module.threading, k) for k in dir(self.module.threading) if not k.startswith("__")}, "Timer": NoTimer})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        import time

        self.adapter._last_sub_flush_at = time.time()
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.adapter._flush_subscription_batch()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def signed_out(self, _):
        self.adapter.connected = self.ws.connected = False
        return self._take()

    def reconnect(self, _):
        self.adapter.connected = self.ws.connected = False
        self.ws._on_open(self.ws.ws)
        return self._take()

    def text(self, message):
        text = message if isinstance(message, str) else json.dumps(message)
        was = self.ws.connected
        self.ws._on_message(self.ws.ws, text)
        thread = self.adapter._resub_thread
        if thread is not None:
            thread.join(timeout=5)
            self.adapter._resub_thread = None
        return self._take()


def sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(message):
    return {"text": message}


AK = {"t": "ak", "s": "OK", "uid": "FA12345"}

MANY = [sub(f"S{i:03d}", 2) for i in range(101)]
HUNDRED = [sub(f"S{i:03d}", 3) for i in range(100)]
SCENARIOS = [
    {"name": "a_hundred_and_one_scrips_go_in_two_messages", "steps": [{"subscribe": MANY}]},
    {"name": "a_hundred_scrips_go_in_one_message", "steps": [{"subscribe": HUNDRED}]},
    {"name": "a_hundred_and_one_depth_scrips_go_in_two_messages", "steps": [{"subscribe": [sub(f"S{i:03d}", 3) for i in range(101)]}]},
    {"name": "one_name_for_two_symbols_shares_one_request", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("SBIN2", 2)]}, {"unsubscribe": sub("SBIN", 2)}, {"unsubscribe": sub("SBIN2", 2)}]},
    {"name": "one_name_for_two_symbols_depth", "steps": [{"subscribe": [sub("SBIN", 3)]}, {"subscribe": [sub("SBIN2", 3)]}, {"unsubscribe": sub("SBIN", 3)}, {"unsubscribe": sub("SBIN2", 3)}]},
    {"name": "one_name_for_two_symbols_in_one_batch", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN2", 2)]}, frame(touch())]},
    {"name": "ltp_then_quote_in_separate_steps_sends_once", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 2)]}]},
    {"name": "quote_then_depth_then_quote_of_another_name", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("SBIN", 3)]}, {"subscribe": [sub("SBIN2", 3)]}]},
    {"name": "subscribe_while_signed_out_sends_nothing_until_sign_in", "steps": [{"signed_out": None}, {"subscribe": [sub("SBIN", 2), sub("INFY", 3)]}, {"reconnect": None}, frame(AK)]},
    {"name": "subscribe_while_signing_in", "steps": [{"reconnect": None}, {"subscribe": [sub("SBIN", 2)]}, frame(AK)]},
    {"name": "subscribe_again_after_sign_in_is_accepted", "steps": [{"reconnect": None}, frame(AK), {"subscribe": [sub("SBIN", 2)]}]},
    {"name": "unsubscribe_while_signed_out", "steps": [{"subscribe": [sub("SBIN", 2), sub("INFY", 3)]}, {"signed_out": None}, {"unsubscribe": sub("SBIN", 2)}, {"reconnect": None}, frame(AK)]},
    {"name": "a_shared_token_without_an_exchange_after_one_is_unsubscribed", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 2, "BSE")]}, {"unsubscribe": sub("SBIN", 2, "BSE")}, frame({"t": "tf", "tk": "3045", "lp": "805.45"})]},
    {"name": "heartbeat_answers_naming_a_token", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "hk", "e": "NSE", "tk": "3045", "lp": "1"}), frame({"t": "h", "e": "NSE", "tk": "3045", "lp": "1"})]},
    {"name": "zero_average_price_keeps_the_cached_value", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), frame({"t": "tk", "e": "NSE", "tk": "3045", "ap": "0"}), frame({"t": "tk", "e": "NSE", "tk": "3045", "ap": "0.0"})]},
    {"name": "a_null_value_keeps_the_cached_value", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), {"text": '{"t":"tk","e":"NSE","tk":"3045","o":null,"h":null,"l":null,"c":null,"ap":null,"lp":"806"}'}]},
    {"name": "a_null_value_over_nothing", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"text": '{"t":"tf","e":"NSE","tk":"3045","o":null,"lp":"806"}'}]},
    {"name": "subscribe_one_quote", "steps": [{"subscribe": [sub("SBIN", 2)]}]},
    {"name": "subscribe_in_each_mode", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("RELIANCE", 3)]}]},
    {"name": "touchline_and_depth_go_in_separate_messages", "steps": [{"subscribe": [sub("SBIN", 3), sub("INFY", 2), sub("RELIANCE", 1), sub("TCS", 3)]}]},
    {"name": "ltp_and_quote_of_one_symbol_share_one_request", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}]},
    {"name": "touchline_and_depth_of_one_symbol", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("INFY", 2)]}, {"subscribe": [sub("SBIN", 3)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 2)]}]},
    {"name": "the_same_subscription_again_later", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("SBIN", 2)]}]},
    {"name": "an_index_uses_the_cash_exchange", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX"), sub("SENSEX", 2, "BSE_INDEX")]}]},
    {"name": "futures_and_commodities", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", 2, "NFO"), sub("CRUDEOIL", 2, "MCX")]}]},
    {"name": "an_exchange_shoonya_does_not_have", "steps": [{"subscribe": [sub("ODD", 2, "NCO")]}]},
    {"name": "a_mode_shoonya_does_not_have", "steps": [{"subscribe": [sub("SBIN", 4)]}, {"subscribe": [sub("SBIN", 0)]}]},
    {"name": "unsubscribe_sends_a_release", "steps": [{"subscribe": [sub("SBIN", 2), sub("INFY", 3)]}, {"unsubscribe": sub("SBIN", 2)}, {"unsubscribe": sub("INFY", 3)}]},
    {"name": "unsubscribe_something_never_subscribed", "steps": [{"unsubscribe": sub("SBIN", 2)}]},
    {"name": "unsubscribe_twice", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 2)}, {"unsubscribe": sub("SBIN", 2)}]},
    {"name": "unsubscribe_one_of_two_owners_sends_nothing", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 2)}, {"unsubscribe": sub("SBIN", 2)}]},
    {"name": "unsubscribe_ltp_keeps_the_touchline_while_quote_remains", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 1)}, {"unsubscribe": sub("SBIN", 2)}]},
    {"name": "unsubscribe_depth_keeps_the_touchline", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}, {"unsubscribe": sub("SBIN", 3)}, {"unsubscribe": sub("SBIN", 2)}]},
    {"name": "resubscribe_after_unsubscribe", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 2)}, {"subscribe": [sub("SBIN", 2)]}]},
    {"name": "reconnect_signs_in_again", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"reconnect": None}]},
    {"name": "sign_in_accepted_repeats_the_subscriptions", "steps": [{"subscribe": [sub("SBIN", 2), sub("INFY", 3)]}, {"reconnect": None}, frame(AK)]},
    {"name": "sign_in_accepted_with_nothing_subscribed", "steps": [{"reconnect": None}, frame(AK)]},
    {"name": "sign_in_refused_repeats_nothing", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"reconnect": None}, frame({"t": "ak", "s": "NOT_OK", "emsg": "Invalid Access Token"})]},
    {"name": "heartbeat_answers_carry_no_data", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "hk"}), frame({"t": "h"})]},
    {"name": "quote_frame_full", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch())]},
    {"name": "quote_frame_partial", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch(t="tk"))]},
    {"name": "ltp_frame", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(touch())]},
    {"name": "ltp_and_quote_each_get_a_tick", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, frame(touch())]},
    {"name": "partial_frame_merges_into_the_cache", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), frame({"t": "tk", "e": "NSE", "tk": "3045", "lp": "806.1"})]},
    {"name": "zero_ohlc_keeps_the_cached_value", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), frame({"t": "tk", "e": "NSE", "tk": "3045", "lp": "806.1", "o": "0", "h": "0.0", "l": "", "c": 0, "ap": "0.00"})]},
    {"name": "zero_ohlc_with_nothing_cached_is_zero", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "tf", "e": "NSE", "tk": "3045", "lp": "806.1", "o": "0", "h": "0.0"})]},
    {"name": "zero_volume_is_not_preserved", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), frame({"t": "tk", "e": "NSE", "tk": "3045", "v": "0", "lp": "0"})]},
    {"name": "odd_numbers_become_zero", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch(lp="abc", o="-", h=None, l="", v="12.9", ltq="x", pc="1e2"))]},
    {"name": "numbers_sent_as_numbers", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "tf", "e": "NSE", "tk": "3045", "lp": 805.45, "v": 1000, "o": 802.0})]},
    {"name": "depth_frame_full", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(depth())]},
    {"name": "depth_frame_partial", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(depth(t="dk"))]},
    {"name": "depth_frame_with_fewer_levels", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame({"t": "df", "e": "NSE", "tk": "3045", "lp": "805.45", "bp1": "805.4", "bq1": "100", "sp1": "805.5", "sq1": "110"})]},
    {"name": "touchline_frame_does_not_reach_a_depth_subscriber", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(touch())]},
    {"name": "depth_frame_does_not_reach_a_quote_subscriber", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(depth())]},
    {"name": "quote_and_depth_of_one_symbol", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}, frame(touch()), frame(depth())]},
    {"name": "depth_and_touchline_share_the_cache", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}, frame(touch()), frame({"t": "df", "e": "NSE", "tk": "3045", "bp1": "800", "bq1": "7", "bo1": "1"})]},
    {"name": "index_frame", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX")]}, frame(touch("26000", "NSE", lp="24100.25"))]},
    {"name": "bse_index_frame", "steps": [{"subscribe": [sub("SENSEX", 2, "BSE_INDEX")]}, frame(touch("1", "BSE", lp="79000.5"))]},
    {"name": "futures_frame", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", 2, "NFO")]}, frame(touch("35001", "NFO", lp="24005.5"))]},
    {"name": "the_same_token_on_two_exchanges_is_kept_apart", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 2, "BSE")]}, frame(touch("3045", "NSE", lp="805.45")), frame(touch("3045", "BSE", lp="805.9"))]},
    {"name": "a_frame_without_an_exchange_for_a_unique_token", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "tf", "tk": "3045", "lp": "805.45"})]},
    {"name": "a_frame_without_an_exchange_for_a_shared_token_is_dropped", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 2, "BSE")]}, frame({"t": "tf", "tk": "3045", "lp": "805.45"})]},
    {"name": "a_frame_without_an_exchange_for_an_unknown_token", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "tf", "tk": "777", "lp": "1"})]},
    {"name": "a_frame_for_an_exchange_not_subscribed", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch("3045", "BSE"))]},
    {"name": "a_frame_for_a_token_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch("1594"))]},
    {"name": "a_frame_without_a_token", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "tf", "e": "NSE", "lp": "1"}), frame({"t": "tf", "e": "NSE", "tk": "", "lp": "1"})]},
    {"name": "a_frame_without_a_type", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"e": "NSE", "tk": "3045", "lp": "1"})]},
    {"name": "an_unknown_frame_type", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame({"t": "zz", "e": "NSE", "tk": "3045", "lp": "1"})]},
    {"name": "text_that_is_not_json", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame("not json")]},
    {"name": "frame_after_unsubscribe", "steps": [{"subscribe": [sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 2)}, frame(touch())]},
    {"name": "the_cache_is_forgotten_with_the_last_subscription", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), {"unsubscribe": sub("SBIN", 2)}, {"subscribe": [sub("SBIN", 2)]}, frame({"t": "tk", "e": "NSE", "tk": "3045", "lp": "1.5"})]},
    {"name": "the_cache_is_kept_while_another_mode_remains", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, frame(touch()), {"unsubscribe": sub("SBIN", 1)}, frame({"t": "tk", "e": "NSE", "tk": "3045", "lp": "1.5"})]},
    {"name": "the_cache_is_forgotten_at_sign_in", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(touch()), {"reconnect": None}, frame(AK), frame({"t": "tk", "e": "NSE", "tk": "3045", "lp": "1.5"})]},
]
