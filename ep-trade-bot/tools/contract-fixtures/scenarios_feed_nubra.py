"""Nubra feed: one socket (wss://api.nubra.io/apibatch/ws, bearer token and x-device-id in the headers) and plain text commands that carry the session token: batch_subscribe <token> index|index_bucket|orderbook <json> [interval] [exchange]. LTP and Quote
subscribe an instrument (or an index by its exchange name, Nifty 50) on the index channel and its 1d candle bucket; Depth subscribes the order book by reference id. Subscribe requests are gathered for half a second and sent grouped by exchange, so the recording fires the
timer by hand. Frames are protobuf (an Any in an Any); the adapter reads each message type with its own handler. Run on the real adapter with a fake socket."""

import json
import sys
import threading
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

NOW_MS = 1_793_700_000_123
BROKER = "nubra"
AUTH_TOKEN = "nubra-session-token"
CLIENT_ID = None
ENV = {"BROKER_API_KEY": "9876543210", "BROKER_API_SECRET": "1234"}


def row(symbol, brsymbol, exchange, brexchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN", "NSE", "NSE", 3045), row("INFY", "INFY", "NSE", "NSE", 1594), row("RELIANCE", "RELIANCE", "NSE", "NSE", 2885), row("TATAMOTORS", "TATAMOTORS", "BSE", "BSE", 500570),
    row("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", "NSE", 49500, "FUT"), row("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", "BSE", 60001, "FUT"), row("USDINR25NOV26FUT", "USDINR26NOVFUT", "CDS", "NSE", 90001, "FUT"),
    row("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", "MCX", 80001, "FUT"), row("CRUDEOIL25NOV26FUT", "CRUDEOIL26NOVFUT", "NCO", "NCO", 70001, "FUT"),
    row("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "INDEX"), row("BANKNIFTY", "BANKNIFTY", "NSE_INDEX", "NSE", 26009, "INDEX"),
    row("MIDCPNIFTY", "MIDCPNIFTY", "NSE_INDEX", "NSE", 26074, "INDEX"), row("NOMAPINDEX", "", "NSE_INDEX", "NSE", 26999, "INDEX"),
    row("COLONTOKEN", "COLONTOKEN", "NSE", "NSE", "7777:9"), row("TEXTTOKEN", "TEXTTOKEN", "NSE", "NSE", "abc"), row("TWOPART", "TWOPART", "NSE", "NSE", "8888::::x"),
]


class Driver:
    """Runs the real adapter with a fake socket; the half-second timer is fired by hand."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.nubra.api import nubrawebsocket as ws_module
        from broker.nubra.streaming import nubra_adapter as adapter_module

        self.ws_module, self.adapter_module = ws_module, adapter_module
        self.timers = []
        driver = self

        class Timer:
            def __init__(self, interval, function, *a, **k):
                self.function = function
                self.daemon = False
                self.fired = False

            def start(self):
                driver.timers.append(self)

            def is_alive(self):
                return not self.fired

            def cancel(self):
                self.fired = True

        threading.Timer = Timer

    def reset(self):
        self.sent, self.published, self.timers[:] = [], [], []
        sent = self.sent

        class FakeApp:
            def __init__(self):
                self.sock = types.SimpleNamespace(connected=True)

            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        adapter = self.adapter_module.NubraWebSocketAdapter()
        adapter.cleanup_zmq = lambda: None
        client = self.adapter_module._StreamingNubraWebSocket(
            auth_token=AUTH_TOKEN, on_index_data=adapter._on_index_data, on_orderbook_data=adapter._on_orderbook_data, on_ohlcv_data=adapter._on_ohlcv_data,
        )
        client.ws = FakeApp()
        client._connected_event.set()
        adapter.ws_client = client
        adapter.running = True
        adapter.connected = True
        adapter.user_id = "nubra-user"
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.client = adapter, client

    def _fire(self):
        timers, self.timers[:] = list(self.timers), []
        for timer in timers:
            timer.fired = True
            timer.function()

    def _take(self, extra=None):
        out = {"sent": list(self.sent), "published": list(self.published)}
        if extra:
            out.update(extra)
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        errors = []
        for item in items:
            result = self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"], item.get("depth_level", 5))
            if result.get("status") != "success":
                errors.append(result.get("error_code"))
        self._fire()
        return self._take({"errors": errors} if errors else None)

    def unsubscribe(self, item):
        result = self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        self._fire()
        return self._take({"error": result.get("error_code")} if result.get("status") != "success" else None)

    def reconnect(self, _):
        self.client._on_open(self.client.ws)
        self._fire()
        return self._take()

    def frame(self, hex_text):
        self.client._on_message(None, bytes.fromhex(hex_text))
        return self._take()

    def text(self, text):
        self.client._on_message(None, text if isinstance(text, str) else json.dumps(text))
        return self._take()


def _any(message):
    from google.protobuf.any_pb2 import Any

    inner = Any()
    inner.Pack(message)
    outer = Any()
    outer.type_url = "type.googleapis.com/wrapper"
    outer.value = inner.SerializeToString()
    return outer.SerializeToString().hex()


def _pb():
    import broker.nubra.api.nubrawebsocket  # noqa: F401  (puts the protos on the path)

    from protos import nubrafrontend_pb2

    return nubrafrontend_pb2


def index_frame(name="SBIN", exchange="NSE", value=80545, high=80950, low=79900, volume=1234567, prev_close=80000, oi=0, pct=0.68, ts=1793700000, instruments=True, also=None):
    pb = _pb()
    msg = pb.BatchWebSocketIndexMessage()
    for target, nm, ex in [(msg.instruments if instruments else msg.indexes, name, exchange)] + list(also or []):
        obj = target.add()
        obj.indexname, obj.exchange, obj.index_value, obj.high_index_value, obj.low_index_value = nm, ex, value, high, low
        obj.volume, obj.prev_close, obj.volume_oi, obj.changepercent, obj.timestamp = volume, prev_close, oi, pct, ts
    return _any(msg)


def two_lists_frame():
    pb = _pb()
    msg = pb.BatchWebSocketIndexMessage()
    for target, name, value in [(msg.instruments, "INFY", 150010), (msg.indexes, "SBIN", 80545)]:
        obj = target.add()
        obj.indexname, obj.exchange, obj.index_value, obj.high_index_value, obj.low_index_value, obj.volume, obj.prev_close, obj.timestamp = name, "NSE", value, value + 100, value - 100, 5, value - 500, 1793700000
    return _any(msg)


def bucket_frame(name="NIFTY", exchange="NSE", o=2400000, h=2415000, low=2395000, c=2410025, bucket_volume=50, cumulative=0, ts=1793700000, instruments=False):
    pb = _pb()
    msg = pb.BatchWebSocketIndexBucketMessage()
    obj = (msg.instruments if instruments else msg.indexes).add()
    obj.indexname, obj.exchange, obj.open, obj.high, obj.low, obj.close, obj.bucket_volume, obj.cumulative_volume, obj.timestamp = name, exchange, o, h, low, c, bucket_volume, cumulative, ts
    return _any(msg)


def book_frame(ref=3045, ltp=80545, bids=None, asks=None, ltq=25, volume=1234567, inst=0, ts=1793700000):
    pb = _pb()
    msg = pb.BatchWebSocketOrderbookMessage()
    obj = msg.instruments.add()
    obj.ref_id, obj.inst_id, obj.ltp, obj.ltq, obj.volume, obj.timestamp = ref, inst, ltp, ltq, volume, ts
    for price, qty, orders in bids if bids is not None else [(80540 - 5 * i, 100 * (i + 1), i + 1) for i in range(5)]:
        level = obj.bids.add()
        level.price, level.quantity, level.orders = price, qty, orders
    for price, qty, orders in asks if asks is not None else [(80550 + 5 * i, 110 * (i + 1), i + 2) for i in range(5)]:
        level = obj.asks.add()
        level.price, level.quantity, level.orders = price, qty, orders
    return _any(msg)


def greeks_frame(ref=3045, oi=7000000, prev_oi=6900000, ltp=80545, volume=1234567):
    pb = _pb()
    msg = pb.BatchWebSocketGreeksMessage()
    obj = msg.instruments.add()
    obj.ref_id, obj.oi, obj.prev_oi, obj.ltp, obj.volume = ref, oi, prev_oi, ltp, volume
    return _any(msg)


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def S(*items):
    return {"subscribe": list(items)}


def U(symbol, exchange, mode):
    return {"unsubscribe": sub(symbol, exchange, mode)}


def F(hex_text):
    return {"frame": hex_text}


RECONNECT = {"reconnect": None}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


SCENARIOS = [
    scenario("subscribe_ltp_sends_the_index_channel_and_the_day_bucket", S(sub("SBIN", "NSE", 1))),
    scenario("ltp_tick_from_the_index_channel", S(sub("SBIN", "NSE", 1)), F(index_frame())),
    scenario("quote_tick_from_the_index_channel_closes_at_the_previous_close", S(sub("SBIN", "NSE", 2)), F(index_frame())),
    scenario("quote_tick_takes_open_and_close_from_an_earlier_bucket", S(sub("SBIN", "NSE", 2)), F(bucket_frame("SBIN", "NSE", o=80100, c=80410, instruments=True)), F(index_frame())),
    scenario("quote_tick_with_a_bucket_that_has_no_open", S(sub("SBIN", "NSE", 2)), F(bucket_frame("SBIN", "NSE", o=0, c=0, instruments=True)), F(index_frame())),
    scenario("a_bucket_alone_publishes_nothing", S(sub("SBIN", "NSE", 2)), F(bucket_frame("SBIN", "NSE", instruments=True))),
    scenario("ltp_and_quote_on_one_symbol_publish_the_quote_first", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)), F(index_frame())),
    scenario("a_frame_in_the_indexes_list", S(sub("SBIN", "NSE", 1)), F(index_frame(instruments=False))),
    scenario("a_frame_with_both_lists_reads_the_indexes_first", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)), F(two_lists_frame())),
    scenario("a_frame_for_a_symbol_in_lower_case", S(sub("SBIN", "NSE", 1)), F(index_frame("sbin"))),
    scenario("a_frame_for_a_symbol_not_subscribed", S(sub("SBIN", "NSE", 1)), F(index_frame("INFY", value=150010))),
    scenario("a_frame_with_no_name", S(sub("SBIN", "NSE", 1)), F(index_frame(""))),
    scenario("a_frame_with_no_timestamp_is_stamped_now", S(sub("SBIN", "NSE", 1)), F(index_frame(ts=0))),
    scenario("a_frame_with_a_zero_price", S(sub("SBIN", "NSE", 2)), F(index_frame(value=0, high=0, low=0, volume=0, prev_close=0))),
    scenario("a_frame_for_the_bse_listing_of_the_same_name", S(sub("TATAMOTORS", "BSE", 1)), F(index_frame("TATAMOTORS", "BSE", 50100))),
    scenario("the_derivative_exchanges_ride_on_the_cash_exchange", S(sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("GOLDM25NOV26FUT", "MCX", 1))),
    scenario("an_exchange_nubra_does_not_know_falls_back_to_nse", S(sub("USDINR25NOV26FUT", "CDS", 1), sub("CRUDEOIL25NOV26FUT", "NCO", 1))),
    scenario("a_burst_is_grouped_by_exchange_with_the_book_last", S(sub("SBIN", "NSE", 1), sub("TATAMOTORS", "BSE", 2), sub("SBIN", "NSE", 3), sub("INFY", "NSE", 2), sub("INFY", "NSE", 3), sub("GOLDM25NOV26FUT", "MCX", 2))),
    scenario("subscribing_again_sends_nothing_new", S(sub("SBIN", "NSE", 1)), S(sub("SBIN", "NSE", 1)), S(sub("SBIN", "NSE", 2))),
    scenario("subscribing_the_same_symbol_twice_in_one_burst", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 1))),
    scenario("an_invalid_mode_is_refused", S(sub("SBIN", "NSE", 4), sub("SBIN", "NSE", 0))),
    scenario("index_quote_goes_by_its_exchange_name", S(sub("NIFTY", "NSE_INDEX", 2)), F(index_frame("Nifty 50", "NSE", 2410025, 2415000, 2395000, 0, 2400000, instruments=False)), F(bucket_frame("NIFTY 50", "NSE", o=2400000, c=2410025))),
    scenario("index_bucket_then_index_gives_open_and_close", S(sub("NIFTY", "NSE_INDEX", 2)), F(bucket_frame("Nifty 50", "NSE", o=2398000, c=2405000)), F(index_frame("Nifty 50", "NSE", 2410025, 2415000, 2395000, 0, 2400000, instruments=False))),
    scenario("index_ltp", S(sub("NIFTY", "NSE_INDEX", 1)), F(index_frame("NIFTY 50", "NSE", 2410025, instruments=False))),
    scenario("bank_nifty_sensex_and_the_other_known_names", S(sub("BANKNIFTY", "NSE_INDEX", 1), sub("SENSEX", "BSE_INDEX", 1)), F(index_frame("Nifty Bank", "NSE", 5400000, instruments=False)), F(index_frame("BSE SENSEX", "BSE", 8010000, instruments=False))),
    scenario("index_name_for_the_other_index_exchange_is_ignored", S(sub("SENSEX", "BSE_INDEX", 1)), F(index_frame("BSE SENSEX", "BSE", 8010000, instruments=False)), F(index_frame("NIFTY 50", "NSE", 2410025, instruments=False))),
    scenario("an_index_with_no_known_name_uses_its_broker_symbol", S(sub("MIDCPNIFTY", "NSE_INDEX", 1)), F(index_frame("MIDCPNIFTY", "NSE", 1250000, instruments=False))),
    scenario("an_index_with_no_broker_symbol_is_refused", S(sub("NOMAPINDEX", "NSE_INDEX", 1))),
    scenario("depth_on_an_index_is_refused", S(sub("NIFTY", "NSE_INDEX", 3))),
    scenario("subscribe_depth_sends_the_order_book", S(sub("SBIN", "NSE", 3))),
    scenario("depth_tick_from_the_order_book", S(sub("SBIN", "NSE", 3)), F(book_frame())),
    scenario("a_book_serves_every_mode_subscribed", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)), F(book_frame())),
    scenario("a_book_with_no_depth_mode_still_serves_ltp", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)), F(book_frame(bids=[], asks=[]))),
    scenario("a_book_names_the_instrument_when_it_has_no_reference_id", S(sub("SBIN", "NSE", 3)), F(book_frame(ref=0, inst=3045))),
    scenario("a_book_with_neither_id_is_dropped", S(sub("SBIN", "NSE", 3)), F(book_frame(ref=0, inst=0))),
    scenario("a_book_for_another_instrument_is_dropped", S(sub("SBIN", "NSE", 3)), F(book_frame(ref=1594))),
    scenario("a_short_book_is_padded_to_five", S(sub("SBIN", "NSE", 3)), F(book_frame(bids=[(80540, 100, 1)], asks=[(80550, 110, 1), (80555, 120, 2)]))),
    scenario("a_long_book_is_cut_to_five_and_totalled_on_five", S(sub("SBIN", "NSE", 3)), F(book_frame(bids=[(80540 - 5 * i, 100, 1) for i in range(7)], asks=[(80550 + 5 * i, 100, 1) for i in range(7)]))),
    scenario("a_book_with_a_zero_price_best_level", S(sub("SBIN", "NSE", 2, ), sub("SBIN", "NSE", 3)), F(book_frame(bids=[(0, 0, 0)], asks=[(0, 0, 0)]))),
    scenario("a_book_with_a_zero_price_for_the_last_trade", S(sub("SBIN", "NSE", 1)), F(book_frame(ltp=0, ts=0))),
    scenario("a_quote_off_the_book_carries_the_best_bid_and_ask", S(sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)), F(book_frame())),
    scenario("depth_for_a_symbol_with_a_colon_in_its_token", S(sub("COLONTOKEN", "NSE", 3))),
    scenario("depth_for_a_token_with_four_colons", S(sub("TWOPART", "NSE", 3))),
    scenario("depth_for_a_token_that_is_not_a_number_is_refused", S(sub("TEXTTOKEN", "NSE", 3))),
    scenario("a_greeks_frame_publishes_nothing", S(sub("SBIN", "NSE", 3)), F(greeks_frame())),
    scenario("a_frame_that_is_not_protobuf_publishes_nothing", S(sub("SBIN", "NSE", 1)), F("00ff00ff")),
    scenario("a_text_frame_publishes_nothing", S(sub("SBIN", "NSE", 1)), {"text": "subscribed"}),
    scenario("unsubscribe_the_only_mode_sends_both_channels", S(sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 2)),
    scenario("unsubscribe_one_of_two_modes_sends_nothing", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 1), F(index_frame())),
    scenario("unsubscribe_depth_sends_the_order_book", S(sub("SBIN", "NSE", 3)), U("SBIN", "NSE", 3), F(book_frame())),
    scenario("unsubscribe_after_a_quote_and_a_depth_follows_the_last_subscribe", S(sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)), U("SBIN", "NSE", 2), U("SBIN", "NSE", 3)),
    scenario("unsubscribe_after_a_depth_and_a_quote_follows_the_last_subscribe", S(sub("SBIN", "NSE", 3), sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 3), U("SBIN", "NSE", 2)),
    scenario("unsubscribe_a_mode_not_subscribed_keeps_the_others", S(sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 3), F(index_frame())),
    scenario("unsubscribe_what_was_never_subscribed", U("SBIN", "NSE", 2)),
    scenario("unsubscribe_an_index_sends_its_exchange_name", S(sub("NIFTY", "NSE_INDEX", 2)), U("NIFTY", "NSE_INDEX", 2), F(index_frame("Nifty 50", "NSE", 2410025, instruments=False))),
    scenario("unsubscribe_an_mcx_future_names_mcx", S(sub("GOLDM25NOV26FUT", "MCX", 1)), U("GOLDM25NOV26FUT", "MCX", 1)),
    scenario("subscribe_again_after_unsubscribe_sends_again", S(sub("SBIN", "NSE", 1)), U("SBIN", "NSE", 1), S(sub("SBIN", "NSE", 1))),
    scenario("reconnect_repeats_every_channel", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 3)), RECONNECT),
    scenario("reconnect_after_a_single_unsubscribe_drops_it", S(sub("SBIN", "NSE", 1)), S(sub("INFY", "NSE", 1)), U("SBIN", "NSE", 1), RECONNECT),
    scenario("reconnect_after_an_unsubscribe_from_a_burst_still_repeats_the_whole_burst", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1), sub("INFY", "NSE", 3)), U("SBIN", "NSE", 1), RECONNECT),
    scenario("reconnect_with_nothing_subscribed", RECONNECT),
    scenario("reconnect_repeats_an_index_and_a_depth", S(sub("NIFTY", "NSE_INDEX", 2), sub("SBIN", "NSE", 3), sub("GOLDM25NOV26FUT", "MCX", 1)), RECONNECT),
]
