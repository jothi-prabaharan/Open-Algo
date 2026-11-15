"""Groww feed: NATS over a WebSocket (wss://socket-api.groww.in) with a socket token and an Ed25519 key from Groww's own call. The server's INFO carries a nonce, the client answers CONNECT (the socket token, the public key and the signed nonce) and a PING; once the server
answers, every subscription is sent as SUB /ld/<eq|fo|comm|cds>/<exchange>/price|book.<token> <sid>, a batch followed by one PING. A depth subscription also takes an LTP subscription of its own. Messages are protobuf; the adapter merges what the price and book topics
give. Run on the real adapter with a fake socket; the batch timer is fired by hand and the key is fixed."""

import json
import struct
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

NOW_MS = 1_793_700_000_123
BROKER = "groww"
AUTH_TOKEN = "groww-access-token"
CLIENT_ID = None
ENV = {"BROKER_API_KEY": "groww-api-key", "BROKER_API_SECRET": "groww-api-secret"}
SOCKET_TOKEN = "groww-socket-jwt"
SEED = bytes(range(1, 33))
NONCE = "kXpA3nTqLz9sWm"


def row(symbol, brsymbol, exchange, brexchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN", "NSE", "NSE", 3045), row("INFY", "INFY", "NSE", "NSE", 1594), row("TATAMOTORS", "TATAMOTORS", "BSE", "BSE", 500570), row("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", "NSE", 49500, "FUT"),
    row("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", "BSE", 60001, "FUT"), row("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", "MCX", 80001, "FUT"), row("USDINR25NOV26FUT", "USDINR26NOVFUT", "CDS", "NSE", 90001, "FUT"),
    row("USDINR26NOVBCD", "USDINR26NOVBCD", "BCD", "BSE", 90002, "FUT"), row("CRUDEOIL25NOV26FUT", "CRUDEOIL26NOVFUT", "NCO", "NCO", 70001, "FUT"), row("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "INDEX"),
    row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "INDEX"), row("NOTOKEN", "NOTOKEN", "NSE", "NSE", ""),
]


