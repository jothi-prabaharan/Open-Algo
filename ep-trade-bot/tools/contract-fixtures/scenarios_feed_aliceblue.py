"""AliceBlue feed: a Noren-style JSON socket signed in by a connect frame whose token is the user session hashed twice, answered by a ck frame. Subscriptions are batched by feed type (t for ticks, d for depth), scrips joined by a hash; an unsubscribe is one frame whatever the mode. The adapter keeps one record a scrip with the highest mode and every mode asked for, merges ticks over a snapshot that keeps the last non-zero price, and publishes each tick to the modes the scrip was asked for."""

import json
import threading

NOW_MS = 1_793_700_000_123
BROKER = "aliceblue"
AUTH_TOKEN = "ab-user-session"
CLIENT_ID = "AB1234"
ENV = {"BROKER_API_KEY": "ab-app-code"}


def row(symbol, brsymbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN-EQ", "NSE", 3045), row("INFY", "INFY-EQ", "NSE", 1594), row("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570), row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "FUT"),
    row("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "FUT"), row("USDINR25NOV26FUT", "USDINR25NOV26FUT", "CDS", 90001, "FUT"), row("USDINR28NOV26FUT", "USDINR28NOV26FUT", "BCD", 91001, "FUT"),
    row("GOLDM25NOV26FUT", "GOLDM25NOV26FUT", "MCX", 80001, "FUT"), row("NIFTY", "Nifty 50", "NSE_INDEX", 99926000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", 99900001, "INDEX"),
    row("MCXENERGY", "MCXENERGY", "MCX_INDEX", 99955001, "INDEX"), row("PLAINIDX", "PLAINIDX", "NSE_INDEX", 26009, "INDEX"), row("ODDTOKEN", "ODDTOKEN-EQ", "NSE", "777.0"),
]


class Driver:
    """Runs the real adapter with a fake socket; the batch timer is fired by hand."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.aliceblue.streaming.aliceblue_adapter import AliceblueWebSocketAdapter

        self.adapter_cls = AliceblueWebSocketAdapter
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

        class Sock:
            connected = True

        class Fake:
            sock = Sock()

            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        adapter = self.adapter_cls()
        adapter.client_id = CLIENT_ID
        adapter.session_id = AUTH_TOKEN
        adapter.ws_client = Fake()
        adapter.running = True
        adapter.connected = True
        adapter._start_heartbeat = lambda ws: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        adapter._last_sub_flush_at = NOW_MS / 1000.0
        self.adapter, self.fake = adapter, Fake()
        self.fake = adapter.ws_client

    def _fire(self):
        timers, self.timers[:] = list(self.timers), []
        for function in timers:
            function()

    def _take(self, extra=None):
        out = {"sent": [json.loads(x) for x in self.sent], "published": list(self.published)}
        if extra:
            out.update(extra)
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        self.adapter._last_sub_flush_at = NOW_MS / 1000.0
        errors = []
        for item in items:
            result = self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
            if result.get("status") != "success":
                errors.append(result.get("code"))
        self._fire()
        return self._take({"errors": errors} if errors else None)

    def unsubscribe(self, item):
        result = self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take({"error": result.get("code")} if result.get("status") != "success" else None)

    def reconnect(self, _):
        self.adapter._authenticate_websocket(self.fake)
        return self._take()

    def text(self, text):
        self.adapter._handle_message(text if isinstance(text, str) else json.dumps(text))
        self._fire()
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


CK = {"t": "ck", "s": "OK", "uid": "AB1234_API"}


def tk(token="3045", exchange="NSE", **extra):
    d = {"t": "tk", "e": exchange, "tk": str(token), "ts": "SBIN-EQ", "lp": "805.45", "o": "801.00", "h": "809.50", "l": "799.00", "c": "800.00", "v": "1234567", "ap": "804.10", "pc": "0.68", "cv": "5.45",
         "ft": "1793699999", "toi": "9000", "ti": "0.05", "ls": "1", "ml": "1", "pp": "2"}
    d.update(extra)
    return d


def tf(token="3045", exchange="NSE", **extra):
    d = {"t": "tf", "e": exchange, "tk": str(token), "lp": "806.00", "pc": "0.75", "v": "1234900", "ft": "1793700001"}
    d.update(extra)
    return d


def depth(kind="df", token="3045", exchange="NSE", **extra):
    d = {"t": kind, "e": exchange, "tk": str(token), "ts": "SBIN-EQ", "lp": "805.45", "ft": "1793699999"}
    for i in range(1, 6):
        d[f"bp{i}"], d[f"bq{i}"], d[f"bo{i}"] = f"{805.4 - 0.05 * (i - 1):.2f}", str(100 * i), str(i)
        d[f"sp{i}"], d[f"sq{i}"], d[f"so{i}"] = f"{805.5 + 0.05 * (i - 1):.2f}", str(110 * i), str(2 * i)
    d.update(extra)
    return d


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


S = sub("SBIN", "NSE", 2)
L = sub("SBIN", "NSE", 1)
D = sub("SBIN", "NSE", 3)
SCENARIOS = [
    # ---- connecting ---------------------------------------------------------------------------------------------
    scenario("the_connection_sends_its_connect_frame", {"reconnect": {}}),
    scenario("the_answer_ok_repeats_nothing_when_nothing_is_subscribed", {"reconnect": {}}, {"text": CK}),
    scenario("the_answer_ok_repeats_the_subscriptions", {"subscribe": [S, sub("INFY", "NSE", 3)]}, {"reconnect": {}}, {"text": CK}),
    scenario("a_second_answer_repeats_them_again", {"subscribe": [S]}, {"text": CK}, {"text": CK}),
    scenario("the_answer_not_ok_repeats_nothing", {"subscribe": [S]}, {"reconnect": {}}, {"text": {"t": "ck", "s": "Not_OK"}}),
    scenario("the_answer_with_no_status_repeats_nothing", {"subscribe": [S]}, {"text": {"t": "ck"}}),
    scenario("a_connection_confirmation_repeats_nothing", {"subscribe": [S]}, {"reconnect": {}}, {"text": {"t": "cf", "k": "OK"}}),
    scenario("a_refused_connection_confirmation", {"subscribe": [S]}, {"text": {"t": "cf", "k": "NOT_OK"}}),
    scenario("the_repeat_asks_for_the_highest_mode_only", {"subscribe": [S, D]}, {"text": CK}),
    scenario("the_repeat_asks_for_a_lower_mode_after_the_higher_one_is_dropped", {"subscribe": [S, D]}, {"unsubscribe": D}, {"text": CK}),
    scenario("the_repeat_sends_each_scrip_once", {"subscribe": [S, L, sub("INFY", "NSE", 2)]}, {"text": CK}),
    # ---- subscribing --------------------------------------------------------------------------------------------
    scenario("subscribe_quote_is_a_tick_frame", {"subscribe": [S]}),
    scenario("subscribe_ltp_is_a_tick_frame", {"subscribe": [L]}),
    scenario("subscribe_depth_is_a_depth_frame", {"subscribe": [D]}),
    scenario("subscribe_several_is_one_frame_a_kind", {"subscribe": [S, sub("INFY", "NSE", 3), sub("NIFTY25NOV26FUT", "NFO", 2), sub("TATAMOTORS", "BSE", 1), sub("INFY", "NSE", 2)]}),
    scenario("subscribe_every_exchange", {"subscribe": [sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("USDINR28NOV26FUT", "BCD", 2), sub("GOLDM25NOV26FUT", "MCX", 2), sub("TATAMOTORS", "BSE", 2)]}),
    scenario("subscribe_an_index_drops_the_999_prefix", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 1), sub("MCXENERGY", "MCX_INDEX", 2)]}),
    scenario("subscribe_an_index_without_the_prefix", {"subscribe": [sub("PLAINIDX", "NSE_INDEX", 2)]}),
    scenario("subscribe_a_token_with_a_decimal_point", {"subscribe": [sub("ODDTOKEN", "NSE", 2)]}),
    scenario("subscribe_the_same_scrip_again_sends_again", {"subscribe": [S]}, {"subscribe": [S]}),
    scenario("subscribe_quote_then_depth_sends_both", {"subscribe": [S]}, {"subscribe": [D]}),
    scenario("subscribe_depth_then_quote_sends_both", {"subscribe": [D]}, {"subscribe": [S]}),
    # ---- unsubscribing ------------------------------------------------------------------------------------------
    scenario("unsubscribe_one_of_two_scrips", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}),
    scenario("unsubscribe_depth_one_of_two", {"subscribe": [D, sub("INFY", "NSE", 2)]}, {"unsubscribe": D}),
    scenario("unsubscribe_one_mode_still_sends_the_frame_and_keeps_the_scrip", {"subscribe": [S, D, sub("INFY", "NSE", 2)]}, {"unsubscribe": D}, {"text": tf()}),
    scenario("unsubscribe_the_other_mode_ends_the_scrip", {"subscribe": [S, D, sub("INFY", "NSE", 2)]}, {"unsubscribe": D}, {"unsubscribe": S}, {"text": tf()}),
    scenario("unsubscribe_a_mode_never_asked_for_still_sends_the_frame", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": L}),
    scenario("unsubscribe_a_scrip_never_subscribed_sends_the_frame", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    scenario("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), S]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    scenario("unsubscribe_the_last_scrip", {"subscribe": [S]}, {"unsubscribe": S}),
    scenario("unsubscribing_forgets_the_snapshot", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"text": tk()}, {"unsubscribe": S}, {"subscribe": [S]}, {"text": tf(lp="0")}),
    # ---- tick data ----------------------------------------------------------------------------------------------
    scenario("a_quote_acknowledgement", {"subscribe": [S]}, {"text": tk()}),
    scenario("an_ltp_acknowledgement", {"subscribe": [L]}, {"text": tk()}),
    scenario("an_acknowledgement_with_nothing_but_the_price", {"subscribe": [S]}, {"text": {"t": "tk", "e": "NSE", "tk": "3045", "lp": "805.45"}}),
    scenario("an_acknowledgement_is_published_as_sent_even_when_zero", {"subscribe": [S]}, {"text": tk()}, {"text": tk(o="0", h="0", l="0", c="0", ap="0")}),
    scenario("a_feed_tick_after_the_acknowledgement", {"subscribe": [S]}, {"text": tk()}, {"text": tf()}),
    scenario("a_feed_tick_keeps_the_last_price_when_it_sends_zero", {"subscribe": [S]}, {"text": tk()}, {"text": tf(lp="0", o="0", h="0")}),
    scenario("a_feed_tick_takes_a_new_open_high_low_close", {"subscribe": [S]}, {"text": tk()}, {"text": tf(o="802", h="810", l="798", c="801", ap="805")}),
    scenario("a_feed_tick_before_any_acknowledgement", {"subscribe": [S]}, {"text": tf()}),
    scenario("a_feed_tick_before_any_acknowledgement_for_ltp", {"subscribe": [L]}, {"text": tf()}),
    scenario("a_feed_tick_with_only_a_volume", {"subscribe": [S]}, {"text": tk()}, {"text": {"t": "tf", "e": "NSE", "tk": "3045", "v": "0"}}),
    scenario("a_feed_tick_with_a_lower_volume_is_taken", {"subscribe": [S]}, {"text": tk()}, {"text": tf(v="100")}),
    scenario("a_feed_tick_with_an_open_interest", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"text": tk("49500", "NFO", toi="7000")}, {"text": tf("49500", "NFO", toi="7100")}),
    scenario("a_tick_for_a_scrip_nobody_asked_for", {"subscribe": [S]}, {"text": tk("9999")}),
    scenario("the_same_token_on_another_exchange_is_not_the_scrip", {"subscribe": [S]}, {"text": tk("3045", "BSE")}),
    scenario("an_index_tick_is_found_by_its_exchange", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"text": tk("26000", "NSE", ts="Nifty 50", lp="24100.25")}),
    scenario("a_tick_with_a_price_that_is_not_a_number_is_dropped", {"subscribe": [S]}, {"text": tk(lp="n/a")}),
    scenario("a_feed_tick_with_a_price_that_is_not_a_number_is_dropped", {"subscribe": [S]}, {"text": tk()}, {"text": tf(lp="x")}),
    scenario("a_tick_with_an_empty_price_is_zero", {"subscribe": [S]}, {"text": tk(lp="", v="", o="")}),
    scenario("a_message_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    scenario("an_unknown_message_type_is_ignored", {"subscribe": [S]}, {"text": {"t": "xx", "e": "NSE", "tk": "3045", "lp": "1"}}),
    scenario("two_modes_on_one_scrip_publish_to_both", {"subscribe": [L, S]}, {"text": tk()}, {"text": tf()}),
    scenario("ltp_and_depth_on_one_scrip", {"subscribe": [L, D]}, {"text": tk()}, {"text": tf()}),
    scenario("quote_and_depth_on_one_scrip", {"subscribe": [S, D]}, {"text": tk()}, {"text": tf()}),
    scenario("depth_alone_gets_no_tick_publication", {"subscribe": [D]}, {"text": tk()}, {"text": tf()}),
    # ---- depth data ---------------------------------------------------------------------------------------------
    scenario("a_depth_acknowledgement", {"subscribe": [D]}, {"text": depth("dk")}),
    scenario("a_depth_feed_tick", {"subscribe": [D]}, {"text": depth("dk")}, {"text": depth("df", lp="806")}),
    scenario("a_depth_feed_with_missing_levels_keeps_the_last_ladder", {"subscribe": [D]}, {"text": depth("dk")}, {"text": {"t": "df", "e": "NSE", "tk": "3045", "lp": "806", "bp1": "805.9", "bq1": "50"}}),
    scenario("a_depth_feed_with_no_levels_keeps_the_last_ladder", {"subscribe": [D]}, {"text": depth("dk")}, {"text": {"t": "df", "e": "NSE", "tk": "3045", "lp": "806"}}),
    scenario("a_depth_feed_with_zero_prices_keeps_the_last_ladder", {"subscribe": [D]}, {"text": depth("dk")}, {"text": depth("df", bp1="0", bp2="0", bp3="0", bp4="0", bp5="0", sp1="0", sp2="0", sp3="0", sp4="0", sp5="0")}),
    scenario("a_depth_acknowledgement_with_a_zero_price_level_leaves_it_out", {"subscribe": [D]}, {"text": depth("dk", bp3="0", sp2="0")}),
    scenario("a_depth_feed_before_an_acknowledgement", {"subscribe": [D]}, {"text": depth("df")}),
    scenario("a_depth_tick_for_a_quote_subscriber", {"subscribe": [S]}, {"text": tk()}, {"text": depth("df")}),
    scenario("a_depth_tick_for_an_ltp_subscriber", {"subscribe": [L]}, {"text": depth("df")}),
    scenario("a_depth_tick_for_quote_and_depth_subscribers", {"subscribe": [S, D]}, {"text": tk()}, {"text": depth("dk")}, {"text": depth("df")}),
    scenario("a_depth_tick_for_a_scrip_nobody_asked_for", {"subscribe": [D]}, {"text": depth("df", token="9999")}),
    scenario("a_depth_acknowledgement_with_a_quantity_that_is_not_a_number_leaves_the_level_out", {"subscribe": [D]}, {"text": depth("dk", bq2="x")}),
    scenario("a_depth_future_tick", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, {"text": depth("dk", token="49500", exchange="NFO", ts="NIFTY25NOV26FUT")}),
]
