"""Nubra quotes, depth and several quotes. A feed connection (wss://api.nubra.io/apibatch/ws, Authorization and x-device-id) is opened for the read; text commands subscribe (batch_subscribe <token> index|orderbook|greeks|index_bucket ...) and the answers are protobuf frames
(an Any wrapping an Any wrapping the batch message). A stock's quote waits two seconds for its index price and falls back to its order book and then to GET /orderbooks/<ref id>?levels=1; depth waits up to ten half-seconds for a full book and falls back to ?levels=5;
an index's quote is its candle bucket's. Run on the real BrokerData with a fake socket (one frame for each time the client looks) and a fake HTTP client."""

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
    row("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", "MCX", 80001, "FUT"), row("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 19000, "INDEX"),
]


class Response:
    def __init__(self, status, body):
        self.status_code = status
        self.text = body if isinstance(body, str) else json.dumps(body)

    def json(self):
        return json.loads(self.text)


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.nubra.api import data as data_module
        from broker.nubra.api import nubrawebsocket as ws_module

        sys.path.insert(0, str(Path(ws_module.__file__).resolve().parents[1]))
        from protos import nubrafrontend_pb2

        self.data, self.ws, self.pb = data_module, ws_module, nubrafrontend_pb2

    def reset(self):
        pass

    def _install(self, ws_spec, rest):
        driver = self
        self.sent, self.http, self.queue = [], [], list(ws_spec.get("frames", []))
        connect = ws_spec.get("connect", True)
        state = {"now": 0.0}

        def deliver_next():
            if driver.queue:
                item = driver.queue.pop(0)
                driver.current.ws._on_message(None, bytes.fromhex(item["hex"]) if "hex" in item else item["text"])

        def clock():
            state["now"] += 0.05
            return state["now"]

        class FakeApp:
            def __init__(self):
                self.sock = types.SimpleNamespace(connected=True)

            def send(self, text):
                driver.sent.append(text.replace(AUTH_TOKEN, "<token>"))

            def close(self):
                pass

        class Flag:
            def __init__(self):
                self.on = False

            def set(self):
                self.on = True

            def clear(self):
                self.on = False

            def is_set(self):
                return self.on

            def wait(self, timeout=None):
                return self.on

        class FakeSocket(self.ws.NubraWebSocket):
            def __init__(self, token, *a, **k):
                super().__init__(token, *a, **k)
                self._connected_event = Flag()
                driver.current = types.SimpleNamespace(ws=self)

            def connect(self):
                if connect:
                    self.ws = FakeApp()
                    self._connected_event.set()

            def close(self):
                self._connected_event.clear()
                self.ws = None

        replies = list(rest)

        class FakeClient:
            def _next(self, method, url, **kwargs):
                driver.http.append({"method": method, "url": url.replace("https://api.nubra.io", "")})
                spec = replies.pop(0) if replies else {"status": 599, "body": ""}
                return Response(spec.get("status", 200), spec.get("body", {}))

            def get(self, url, headers=None, **k):
                return self._next("GET", url)

            def post(self, url, headers=None, content=None, **k):
                return self._next("POST", url)

        self.data.NubraWebSocket = FakeSocket
        self.data.get_httpx_client = lambda: FakeClient()
        self.data.time = types.SimpleNamespace(time=clock, monotonic=clock, sleep=lambda seconds: deliver_next())

    def _call(self, spec, call):
        self._install(spec.get("ws", {}), spec.get("rest", []))
        out = {}
        bd = self.data.BrokerData(AUTH_TOKEN)
        try:
            out["result"] = call(bd)
        except Exception as e:
            out["error"] = str(e)
        out["sent"] = list(self.sent)
        out["http"] = list(self.http)
        return out

    def quote(self, spec):
        return self._call(spec, lambda bd: bd.get_quotes(spec["symbol"], spec["exchange"]))

    def depth(self, spec):
        return self._call(spec, lambda bd: bd.get_depth(spec["symbol"], spec["exchange"]))

    def multi(self, spec):
        out = self._call(spec, lambda bd: bd.get_multiquotes([{"symbol": s, "exchange": e} for s, e in spec["symbols"]]))
        if isinstance(out.get("result"), list):
            out["result"] = sorted(out["result"], key=lambda r: (r["exchange"], r["symbol"]))
        return out


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