class Driver:
    """Runs the real adapter and its real NATS client with a fake socket; the half-second batch timer is fired by hand, and the key is fixed."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.groww.streaming import groww_adapter, groww_nats, groww_nkeys, nats_websocket

        self.adapter_cls = groww_adapter.GrowwWebSocketAdapter
        self.client_cls = nats_websocket.GrowwNATSWebSocket
        self.nats, self.nkeys = groww_nats, groww_nkeys
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
        import time

        self.sent, self.published, self.timers[:] = [], [], []
        sent = self.sent

        class Fake:
            def send(self, data):
                sent.append(data if isinstance(data, str) else data.decode("latin-1"))

            def close(self):
                pass

        adapter = self.adapter_cls()
        adapter.cleanup_zmq = lambda: None
        client = self.client_cls(auth_token=AUTH_TOKEN, on_data=adapter._on_data, on_error=adapter._on_error)
        client.ws = Fake()
        client.socket_token = SOCKET_TOKEN
        client.subscription_id = "sub-1"
        client.nkey_seed = self.nkeys.encode_seed(SEED, self.nkeys.PREFIX_BYTE_USER).decode()
        client.nats_protocol = self.nats.NATSProtocol()
        client.connected = False
        client.authenticated = False
        client.connect_sent = False
        client.running = True
        adapter.ws_client = client
        adapter.running = True
        adapter.connected = True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        adapter._last_batch_flush_at = time.time()
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
        import time

        self.adapter._last_batch_flush_at = time.time()
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
        """A new connection: a fresh protocol handler, nothing signed in yet, and the old subscription ids gone."""
        client = self.client
        client.nats_protocol = self.nats.NATSProtocol()
        client.connected = True
        client.authenticated = False
        client.connect_sent = False
        client.server_nonce = None
        return self._take()

    def frame(self, hex_text):
        data = bytes.fromhex(hex_text)
        self.client._on_message(None, data)
        # Groww's client takes the connection as signed in when its server has answered for two seconds; a PONG after the CONNECT stands for that here.
        if data.startswith(b"PONG") and self.client.connected and not self.client.authenticated:
            self.client.authenticated = True
            self.client._resubscribe_all()
        return self._take()

    def text(self, text):
        self.client._on_message(None, text)
        return self._take()


# ---- frames -----------------------------------------------------------------------------------------------


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)


def f64(field, value):
    return varint(field << 3 | 1) + struct.pack("<d", value)


def nested(field, payload):
    return varint(field << 3 | 2) + varint(len(payload)) + payload


def text_field(field, value):
    return nested(field, value.encode())


def price(ltp=805.45, ts=1793700000123.0, o=801.0, h=809.5, low=799.0, c=800.0, volume=1234567.0, value=9.9e8):
    parts = b""
    for field, v in ((1, ts), (2, o), (3, h), (4, low), (5, c), (6, volume), (7, value), (13, ltp)):
        if v is not None:
            parts += f64(field, v)
    return nested(4, parts)


def level(p, q, orders=1):
    return varint(1 << 3) + varint(orders) + nested(2, f64(1, p) + f64(2, q))


def book(buy=None, sell=None, ts=1793700000200.0):
    buy = buy if buy is not None else [(805.4 - 0.05 * i, 100.0 * (i + 1), i + 1) for i in range(5)]
    sell = sell if sell is not None else [(805.5 + 0.05 * i, 110.0 * (i + 1), i + 2) for i in range(5)]
    body = f64(1, ts)
    for p, q, o in buy:
        body += nested(2, level(p, q, o))
    for p, q, o in sell:
        body += nested(3, level(p, q, o))
    return nested(5, body)


def index(value=24100.25, ts=1793700000300.0):
    return nested(6, f64(1, ts) + f64(2, value))


def msg(subject, sid, payload):
    return (f"MSG {subject} {sid} {len(payload)}\r\n".encode() + payload + b"\r\n").hex()


def info(nonce=NONCE):
    body = {"server_id": "NA6FLYVSOQ", "server_name": "groww-nats", "version": "2.10.18", "proto": 1, "headers": True, "max_payload": 1048576, "client_id": 77}
    if nonce is not None:
        body["nonce"] = nonce
    return ("INFO " + json.dumps(body) + "\r\n").encode().hex()


PONG = b"PONG\r\n".hex()
PING = b"PING\r\n".hex()
OK = b"+OK\r\n".hex()


def err(text):
    return f"-ERR '{text}'\r\n".encode().hex()


def sub(symbol, exchange, mode, depth_level=5):
    return {"symbol": symbol, "exchange": exchange, "mode": mode, "depth_level": depth_level}


def S(*items):
    return {"subscribe": list(items)}


def U(symbol, exchange, mode):
    return {"unsubscribe": sub(symbol, exchange, mode)}


def F(hex_text):
    return {"frame": hex_text}


RECONNECT = {"reconnect": None}
UP = [RECONNECT, F(info()), F(PONG)]


def scenario(name, *steps, up=True):
    return {"name": name, "steps": (UP if up else []) + list(steps)}


def px(token=3045, seg="eq", ex="nse"):
    return f"/ld/{seg}/{ex}/price.{token}"


def bk(token=3045, seg="eq", ex="nse"):
    return f"/ld/{seg}/{ex}/book.{token}"


SCENARIOS = [
    scenario("signing_in_answers_the_server_info_once", RECONNECT, F(info()), F(info("another-nonce")), F(PONG), up=False),
    scenario("signing_in_without_a_nonce_sends_no_key", RECONNECT, F(info(None)), F(PONG), up=False),
    scenario("a_ping_is_answered_with_a_pong", F(PING)),
    scenario("a_pong_changes_nothing", F(PONG)),
    scenario("an_error_message_changes_nothing", F(err("Authorization Violation"))),
    scenario("a_stale_connection_error_changes_nothing", F(err("Stale Connection"))),
    scenario("text_that_is_not_a_command", F(b"HELLO\r\n".hex())),
    scenario("subscribe_ltp", S(sub("SBIN", "NSE", 1))),
    scenario("subscribe_quote_is_the_price_topic", S(sub("SBIN", "NSE", 2))),
    scenario("subscribe_depth_adds_a_price_subscription", S(sub("SBIN", "NSE", 3))),
    scenario("subscribe_a_burst_is_one_batch_with_one_ping", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("TATAMOTORS", "BSE", 3))),
    scenario("subscribe_every_segment", S(sub("NIFTY25NOV26FUT", "NFO", 1), sub("SENSEX26NOVFUT", "BFO", 1), sub("GOLDM25NOV26FUT", "MCX", 1), sub("USDINR25NOV26FUT", "CDS", 1), sub("USDINR26NOVBCD", "BCD", 1), sub("CRUDEOIL25NOV26FUT", "NCO", 1))),
    scenario("subscribe_depth_on_a_future", S(sub("NIFTY25NOV26FUT", "NFO", 3))),
    scenario("subscribe_an_nse_index_uses_the_symbol_as_its_token", S(sub("NIFTY", "NSE_INDEX", 1))),
    scenario("subscribe_a_bse_index_keeps_its_token", S(sub("SENSEX", "BSE_INDEX", 2))),
    scenario("subscribe_depth_on_an_index_becomes_ltp", S(sub("NIFTY", "NSE_INDEX", 3))),
    scenario("subscribe_ltp_and_quote_on_one_symbol_share_a_topic", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2))),
    scenario("subscribe_ltp_and_depth_on_one_symbol", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3))),
    scenario("subscribe_the_same_thing_twice", S(sub("SBIN", "NSE", 1)), S(sub("SBIN", "NSE", 1))),
    scenario("subscribe_invalid_mode", S(sub("SBIN", "NSE", 4), sub("SBIN", "NSE", 0))),
    scenario("subscribe_a_symbol_with_no_token", S(sub("NOTOKEN", "NSE", 1))),
    scenario("subscribe_depth_with_another_level_asks_for_five", S(sub("SBIN", "NSE", 3, 20))),
    scenario("ltp_tick_for_an_ltp_subscriber", S(sub("SBIN", "NSE", 1)), F(msg(px(), 1, price()))),
    scenario("ltp_tick_for_a_quote_subscriber", S(sub("SBIN", "NSE", 2)), F(msg(px(), 1, price()))),
    scenario("ltp_tick_leaves_out_zero_fields", S(sub("SBIN", "NSE", 2)), F(msg(px(), 1, price(o=0.0, h=0.0, low=0.0, c=0.0, volume=0.0, value=0.0)))),
    scenario("ltp_tick_with_only_a_price", S(sub("SBIN", "NSE", 2)), F(msg(px(), 1, price(o=None, h=None, low=None, c=None, volume=None, value=None, ts=None)))),
    scenario("two_subscribers_of_one_symbol_both_get_the_tick", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)), F(msg(px(), 2, price()))),
    scenario("depth_tick_for_a_depth_subscriber_has_no_price_yet", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book()))),
    scenario("depth_tick_after_a_price_tick_is_merged", S(sub("SBIN", "NSE", 3)), F(msg(px(), 2, price())), F(msg(bk(), 1, book()))),
    scenario("price_tick_after_a_depth_tick_is_merged_into_depth", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book())), F(msg(px(), 2, price()))),
    scenario("price_tick_alone_for_a_depth_subscriber_is_an_empty_depth", S(sub("SBIN", "NSE", 3)), F(msg(px(), 2, price()))),
    scenario("depth_tick_is_not_given_to_a_price_subscriber", S(sub("SBIN", "NSE", 3), sub("SBIN", "NSE", 1)), F(msg(bk(), 1, book()))),
    scenario("depth_levels_with_nothing_are_left_out", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book(buy=[(805.4, 100.0, 1), (0.0, 0.0, 0)], sell=[(0.0, 0.0, 0), (805.6, 50.0, 2)])))),
    scenario("a_book_of_seven_levels_keeps_five", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book(buy=[(805.0 - i, 10.0, 1) for i in range(7)], sell=[(806.0 + i, 10.0, 1) for i in range(7)])))),
    scenario("a_book_with_no_levels", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book(buy=[], sell=[])))),
    scenario("a_book_with_one_side_only", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 1, book(sell=[])))),
    scenario("an_index_tick", S(sub("NIFTY", "NSE_INDEX", 1)), F(msg(px("NIFTY"), 1, index()))),
    scenario("an_index_tick_for_a_bse_index", S(sub("SENSEX", "BSE_INDEX", 2)), F(msg(px(19000), 1, index(80100.5)))),
    scenario("a_future_tick", S(sub("NIFTY25NOV26FUT", "NFO", 2)), F(msg(px(49500, "fo"), 1, price(24010.5, o=24000.0, h=24100.0, low=23950.0, c=24005.0)))),
    scenario("a_message_for_an_unknown_subscription_id_found_by_its_topic", S(sub("SBIN", "NSE", 1)), F(msg(px(), 9, price()))),
    scenario("a_message_for_a_topic_nobody_asked_for", S(sub("SBIN", "NSE", 1)), F(msg(px(7777), 9, price()))),
    scenario("a_book_message_for_an_unknown_id_found_by_its_topic", S(sub("SBIN", "NSE", 3)), F(msg(bk(), 9, book()))),
    scenario("a_message_with_a_payload_that_is_not_protobuf", S(sub("SBIN", "NSE", 1)), F(msg(px(), 1, b"\xff\xff\xff\xff"))),
    scenario("a_message_with_an_empty_payload", S(sub("SBIN", "NSE", 1)), F(msg(px(), 1, b""))),
    scenario("a_message_with_unknown_fields_in_it", S(sub("SBIN", "NSE", 1)), F(msg(px(), 1, text_field(1, "SBIN") + varint(9 << 3) + varint(5) + price()))),
    scenario("a_message_before_anything_was_asked_for", F(msg(px(), 1, price()))),
    scenario("unsubscribe_ltp", S(sub("SBIN", "NSE", 1)), U("SBIN", "NSE", 1)),
    scenario("unsubscribe_depth_takes_the_price_subscription_with_it", S(sub("SBIN", "NSE", 3)), U("SBIN", "NSE", 3)),
    scenario("unsubscribe_one_of_two_modes_that_share_a_topic", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 1), F(msg(px(), 2, price()))),
    scenario("unsubscribe_what_was_never_subscribed", U("SBIN", "NSE", 1)),
    scenario("a_tick_after_unsubscribe_is_dropped", S(sub("SBIN", "NSE", 1)), U("SBIN", "NSE", 1), F(msg(px(), 1, price()))),
    scenario("subscribe_again_after_unsubscribe", S(sub("SBIN", "NSE", 1)), U("SBIN", "NSE", 1), S(sub("SBIN", "NSE", 1))),
    scenario("reconnect_repeats_every_subscription_with_new_ids", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 3)), RECONNECT, F(info()), F(PONG)),
    scenario("reconnect_with_nothing_subscribed", RECONNECT, F(info()), F(PONG)),
    scenario("reconnect_after_unsubscribe_leaves_it_out", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)), U("SBIN", "NSE", 1), RECONNECT, F(info()), F(PONG)),
    scenario("a_tick_after_reconnect_uses_the_new_ids", S(sub("SBIN", "NSE", 1)), RECONNECT, F(info()), F(PONG), F(msg(px(), 1, price()))),
    scenario("subscribing_before_the_connection_is_signed_in_waits", RECONNECT, S(sub("SBIN", "NSE", 1)), F(info()), F(PONG), up=False),
]
