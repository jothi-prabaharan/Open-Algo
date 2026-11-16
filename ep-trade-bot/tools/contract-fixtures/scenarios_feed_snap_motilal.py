"""Motilal depth and several quotes: nothing is asked for over REST. A feed socket is opened (wss://ws1feed.motilaloswal.com/jwebsocket/jwebsocket), a binary login packet carrying the client code is sent and the first frame back marks it signed in. Each scrip is then
registered with a 10-byte binary packet, the 30-byte packets that come back are read in turn (depth waits up to three seconds for the five depth levels, the day OHLC and LTP packets, and the open interest of a derivative; several quotes up to two to five seconds for what a quote
uses) and the scrips are unregistered. An index is registered with a JSON text frame, not a scrip packet. Run on the real BrokerData and the real socket client with a fake transport that hands back scripted frames, one for each time the client looks."""

import json
import struct
import sys
import threading
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

NOW_MS = 1_793_700_000_123
BROKER = "motilal"
AUTH_TOKEN = "motilal-auth-token:::motilal-access-token"
CLIENT_ID = "MO100"
ENV = {"BROKER_API_KEY": "motilal-api-key", "BROKER_API_SECRET": "motilal-api-secret"}


def row(symbol, brsymbol, exchange, brexchange, token, lot=1, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": str(token), "expiry": "", "strike": 0.0, "lotsize": lot, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN EQ", "NSE", "NSE", 3045), row("INFY", "INFY EQ", "NSE", "NSE", 1594), row("RELIANCE", "RELIANCE EQ", "NSE", "NSE", 2885), row("TATAMOTORS", "TATAMOTORS EQ", "BSE", "BSE", 500570),
    row("NIFTY25NOV26FUT", "NIFTY 25-Nov-2026", "NFO", "NSEFO", 49500, 75, "FUT"), row("SENSEX26NOVFUT", "SENSEX 26-Nov-2026", "BFO", "BSEFO", 60001, 20, "FUT"), row("USDINR25NOV26FUT", "USDINR 25-Nov-2026", "CDS", "NSECD", 90001, 1000, "FUT"),
    row("GOLDM25NOV26FUT", "GOLDM 25-Nov-2026", "MCX", "MCX", 80001, 10, "FUT"), row("NIFTY", "NIFTY", "NSE_INDEX", "NSE", 26000, 1, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", "BSE", 999912, 1, "INDEX"),
    row("CRUDEIDX", "CRUDEIDX", "MCX_INDEX", "MCX", 77, 1, "INDEX"), row("NOTOKEN", "NOTOKEN", "NSE", "NSE", 0),
]


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.motilal.api import baseurl
        from broker.motilal.api import data as data_module
        from broker.motilal.api import motilal_websocket as ws_module

        self.data, self.ws, self.baseurl = data_module, ws_module, baseurl

    def reset(self):
        self.baseurl.set_client_code(CLIENT_ID)
        with self.data._WS_REGISTRY_LOCK:
            self.data._WS_REGISTRY.clear()

    def _install(self, frames, connect):
        driver = self
        self.sent, self.queue, self.app, self.connected = [], (list(frames) if connect else []), None, connect
        state = {"now": 0.0}

        def deliver_next():
            if not driver.queue or driver.app is None:
                return
            item = driver.queue.pop(0)
            driver.app.on_message(driver.app, bytes.fromhex(item["hex"]))

        def clock():
            state["now"] += 0.05
            return state["now"]

        class FakeApp:
            def __init__(self, url, on_open=None, on_message=None, on_error=None, on_close=None, **kwargs):
                self.on_open, self.on_message = on_open, on_message
                driver.app = self

            def run_forever(self, *a, **k):
                if connect:
                    self.on_open(self)

            def send(self, data, opcode=None):
                driver.sent.append({"binary": bytes(data).hex()} if isinstance(data, (bytes, bytearray)) else {"text": data})

            def close(self):
                pass

        class SyncThread:
            def __init__(self, target=None, args=(), kwargs=None, name=None, daemon=None):
                self.target, self.args, self.kwargs = target, args, kwargs or {}
                self.daemon = daemon

            def start(self):
                self.target(*self.args, **self.kwargs)

            def is_alive(self):
                return False

            def join(self, *a, **k):
                pass

        ws = self.ws

        def one_attempt(this):
            this.ws = ws.websocket.WebSocketApp(this.ws_url, on_open=this.on_open, on_message=this.on_message, on_error=this.on_error, on_close=this.on_close)
            this.ws.run_forever()

        ws.MotilalWebSocket._connect_with_retry = one_attempt
        ws.MotilalWebSocket._start_heartbeat = lambda this: None
        ws.websocket.WebSocketApp = FakeApp
        ws.threading = types.SimpleNamespace(Thread=SyncThread, Lock=threading.Lock, RLock=threading.RLock, Event=threading.Event)
        self.data.time = types.SimpleNamespace(time=clock, monotonic=clock, sleep=lambda seconds: deliver_next())

    def _call(self, frames, connect, call):
        self.reset()
        self._install(frames, connect)
        out = {}
        try:
            out["result"] = call(self.data.BrokerData(AUTH_TOKEN))
        except Exception as e:
            out["error"] = str(e)
        if "error" in out and "WebSocket" in out["error"]:
            # OpenAlgo asks again up to three times (two connections each); EP Trade bot asks once, so only the first sign-in is recorded.
            self.sent = self.sent[:1]
        if not self.connected:
            # A socket that never opened sends nothing; OpenAlgo's closing logout notice is not part of what EP Trade bot does.
            self.sent = []
        out["sent"] = list(self.sent)
        return out

    def depth(self, step):
        return self._call(step.get("frames", []), step.get("connect", True), lambda bd: bd.get_depth(step["symbol"], step["exchange"]))

    def multi(self, step):
        return self._call(step.get("frames", []), step.get("connect", True), lambda bd: bd.get_multiquotes(step["symbols"]))