def index_frame(name="SBIN", exchange="NSE", value=80545, high=80950, low=79900, volume=1234567, prev_close=80000, oi=0, pct=0.68, instruments=True):
    pb = _pb()
    msg = pb.BatchWebSocketIndexMessage()
    obj = (msg.instruments if instruments else msg.indexes).add()
    obj.indexname, obj.exchange, obj.index_value, obj.high_index_value, obj.low_index_value = name, exchange, value, high, low
    obj.volume, obj.prev_close, obj.volume_oi, obj.changepercent, obj.timestamp = volume, prev_close, oi, pct, 1793700000
    return {"hex": _any(msg)}


def bucket_frame(name="NIFTY", exchange="NSE", o=2400000, h=2415000, low=2395000, c=2410025, bucket_volume=50, cumulative=0, ts=1793700000, instruments=False):
    pb = _pb()
    msg = pb.BatchWebSocketIndexBucketMessage()
    obj = (msg.instruments if instruments else msg.indexes).add()
    obj.indexname, obj.exchange, obj.open, obj.high, obj.low, obj.close, obj.bucket_volume, obj.cumulative_volume, obj.timestamp = name, exchange, o, h, low, c, bucket_volume, cumulative, ts
    return {"hex": _any(msg)}


def book_frame(ref=3045, ltp=80545, bids=None, asks=None, ltq=25, volume=1234567, inst=0):
    pb = _pb()
    msg = pb.BatchWebSocketOrderbookMessage()
    obj = msg.instruments.add()
    obj.ref_id, obj.inst_id, obj.ltp, obj.ltq, obj.volume, obj.timestamp = ref, inst, ltp, ltq, volume, 1793700000
    for price, qty, orders in bids if bids is not None else [(80540 - 5 * i, 100 * (i + 1), i + 1) for i in range(5)]:
        level = obj.bids.add()
        level.price, level.quantity, level.orders = price, qty, orders
    for price, qty, orders in asks if asks is not None else [(80550 + 5 * i, 110 * (i + 1), i + 2) for i in range(5)]:
        level = obj.asks.add()
        level.price, level.quantity, level.orders = price, qty, orders
    return {"hex": _any(msg)}


def greeks_frame(ref=3045, oi=7000000, prev_oi=6900000, ltp=80545, volume=1234567):
    pb = _pb()
    msg = pb.BatchWebSocketGreeksMessage()
    obj = msg.instruments.add()
    obj.ref_id, obj.oi, obj.prev_oi, obj.ltp, obj.volume = ref, oi, prev_oi, ltp, volume
    return {"hex": _any(msg)}


def rest_book(bid=80540, ask=80550, ltp=80545, prev_close=80000, volume=1234567, levels=1, **extra):
    book = {"bid": [{"p": bid - 5 * i, "q": 100 * (i + 1)} for i in range(levels)], "ask": [{"p": ask + 5 * i, "q": 110 * (i + 1)} for i in range(levels)], "ltp": ltp, "prev_close": prev_close, "volume": volume}
    book.update(extra)
    return {"status": 200, "body": {"orderBook": book}}


def q(symbol="SBIN", exchange="NSE", frames=(), rest=(), connect=True):
    return {"quote": {"symbol": symbol, "exchange": exchange, "ws": {"frames": list(frames), "connect": connect}, "rest": list(rest)}}


def d(symbol="SBIN", exchange="NSE", frames=(), rest=(), connect=True):
    return {"depth": {"symbol": symbol, "exchange": exchange, "ws": {"frames": list(frames), "connect": connect}, "rest": list(rest)}}


