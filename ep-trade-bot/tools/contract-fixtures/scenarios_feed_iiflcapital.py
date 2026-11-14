"""IIFL Capital feed: MQTT topics subscribed (market feed, index feed and open interest prefixes, a hundred topics a call) and what the adapter publishes for the packed binary packets (186-byte market packet, 16-byte open-interest packet)."""

import base64
import json
import struct

NOW_MS = 1_793_700_000_123
BROKER = "iiflcapital"
ENV = {"BROKER_API_KEY": "iifl-app-key", "BROKER_API_SECRET": "iifl-app-secret"}


def _b64(data):
    return base64.urlsafe_b64encode(json.dumps(data).encode()).decode().rstrip("=")


AUTH_TOKEN = f"{_b64({'alg': 'RS256'})}.{_b64({'preferred_username': 'iifl-user-1'})}.signature"


def row(symbol, exchange, token, brexchange, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1,
            "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045, "NSEEQ"), row("INFY", "NSE", 1594, "NSEEQ"), row("TATAMOTORS", "BSE", 500570, "BSEEQ"), row("NIFTY25NOV26FUT", "NFO", 49500, "NSEFO", "FUT"),
    row("NIFTY25NOV2624000CE", "NFO", 49599, "NSEFO", "CE"), row("SENSEX26NOVFUT", "BFO", 60001, "BSEFO", "FUT"), row("USDINR25NOV26FUT", "CDS", 90001, "NSECURR", "FUT"),
    row("GOLDM25NOV26FUT", "MCX", 80001, "MCXCOMM", "FUT"), row("NIFTY", "NSE_INDEX", 26000, "NSEEQ", "INDEX"), row("SENSEX", "BSE_INDEX", 26001, "BSEEQ", "INDEX"),
    row("NOBR", "NSE", 777, ""), row("NOTOKEN", "NSE", "", "NSEEQ"),
]
SYMBOLS += [row(f"TKN{i}", "NSE", 900000 + i, "NSEEQ") for i in range(120)]

MW, IDX, OI = "prod/marketfeed/mw/v1/", "prod/marketfeed/index/v1/", "prod/marketfeed/oi/v1/"


def packet(ltp=80545, divisor=100, ltt=1793699999, volume=1234567, depth=True, size=186, **over):
    f = dict(ltp=ltp, ltq=25, volume=volume, high=80950, low=79900, open=80100, close=80000, avg=80410, bbq=100, bbp=80540, baq=110, bap=80550, tbq=50000, tsq=61000)
    f.update(over)
    b = struct.pack("<iIIiiiii", f["ltp"], f["ltq"], f["volume"], f["high"], f["low"], f["open"], f["close"], f["avg"])
    b += struct.pack("<H", 0) + struct.pack("<IiIi", f["bbq"], f["bbp"], f["baq"], f["bap"]) + struct.pack("<IIii", f["tbq"], f["tsq"], divisor, ltt)
    levels = b""
    for i in range(10):
        if depth:
            price = 80540 - 5 * i if i < 5 else 80550 + 5 * (i - 5)
            levels += struct.pack("<IiHH", 100 * (i + 1), price, i + 1, 0)
        else:
            levels += struct.pack("<IiHH", 0, 0, 0, 0)
    b += levels
    assert len(b) == 186
    return b + b"\x00" * (size - 186)


def oi_packet(oi=12000, high=12500, low=11000, prev=11800):
    return struct.pack("<iiii", oi, high, low, prev)


class FakeMqtt:
    def __init__(self, sink):
        self.sink = sink

    def subscribe(self, topics, qos=0):
        self.sink.append({"subscribe": list(topics), "qos": qos})

    def unsubscribe(self, topics):
        self.sink.append({"unsubscribe": list(topics)})

    def is_connected(self):
        return True

    def disconnect(self):
        pass


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.iiflcapital.streaming.iiflcapital_adapter import IiflcapitalWebSocketAdapter
        from broker.iiflcapital.streaming.iiflcapital_websocket import IiflcapitalWebSocket

        self.adapter_cls, self.ws_cls = IiflcapitalWebSocketAdapter, IiflcapitalWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        ws = self.ws_cls(user_session=AUTH_TOKEN, on_ticks=adapter._handle_ticks)
        ws._mqtt, ws.running, ws.connected = FakeMqtt(self.sent), True, True

        class Alive:
            def is_alive(self):
                return True

        # The drain runs when a step says so, not on a thread of its own.
        ws._sub_thread = Alive()
        ws.SUBSCRIPTION_DELAY = 0
        adapter.ws_client, adapter.running, adapter.connected = ws, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.ws._drain_pending()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item.get("mode"))
        return self._take()

    def reconnect(self, _):
        self.ws._resubscribe_all()
        self.ws._drain_pending()
        return self._take()

    def message(self, item):
        self.ws._on_mqtt_message(item["topic"], bytes.fromhex(item["payload"]))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def msg(topic, payload):
    return {"message": {"topic": topic, "payload": payload.hex()}}