def h(data):
    return {"hex": data.hex()}


def packet(exchange, scrip, kind, body):
    assert len(body) == 20
    return exchange.encode() + struct.pack("<i", scrip) + struct.pack("<i", 1793699999) + kind.encode() + body


def pad(body):
    return body + b"\x00" * (20 - len(body))


def ltp(rate=805.45, ltq=25, cumulative=1234567, avg=804.1, oi=0):
    return pad(struct.pack("<fiifi", rate, ltq, cumulative, avg, oi))


def ohlc(o=801.0, hi=809.5, low=799.0, close=800.0):
    return pad(struct.pack("<ffff", o, hi, low, close))


def level(bid=805.4, bid_qty=100, bid_orders=3, offer=805.5, offer_qty=110, offer_orders=2):
    return struct.pack("<fihfih", bid, bid_qty, bid_orders, offer, offer_qty, offer_orders)


def oi_body(oi=7000, high=7500, low=6500):
    return pad(struct.pack("<iii", oi, high, low))


def index_body(rate=24100.25):
    return pad(struct.pack("<f", rate))


def dpr(upper=880.0, lower=720.0):
    return pad(struct.pack("<ff", upper, lower))


def book_packets(x, token, bids=None, asks=None):
    bids = bids if bids is not None else [(805.4 - 0.05 * i, 100 * (i + 1), 3 + i) for i in range(5)]
    asks = asks if asks is not None else [(805.5 + 0.05 * i, 110 * (i + 1), 2 + i) for i in range(5)]
    return b"".join(packet(x, token, "BCDEF"[i], level(*bids[i], *asks[i])) for i in range(min(len(bids), len(asks), 5)))