def m(symbols, frames=(), rest=(), connect=True):
    return {"multi": {"symbols": symbols, "ws": {"frames": list(frames), "connect": connect}, "rest": list(rest)}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


FULL = book_frame()
SCENARIOS = [
    scenario("a_quote_from_the_index_channel", q(frames=[index_frame()])),
    scenario("a_quote_from_the_index_channel_for_a_future", q("NIFTY25NOV26FUT", "NFO", frames=[index_frame("NIFTY26NOVFUT", "NSE", 2410025, oi=7000000)])),
    scenario("a_quote_for_a_bse_equity", q("TATAMOTORS", "BSE", frames=[index_frame("TATAMOTORS", "BSE", 50100)])),
    scenario("a_quote_for_a_bse_future", q("SENSEX26NOVFUT", "BFO", frames=[index_frame("SENSEX26NOVFUT", "BSE", 8010000)])),
    scenario("a_quote_for_a_commodity_future", q("GOLDM25NOV26FUT", "MCX", frames=[index_frame("GOLDM26NOVFUT", "MCX", 7500000)])),
    scenario("a_quote_for_a_currency_future", q("USDINR25NOV26FUT", "CDS", frames=[index_frame("USDINR26NOVFUT", "NSE", 8350)])),
    scenario("a_quote_index_frame_in_the_indexes_list", q(frames=[index_frame(instruments=False)])),
    scenario("a_quote_for_another_symbol_is_waited_out_then_the_book_answers", q(frames=[index_frame("INFY", value=150010), FULL, greeks_frame()])),
    scenario("a_quote_from_the_order_book_when_no_index_price_comes", q(frames=[FULL, greeks_frame()])),
    scenario("a_quote_from_the_order_book_without_greeks_has_no_oi", q(frames=[FULL])),
    scenario("a_quote_from_the_order_book_names_the_inst_id_when_there_is_no_ref_id", q(frames=[book_frame(ref=0, inst=3045)])),
    scenario("a_quote_whose_book_has_no_levels", q(frames=[book_frame(bids=[], asks=[])])),
    scenario("a_quote_with_an_index_price_of_zero_falls_to_the_book", q(frames=[index_frame(value=0), FULL])),
    scenario("a_quote_with_nothing_on_the_feed_asks_rest", q(rest=[rest_book()])),
    scenario("a_quote_rest_without_a_book", q(rest=[{"status": 200, "body": {"orderBook": {}}}])),
    scenario("a_quote_rest_without_levels", q(rest=[{"status": 200, "body": {"orderBook": {"ltp": 80545, "prev_close": 80000, "volume": 5}}}])),
    scenario("a_quote_rest_session_expired", q(rest=[{"status": 440, "body": {}}])),
    scenario("a_quote_rest_forbidden", q(rest=[{"status": 403, "body": {}}])),
    scenario("a_quote_rest_error_status_with_json", q(rest=[{"status": 500, "body": {"error": "down"}}])),
    scenario("a_quote_rest_text_that_is_not_json", q(rest=[{"status": 200, "body": "<html>"}])),
    scenario("a_quote_rest_throttled_then_read", q(rest=[{"status": 429, "body": {}}, rest_book()])),
    scenario("a_quote_rest_throttled_every_time", q(rest=[{"status": 429, "body": {}}] * 3)),
    scenario("a_quote_with_the_socket_closed_asks_rest", q(connect=False, rest=[rest_book()])),
    scenario("a_quote_with_the_socket_closed_and_rest_empty", q(connect=False, rest=[{"status": 200, "body": {"orderBook": {}}}])),
    scenario("an_index_quote_from_the_candle_bucket", q("NIFTY", "NSE_INDEX", frames=[bucket_frame()])),
    scenario("an_index_quote_from_the_candle_bucket_in_the_instruments_list", q("NIFTY", "NSE_INDEX", frames=[bucket_frame(instruments=True)])),
    scenario("an_index_quote_keeps_the_open_of_an_earlier_bucket", q("NIFTY", "NSE_INDEX", frames=[bucket_frame(o=2400000, c=2410025), bucket_frame(o=0, c=2411000, ts=1793700060)])),
    scenario("an_index_quote_prefers_the_index_message_for_the_price", q("NIFTY", "NSE_INDEX", frames=[index_frame("NIFTY 50", "NSE", 2412000, 2420000, 2390000), bucket_frame(c=2410025)])),
    scenario("an_index_quote_with_the_volume_of_a_bucket", q("NIFTY", "NSE_INDEX", frames=[bucket_frame(bucket_volume=0, cumulative=900)])),
    scenario("a_bse_index_quote", q("SENSEX", "BSE_INDEX", frames=[bucket_frame("SENSEX", "BSE", c=8010000)])),
    scenario("an_index_name_from_the_exchange_is_shortened", q("NIFTY", "NSE_INDEX", frames=[index_frame("NIFTY 50", "NSE", 2412000)])),
    scenario("an_index_quote_with_nothing_on_the_feed_is_zeros", q("NIFTY", "NSE_INDEX")),
    scenario("an_index_quote_with_the_socket_closed_is_zeros", q("NIFTY", "NSE_INDEX", connect=False)),
    scenario("a_depth_from_a_full_book", d(frames=[FULL, greeks_frame()])),
    scenario("a_depth_from_a_full_book_for_a_future", d("NIFTY25NOV26FUT", "NFO", frames=[book_frame(49500, 2410025, bids=[(2410000 - 5 * i, 75 * (i + 1), 1) for i in range(5)], asks=[(2410050 + 5 * i, 75 * (i + 1), 1) for i in range(5)]), greeks_frame(49500)])),
    scenario("a_depth_without_greeks_waits_then_takes_the_book", d(frames=[FULL])),
    scenario("a_depth_with_a_short_book_is_padded_to_five", d(frames=[book_frame(bids=[(80540, 100, 1)], asks=[(80550, 110, 1), (80555, 120, 2)]), greeks_frame()])),
    scenario("a_depth_whose_book_has_no_levels", d(frames=[book_frame(bids=[], asks=[]), greeks_frame()])),
    scenario("a_depth_with_a_zero_price_level_is_not_complete_but_is_used", d(frames=[book_frame(bids=[(0, 0, 0)] * 5), greeks_frame()])),
    scenario("a_depth_with_more_than_five_levels_keeps_five", d(frames=[book_frame(bids=[(80540 - 5 * i, 100, 1) for i in range(7)], asks=[(80550 + 5 * i, 100, 1) for i in range(7)]), greeks_frame()])),
    scenario("a_depth_with_a_price_of_zero_is_asked_of_rest", d(frames=[book_frame(ltp=0)], rest=[rest_book(levels=5)])),
    scenario("a_depth_with_nothing_on_the_feed_asks_rest", d(rest=[rest_book(levels=5, high=81000, low=79000, open=80100, oi=7, ltq=3)])),
    scenario("a_depth_rest_with_a_short_book", d(rest=[rest_book(levels=2)])),
    scenario("a_depth_rest_without_a_book", d(rest=[{"status": 200, "body": {"orderBook": {}}}])),
    scenario("a_depth_rest_session_expired", d(rest=[{"status": 440, "body": {}}])),
    scenario("a_depth_rest_text_that_is_not_json", d(rest=[{"status": 200, "body": "<html>"}])),
    scenario("a_depth_with_the_socket_closed_asks_rest", d(connect=False, rest=[rest_book(levels=5)])),
    scenario("a_depth_for_an_index_is_zeros", d("NIFTY", "NSE_INDEX")),
    scenario("a_depth_for_a_bse_index_is_zeros", d("SENSEX", "BSE_INDEX", frames=[FULL])),
    scenario("several_quotes_from_the_books", m([("SBIN", "NSE"), ("INFY", "NSE")], frames=[book_frame(3045), book_frame(1594, 150010), greeks_frame(3045), greeks_frame(1594, ltp=150010)])),
    scenario("several_quotes_one_book_never_completes_and_asks_rest", m([("SBIN", "NSE"), ("INFY", "NSE")], frames=[book_frame(3045), greeks_frame(3045)], rest=[rest_book()])),
    scenario("several_quotes_with_an_index", m([("SBIN", "NSE"), ("NIFTY", "NSE_INDEX")], frames=[book_frame(3045), greeks_frame(3045), bucket_frame()])),
    scenario("several_quotes_of_indexes_only", m([("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")], frames=[bucket_frame(), bucket_frame("SENSEX", "BSE", c=8010000)])),
    scenario("several_quotes_an_index_with_no_price_is_zeros", m([("NIFTY", "NSE_INDEX")])),
    scenario("several_quotes_nothing_arrives_and_rest_answers", m([("SBIN", "NSE")], rest=[rest_book()])),
    scenario("several_quotes_nothing_arrives_and_rest_is_empty", m([("SBIN", "NSE")], rest=[{"status": 200, "body": {"orderBook": {}}}])),
    scenario("several_quotes_with_the_socket_closed", m([("SBIN", "NSE")], connect=False, rest=[rest_book()])),
]