S1 = sub("SBIN", "NSE", 1)
SCENARIOS = [
    {"name": "subscribe_one_symbol_in_each_mode", "steps": [{"subscribe": [S1]}, {"subscribe": [sub("INFY", "NSE", 2)]}, {"subscribe": [sub("SBIN", "NSE", 3)]}]},
    {"name": "a_future_in_quote_or_depth_also_takes_the_open_interest_topic", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"subscribe": [sub("NIFTY25NOV2624000CE", "NFO", 3)]}]},
    {"name": "a_future_in_ltp_mode_has_no_open_interest_topic", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1)]}]},
    {"name": "an_index_uses_the_index_prefix_and_no_open_interest", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 3), sub("SENSEX", "BSE_INDEX", 1)]}]},
    {"name": "every_exchange_segment", "steps": [{"subscribe": [sub("SBIN", "NSE", 2), sub("TATAMOTORS", "BSE", 2), sub("SENSEX26NOVFUT", "BFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2)]}]},
    {"name": "a_second_mode_raises_the_stream_and_a_lower_one_keeps_it", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1)]}]},
    {"name": "subscribing_twice_in_one_window_repeats_the_topics", "steps": [{"subscribe": [S1, S1]}]},
    {"name": "unsubscribing_the_last_mode_drops_the_topics", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"unsubscribe": sub("NIFTY25NOV26FUT", "NFO", 2)}]},
    {"name": "unsubscribing_one_of_two_modes_keeps_the_stream", "steps": [{"subscribe": [S1, sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, {"unsubscribe": S1}]},
    {"name": "unsubscribe_without_a_mode_drops_every_mode", "steps": [{"subscribe": [S1, sub("SBIN", "NSE", 3)]}, {"unsubscribe": {"symbol": "SBIN", "exchange": "NSE"}}]},
    {"name": "unsubscribe_of_an_index", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}]},
    {"name": "unsubscribe_of_something_never_subscribed_sends_nothing", "steps": [{"unsubscribe": S1}]},
    {"name": "subscribe_again_after_unsubscribing", "steps": [{"subscribe": [S1]}, {"unsubscribe": S1}, {"subscribe": [S1]}]},
    {"name": "reconnect_repeats_every_subscription", "steps": [{"subscribe": [S1, sub("NIFTY25NOV26FUT", "NFO", 3), sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": None}]},
    {"name": "reconnect_after_an_unsubscribe_does_not_bring_it_back", "steps": [{"subscribe": [S1, sub("INFY", "NSE", 2)]}, {"unsubscribe": S1}, {"reconnect": None}]},
    {"name": "reconnect_with_nothing_subscribed_sends_nothing", "steps": [{"reconnect": None}]},
    {"name": "a_hundred_and_twenty_topics_go_in_batches_of_a_hundred", "steps": [{"subscribe": [sub(f"TKN{i}", "NSE", 1) for i in range(120)]}, {"reconnect": None}]},
    {"name": "a_symbol_without_a_broker_segment_is_refused", "steps": [{"subscribe": [sub("NOBR", "NSE", 1)]}]},
    {"name": "a_symbol_without_a_token_is_refused", "steps": [{"subscribe": [sub("NOTOKEN", "NSE", 1)]}]},
    {"name": "an_invalid_mode_is_refused", "steps": [{"subscribe": [sub("SBIN", "NSE", 9)]}]},
    {"name": "ltp_packet", "steps": [{"subscribe": [S1]}, msg(MW + "nseeq/3045", packet())]},
    {"name": "quote_packet", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, msg(MW + "nseeq/3045", packet())]},
    {"name": "depth_packet_publishes_every_subscribed_mode", "steps": [{"subscribe": [S1, sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)]}, msg(MW + "nseeq/3045", packet())]},
    {"name": "depth_packet_for_a_depth_only_subscription", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, msg(MW + "nseeq/3045", packet())]},
    {"name": "a_packet_with_a_zero_divisor_uses_one_hundred", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, msg(MW + "nseeq/3045", packet(divisor=0))]},
    {"name": "a_packet_with_a_divisor_of_ten_thousand", "steps": [{"subscribe": [sub("USDINR25NOV26FUT", "CDS", 2)]}, msg(MW + "nsecurr/90001", packet(ltp=832500, divisor=10000, high=833000, low=831000, open=832000, close=831800, avg=832100, bbp=832400, bap=832600))]},
    {"name": "a_packet_without_a_trade_time", "steps": [{"subscribe": [S1]}, msg(MW + "nseeq/3045", packet(ltt=0))]},
    {"name": "a_packet_of_the_usual_188_bytes_is_cut_to_186", "steps": [{"subscribe": [sub("SBIN", "NSE", 2)]}, msg(MW + "nseeq/3045", packet(size=188))]},
    {"name": "a_short_packet_is_dropped", "steps": [{"subscribe": [S1]}, msg(MW + "nseeq/3045", packet()[:100])]},
    {"name": "a_negative_price_is_read_as_signed", "steps": [{"subscribe": [S1]}, msg(MW + "nseeq/3045", packet(ltp=-5))]},
    {"name": "a_packet_without_depth", "steps": [{"subscribe": [sub("SBIN", "NSE", 3)]}, msg(MW + "nseeq/3045", packet(depth=False))]},
    {"name": "index_packet", "steps": [{"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, msg(IDX + "nseeq/26000", packet(ltp=2405050))]},
    {"name": "bse_index_packet_for_ltp", "steps": [{"subscribe": [sub("SENSEX", "BSE_INDEX", 1)]}, msg(IDX + "bseeq/26001", packet(ltp=7905050))]},
    {"name": "an_open_interest_packet_before_any_tick_publishes_nothing", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, msg(OI + "nsefo/49500", oi_packet())]},
    {"name": "open_interest_is_merged_into_the_next_tick", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 3)]}, msg(OI + "nsefo/49500", oi_packet()), msg(MW + "nsefo/49500", packet(ltp=2400550))]},
    {"name": "open_interest_in_ltp_mode_is_not_subscribed_so_not_merged", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 1)]}, msg(OI + "nsefo/49500", oi_packet()), msg(MW + "nsefo/49500", packet(ltp=2400550))]},
    {"name": "a_short_open_interest_packet_is_dropped", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, msg(OI + "nsefo/49500", oi_packet()[:12]), msg(MW + "nsefo/49500", packet(ltp=2400550))]},
    {"name": "open_interest_is_dropped_when_the_instrument_is_unsubscribed", "steps": [
        {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, msg(OI + "nsefo/49500", oi_packet()), {"unsubscribe": sub("NIFTY25NOV26FUT", "NFO", 2)}, {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]},
        msg(MW + "nsefo/49500", packet(ltp=2400550))]},
    {"name": "a_packet_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [S1]}, msg(MW + "nseeq/1594", packet())]},
    {"name": "a_packet_after_everything_was_unsubscribed", "steps": [{"subscribe": [S1]}, {"unsubscribe": S1}, msg(MW + "nseeq/3045", packet())]},
    {"name": "a_packet_after_one_of_two_modes_was_dropped_publishes_the_mode_left", "steps": [{"subscribe": [S1, sub("SBIN", "NSE", 3)]}, {"unsubscribe": sub("SBIN", "NSE", 3)}, msg(MW + "nseeq/3045", packet())]},
    {"name": "a_topic_with_an_unknown_prefix_is_ignored", "steps": [{"subscribe": [S1]}, msg("prod/other/v1/nseeq/3045", packet())]},
    {"name": "a_market_topic_in_capitals_is_not_matched", "steps": [{"subscribe": [S1]}, msg(MW + "NSEEQ/3045", packet())]},
]