def whole(token=3045, x="N", with_oi=None, **kw):
    """Everything a read uses for a scrip, in one frame: the depth levels, the day OHLC and the LTP."""
    data = book_packets(x, token, kw.pop("bids", None), kw.pop("asks", None)) + packet(x, token, "G", ohlc(*kw.pop("ohlc", (801.0, 809.5, 799.0, 800.0)))) + packet(x, token, "A", ltp(**kw))
    if with_oi is not None:
        data += packet(x, token, "m", oi_body(*with_oi))
    return data


ACK = packet("N", 0, "1", pad(b""))


def d(symbol="SBIN", exchange="NSE", *frames, **extra):
    return {"depth": {"symbol": symbol, "exchange": exchange, "frames": [h(ACK)] + [h(f) for f in frames], **extra}}


def m(symbols, *frames, **extra):
    return {"multi": {"symbols": [{"symbol": s, "exchange": e} for s, e in symbols], "frames": [h(ACK)] + [h(f) for f in frames], **extra}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


SBIN, INFY, REL = ("SBIN", "NSE"), ("INFY", "NSE"), ("RELIANCE", "NSE")
SCENARIOS = [
    scenario("a_depth", d("SBIN", "NSE", whole())),
    scenario("a_depth_in_pieces", d("SBIN", "NSE", book_packets("N", 3045), packet("N", 3045, "G", ohlc()), packet("N", 3045, "A", ltp()))),
    scenario("a_depth_with_ltp_before_the_depth", d("SBIN", "NSE", packet("N", 3045, "A", ltp()), packet("N", 3045, "G", ohlc()), book_packets("N", 3045))),
    scenario("a_depth_whose_book_is_still_short_when_the_time_is_up", d("SBIN", "NSE", book_packets("N", 3045, [(805.4, 100, 3)] * 2, [(805.5, 110, 2)] * 2) + packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_with_a_gap_in_the_levels_is_squeezed", d("SBIN", "NSE", packet("N", 3045, "B", level()) + packet("N", 3045, "E", level(805.2, 400, 6, 805.7, 440, 5)) + packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_with_no_quote", d("SBIN", "NSE", book_packets("N", 3045))),
    scenario("a_depth_with_a_quote_and_no_depth", d("SBIN", "NSE", packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_for_a_future_waits_for_the_open_interest", d("NIFTY25NOV26FUT", "NFO", whole(49500, with_oi=(7000, 7500, 6500), rate=24010.5, oi=7000))),
    scenario("a_depth_for_a_future_whose_open_interest_never_comes", d("NIFTY25NOV26FUT", "NFO", whole(49500, rate=24010.5, oi=6500))),
    scenario("a_depth_for_a_future_open_interest_updates_the_quote", d("NIFTY25NOV26FUT", "NFO", whole(49500, rate=24010.5, oi=6500), packet("N", 49500, "m", oi_body(7100, 7500, 6500)))),
    scenario("a_depth_for_a_bse_equity", d("TATAMOTORS", "BSE", whole(500570, "B", rate=498.1))),
    scenario("a_depth_for_a_bse_future", d("SENSEX26NOVFUT", "BFO", whole(60001, "G", with_oi=(100, 200, 50), rate=80100.0))),
    scenario("a_depth_for_a_currency_future", d("USDINR25NOV26FUT", "CDS", whole(90001, "C", with_oi=(100, 200, 50), rate=83.5))),
    scenario("a_depth_for_a_commodity_future", d("GOLDM25NOV26FUT", "MCX", whole(80001, "M", with_oi=(100, 200, 50), rate=75000.0))),
    scenario("a_depth_the_second_time_starts_clean", d("SBIN", "NSE", whole()), d("INFY", "NSE", whole(1594, rate=1499.5))),
    scenario("a_depth_after_a_frame_for_another_scrip", d("SBIN", "NSE", whole(1594, rate=1499.5), whole())),
    scenario("a_depth_with_a_frame_cut_short", d("SBIN", "NSE", whole()[:70])),
    scenario("a_depth_with_two_frames_one_after_the_other_the_last_value_stands", d("SBIN", "NSE", packet("N", 3045, "A", ltp(805.45)), packet("N", 3045, "A", ltp(806.5)), whole(rate=807.25))),
    scenario("a_depth_with_a_heartbeat_and_an_unknown_packet", d("SBIN", "NSE", packet("N", 3045, "1", pad(b"")) + packet("N", 3045, "Z", pad(b"")), whole())),
    scenario("a_depth_with_circuit_limits", d("SBIN", "NSE", packet("N", 3045, "W", dpr()), whole())),
    scenario("a_depth_with_a_negative_quantity", d("SBIN", "NSE", whole(bids=[(805.4, -100, 3)] * 5))),
    scenario("a_depth_with_every_value_zero", d("SBIN", "NSE", whole(bids=[(0.0, 0, 0)] * 5, asks=[(0.0, 0, 0)] * 5, ohlc=(0.0, 0.0, 0.0, 0.0), rate=0.0, ltq=0, cumulative=0, avg=0.0))),
    scenario("a_depth_with_prices_that_need_rounding", d("SBIN", "NSE", whole(bids=[(805.4567, 100, 3)] * 5, asks=[(805.5123, 110, 2)] * 5, rate=805.455))),
    scenario("a_depth_for_a_nse_index", d("NIFTY", "NSE_INDEX", packet("N", 26000, "H", index_body()))),
    scenario("a_depth_for_a_bse_index", d("SENSEX", "BSE_INDEX", packet("B", 999912, "H", index_body(80100.5)))),
    scenario("a_depth_for_an_index_with_the_day_values", d("NIFTY", "NSE_INDEX", packet("N", 26000, "H", index_body()) + packet("N", 26000, "G", ohlc(24000.5, 24150.25, 23950.0, 24010.75)))),
    scenario("a_depth_for_an_index_that_sends_nothing", d("NIFTY", "NSE_INDEX")),
    scenario("a_depth_for_an_index_with_a_zero_value", d("NIFTY", "NSE_INDEX", packet("N", 26000, "H", index_body(0.0)))),
    scenario("a_depth_for_an_mcx_index_is_refused", d("CRUDEIDX", "MCX_INDEX")),
    scenario("a_depth_nothing_arrives_after_the_sign_in", d("SBIN", "NSE")),
    scenario("a_depth_the_sign_in_never_comes", {"depth": {"symbol": "SBIN", "exchange": "NSE", "frames": []}}),
    scenario("a_depth_the_socket_does_not_open", {"depth": {"symbol": "SBIN", "exchange": "NSE", "frames": [h(ACK), h(whole())], "connect": False}}),
    scenario("several_quotes", m([SBIN, INFY, REL], whole(), whole(1594, rate=1499.5), whole(2885, rate=2900.0))),
    scenario("several_quotes_in_the_other_order", m([SBIN, INFY], whole(1594, rate=1499.5), whole())),
    scenario("several_quotes_one_never_comes", m([SBIN, INFY], whole())),
    scenario("several_quotes_none_come", m([SBIN, INFY])),
    scenario("several_quotes_with_only_level_one_of_the_book", m([SBIN], book_packets("N", 3045, [(805.4, 100, 3)], [(805.5, 110, 2)]) + packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("several_quotes_with_a_level_one_without_a_price_waits_for_the_book", m([SBIN], packet("N", 3045, "B", level(0.0, 0, 0, 805.5, 110, 2)) + packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()), book_packets("N", 3045))),
    scenario("several_quotes_the_best_price_is_the_first_level_that_has_one", m([SBIN], packet("N", 3045, "B", level(0.0, 0, 0, 0.0, 0, 0)) + packet("N", 3045, "C", level(805.3, 90, 2, 805.6, 95, 1)) + packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("several_quotes_with_no_depth_at_all", m([SBIN], packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("several_quotes_a_frame_that_is_not_asked_for", m([SBIN], whole(9999, rate=1500.0), whole())),
    scenario("several_quotes_the_same_symbol_twice", m([SBIN, SBIN], whole())),
    scenario("several_quotes_across_exchanges", m([SBIN, ("NIFTY25NOV26FUT", "NFO"), ("TATAMOTORS", "BSE"), ("SENSEX26NOVFUT", "BFO"), ("USDINR25NOV26FUT", "CDS"), ("GOLDM25NOV26FUT", "MCX")],
                                                  whole(), whole(49500, with_oi=(7000, 7500, 6500), rate=24010.5, oi=7000), whole(500570, "B", rate=498.1), whole(60001, "G", with_oi=(100, 200, 50), rate=80100.0),
                                                  whole(90001, "C", with_oi=(100, 200, 50), rate=83.5), whole(80001, "M", with_oi=(100, 200, 50), rate=75000.0))),
    scenario("several_quotes_a_future_without_its_open_interest_waits_the_whole_time", m([("NIFTY25NOV26FUT", "NFO")], whole(49500, rate=24010.5, oi=6500))),
    scenario("several_quotes_an_index", m([("NIFTY", "NSE_INDEX")], packet("N", 26000, "H", index_body()) + packet("N", 26000, "G", ohlc(24000.5, 24150.25, 23950.0, 24010.75)))),
    scenario("several_quotes_an_index_with_no_value", m([("NIFTY", "NSE_INDEX")])),
    scenario("several_quotes_an_index_with_only_the_day_values", m([("NIFTY", "NSE_INDEX")], packet("N", 26000, "G", ohlc(24000.5, 24150.25, 23950.0, 24010.75)))),
    scenario("several_quotes_two_indexes_on_one_exchange", m([("NIFTY", "NSE_INDEX"), ("NIFTY", "NSE_INDEX")], packet("N", 26000, "H", index_body()))),
    scenario("several_quotes_two_indexes_on_two_exchanges", m([("NIFTY", "NSE_INDEX"), ("SENSEX", "BSE_INDEX")], packet("N", 26000, "H", index_body()), packet("B", 999912, "H", index_body(80100.5)))),
    scenario("several_quotes_an_index_and_a_share_wait_the_whole_time", m([("NIFTY", "NSE_INDEX"), SBIN], packet("N", 26000, "H", index_body()), whole())),
    scenario("several_quotes_an_mcx_index_is_skipped", m([("CRUDEIDX", "MCX_INDEX"), SBIN], whole())),
    scenario("several_quotes_only_an_mcx_index", m([("CRUDEIDX", "MCX_INDEX")])),
    scenario("several_quotes_a_scrip_that_sends_nothing_is_no_data", m([("NOTOKEN", "NSE"), SBIN], whole())),
    scenario("several_quotes_only_a_scrip_that_sends_nothing", m([("NOTOKEN", "NSE")])),
    scenario("several_quotes_the_sign_in_never_comes", {"multi": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}], "frames": []}}),
    scenario("several_quotes_the_socket_does_not_open", {"multi": {"symbols": [{"symbol": "SBIN", "exchange": "NSE"}], "frames": [h(ACK), h(whole())], "connect": False}}),
    scenario("several_quotes_a_single_symbol", m([SBIN], whole())),
    scenario("several_quotes_with_every_value_zero", m([SBIN], whole(bids=[(0.0, 0, 0)] * 5, asks=[(0.0, 0, 0)] * 5, ohlc=(0.0, 0.0, 0.0, 0.0), rate=0.0, ltq=0, cumulative=0, avg=0.0))),
    scenario("several_quotes_volume_and_open_interest_are_whole_numbers", m([("NIFTY25NOV26FUT", "NFO")], whole(49500, with_oi=(7000, 7500, 6500), rate=24010.5, cumulative=4_000_000, oi=7000))),
]
