"""Pocketful quotes, depth and several quotes: nothing is asked for over REST. A temporary feed socket is opened at wss://trade.pocketful.in/ws/v1/feeds with the client id and the token in the address, a subscribe frame goes out for each instrument (mode
marketdata for a quote, full_snapquote for depth), the packets that come back are read in turn (a quote waits up to ten seconds, depth fifteen, several quotes up to half a second each, three to fifteen seconds in all), and an unsubscribe goes out for each at the end.
Run on the real BrokerData and the real socket client, with a fake transport that hands back scripted frames, one for each time the client looks."""

import json
import struct
import sys
import threading
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

NOW_MS = 1_793_700_000_123
BROKER = "pocketful"
AUTH_TOKEN = "pocketful-access-token"
CLIENT_ID = "PF100"
ENV = {"BROKER_API_KEY": "pf-client-key", "BROKER_API_SECRET": "pf-client-secret"}


def row(symbol, brsymbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": 0.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN-EQ", "NSE", 3045), row("INFY", "INFY-EQ", "NSE", 1594), row("RELIANCE", "RELIANCE-EQ", "NSE", 2885), row("TATAMOTORS", "TATAMOTORS", "BSE", 500570),
    row("NIFTY25NOV26FUT", "NIFTY26NOVFUT", "NFO", 49500, "FUT"), row("SENSEX26NOVFUT", "SENSEX26NOVFUT", "BFO", 60001, "FUT"), row("USDINR25NOV26FUT", "USDINR26NOVFUT", "CDS", 90001, "FUT"),
    row("GOLDM25NOV26FUT", "GOLDM26NOVFUT", "MCX", 80001, "FUT"), row("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", 19000, "INDEX"),
    row("CRUDEOIL25NOV26FUT", "CRUDEOIL26NOVFUT", "NCO", 70001, "FUT"),
] + [row(f"T{i:03d}", f"T{i:03d}-EQ", "NSE", 100000 + i) for i in range(60)]


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.pocketful.api import data as data_module
        from broker.pocketful.api import pocketfulwebsocket as ws_module

        self.data, self.ws = data_module, ws_module
        self.real_time = data_module.time

    def reset(self):
        ws = self.ws
        ws.websock, ws.ws_connected, ws._connect_attempt = None, False, None
        ws.snapquote_marketdata_response = ws.compact_marketdata_response = ws.detailed_marketdata_response = {}
        ws.order_update_response = ws.trade_update_response = {}
        for name in ("dtlmktdata_dict", "cmptmktdata_dict", "snpqtdata_dict", "_subscribers"):
            getattr(ws, name).clear()

    def _install(self, frames, connect):
        driver = self
        self.sent, self.connections, self.queue = [], [], list(frames)
        state = {"now": 0.0}

        def deliver_next():
            if not driver.queue:
                return
            item = driver.queue.pop(0)
            if "hex" in item:
                driver.ws.on_message(None, bytes.fromhex(item["hex"]))
            else:
                driver.ws.on_message(None, item["text"])

        def clock():
            state["now"] += 0.05
            return state["now"]

        class FakeApp:
            on_open = None

            def __init__(self, url, on_message=None, on_error=None, on_close=None, **kwargs):
                driver.connections.append(url)
                self.sock = types.SimpleNamespace(connected=True)
                self.keep_running = True

            def run_forever(self, *a, **k):
                if connect:
                    self.on_open(self)

            def send(self, text):
                driver.sent.append(json.loads(text))

            def close(self):
                pass

        class SyncThread:
            def __init__(self, target=None, args=(), kwargs=None, name=None, daemon=None):
                self.target, self.args, self.kwargs = target, args, kwargs or {}

            def start(self):
                self.target(*self.args, **self.kwargs)

        self.ws.websocket.WebSocketApp = FakeApp
        self.ws.heartbeat_thread = lambda client_socket: None
        self.ws.threading = types.SimpleNamespace(Thread=SyncThread, Lock=threading.Lock, RLock=threading.RLock, Event=threading.Event)
        self.data.time = types.SimpleNamespace(time=clock, monotonic=clock, sleep=lambda seconds: deliver_next())

    def _call(self, frames, connect, call):
        self.reset()
        self._install(frames, connect)
        out = {}
        try:
            bd = self.data.BrokerData(AUTH_TOKEN)
            bd.client_id = CLIENT_ID
            out["result"] = call(bd)
        except Exception as e:
            out["error"] = str(e)
        out["sent"] = list(self.sent)
        out["connections"] = list(self.connections)
        return out

    def quote(self, step):
        return self._call(step.get("frames", []), step.get("connect", True), lambda bd: bd.get_quotes(step["symbol"], step["exchange"]))

    def depth(self, step):
        return self._call(step.get("frames", []), step.get("connect", True), lambda bd: bd.get_depth(step["symbol"], step["exchange"]))

    def multi(self, step):
        return self._call(step.get("frames", []), step.get("connect", True), lambda bd: bd.get_multiquotes(step["symbols"]))


def h(data):
    return {"hex": data.hex()}


T = {"text": "ok"}


def detailed(token=3045, exchange=1, ltp=80545, ltt=1793699999, ltq=25, volume=1234567, bid=80540, bidq=100, ask=80550, askq=110, buy=45000, sell=52000, avg=80410, exts=1793699999, o=80100, hi=80950, low=79900, close=80000, yh=90000, yl=70000, lo_dpr=72000,
             hi_dpr=88000, oi=0, initial_oi=0, mode=1):
    return struct.pack(">bbIIIIIIIIIQQIIIIIIIIIIII", mode, exchange, token, ltp, ltt, ltq, volume, bid, bidq, ask, askq, buy, sell, avg, exts, o, hi, low, close, yh, yl, lo_dpr, hi_dpr, oi, initial_oi)


def snap(token=3045, exchange=1, bids=None, asks=None, avg=80410, o=80100, hi=80950, low=79900, close=80000, buy=45000, sell=52000, volume=1234567, mode=4):
    bids = bids if bids is not None else [(3, 80540 - 5 * i, 100 * (i + 1)) for i in range(5)]
    asks = asks if asks is not None else [(2, 80550 + 5 * i, 110 * (i + 1)) for i in range(5)]

    def five(rows, at):
        rows = list(rows) + [(0, 0, 0)] * (5 - len(rows))
        return [r[at] for r in rows[:5]]

    return struct.pack(">bbI" + "I" * 5 * 6 + "IIIIIQQI", mode, exchange, token, *five(bids, 0), *five(bids, 1), *five(bids, 2), *five(asks, 0), *five(asks, 1), *five(asks, 2), avg, o, hi, low, close, buy, sell, volume)


def compact(token=3045, exchange=1):
    return struct.pack(">bbIIIIIIIIII", 2, exchange, token, 80545, 5, 1793699999, 72000, 88000, 0, 0, 80540, 80550)


def q(symbol="SBIN", exchange="NSE", *frames, **extra):
    return {"quote": {"symbol": symbol, "exchange": exchange, "frames": list(frames), **extra}}


def d(symbol="SBIN", exchange="NSE", *frames, **extra):
    return {"depth": {"symbol": symbol, "exchange": exchange, "frames": list(frames), **extra}}


def m(symbols, *frames, **extra):
    return {"multi": {"symbols": [{"symbol": s, "exchange": e} for s, e in symbols], "frames": list(frames), **extra}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


SBIN, INFY, REL = ("SBIN", "NSE"), ("INFY", "NSE"), ("RELIANCE", "NSE")
SCENARIOS = [
    scenario("a_quote", q("SBIN", "NSE", h(detailed()))),
    scenario("a_quote_after_a_text_frame", q("SBIN", "NSE", T, h(detailed()))),
    scenario("a_quote_after_a_frame_for_another_token", q("SBIN", "NSE", h(detailed(1594, ltp=150010)), h(detailed()))),
    scenario("a_quote_for_another_token_alone_is_the_answer_at_the_end", q("SBIN", "NSE", h(detailed(1594, ltp=150010)))),
    scenario("a_quote_the_other_modes_do_not_replace", q("SBIN", "NSE", h(detailed()), h(compact()), h(snap()))),
    scenario("a_quote_whose_frame_is_cut_short_wipes_the_last_one", q("SBIN", "NSE", h(detailed()), h(detailed()[:50]))),
    scenario("a_quote_that_is_a_cut_short_frame_alone", q("SBIN", "NSE", h(detailed()[:50]))),
    scenario("a_quote_of_a_text_frame_in_json_wipes_the_last_one", q("SBIN", "NSE", h(detailed()), {"text": json.dumps({"mode": 1, "x": 1})})),
    scenario("a_quote_of_a_text_frame_without_a_mode_is_ignored", q("SBIN", "NSE", h(detailed()), {"text": json.dumps({"x": 1})})),
    scenario("a_quote_with_every_value_zero", q("SBIN", "NSE", h(detailed(ltp=0, volume=0, bid=0, ask=0, hi=0, low=0, o=0, close=0, oi=0)))),
    scenario("a_quote_without_a_close_has_no_change", q("SBIN", "NSE", h(detailed(close=0)))),
    scenario("a_quote_below_the_close_has_a_negative_change", q("SBIN", "NSE", h(detailed(ltp=79000)))),
    scenario("a_quote_with_open_interest", q("NIFTY25NOV26FUT", "NFO", h(detailed(49500, 2, ltp=2410025, oi=7000000)))),
    scenario("a_bse_equity", q("TATAMOTORS", "BSE", h(detailed(500570, 6)))),
    scenario("a_bse_future", q("SENSEX26NOVFUT", "BFO", h(detailed(60001, 7)))),
    scenario("a_currency_future", q("USDINR25NOV26FUT", "CDS", h(detailed(90001, 3)))),
    scenario("a_commodity_future", q("GOLDM25NOV26FUT", "MCX", h(detailed(80001, 4)))),
    scenario("an_nse_index", q("NIFTY", "NSE_INDEX", h(detailed(26000)))),
    scenario("a_bse_index", q("SENSEX", "BSE_INDEX", h(detailed(19000, 6)))),
    scenario("an_exchange_with_no_code_is_asked_for_as_nse", q("CRUDEOIL25NOV26FUT", "NCO", h(detailed(70001)))),
    scenario("nothing_arrives", q("SBIN", "NSE")),
    scenario("only_text_arrives", q("SBIN", "NSE", T, T, T)),
    scenario("the_socket_does_not_open", q("SBIN", "NSE", h(detailed()), connect=False)),
    scenario("a_large_volume", q("SBIN", "NSE", h(detailed(volume=4_000_000_000)))),
    scenario("a_quote_whose_first_frame_is_cut_short_then_a_good_one", q("SBIN", "NSE", h(detailed()[:50]), h(detailed()))),
    scenario("a_quote_whose_first_frame_is_a_text_in_json_then_a_good_one", q("SBIN", "NSE", {"text": json.dumps({"mode": 1})}, h(detailed()))),
    scenario("a_depth_with_seven_levels_in_text_keeps_five", d("SBIN", "NSE", {"text": json.dumps({"mode": 4, "instrument_token": 3045, "exchange_code": 1, "bidPrices": [1, 2, 3, 4, 5, 6, 7], "bidQtys": [1, 2, 3, 4, 5, 6, 7], "buyers": [1, 2, 3, 4, 5, 6, 7],
                                                                                          "askPrices": [1, 2, 3, 4, 5, 6, 7], "askQtys": [1, 2, 3, 4, 5, 6, 7], "sellers": [1, 2, 3, 4, 5, 6, 7]})})),
    scenario("a_depth_with_fewer_quantities_than_prices", d("SBIN", "NSE", {"text": json.dumps({"mode": 4, "instrument_token": 3045, "exchange_code": 1, "bidPrices": [100, 200, 300], "bidQtys": [1], "buyers": [], "askPrices": [100], "askQtys": [], "sellers": [5]})})),
    scenario("several_quotes_in_two_batches", m([(f"T{i:03d}", "NSE") for i in range(51)], *[h(detailed(100000 + i, ltp=1000 + i)) for i in (0, 50)])),
    scenario("several_quotes_in_one_batch_of_fifty", m([(f"T{i:03d}", "NSE") for i in range(50)], *[h(detailed(100000 + i, ltp=1000 + i)) for i in (0, 49)])),
    scenario("a_quote_asked_for_twice", q("SBIN", "NSE", h(detailed())), q("INFY", "NSE", h(detailed(1594, ltp=150010)))),
    scenario("a_depth", d("SBIN", "NSE", h(snap()))),
    scenario("a_depth_after_a_frame_for_another_token", d("SBIN", "NSE", h(snap(1594, avg=150010)), h(snap()))),
    scenario("a_depth_for_another_token_alone_is_the_answer_at_the_end", d("SBIN", "NSE", h(snap(1594, avg=150010)))),
    scenario("a_depth_the_other_modes_do_not_replace", d("SBIN", "NSE", h(snap()), h(detailed()), h(compact()))),
    scenario("a_depth_after_a_text_frame", d("SBIN", "NSE", T, h(snap()))),
    scenario("a_depth_in_a_text_frame", d("SBIN", "NSE", {"text": json.dumps({"mode": 4, "instrument_token": 3045, "exchange_code": 1, "bidPrices": [80540, 80535], "bidQtys": [100, 200], "buyers": [1, 2], "askPrices": [80550], "askQtys": [110], "sellers": [3],
                                                                         "averageTradePrice": 80410, "open": 80100, "high": 80950, "low": 79900, "close": 80000, "totalBuyQty": 45000, "totalSellQty": 52000, "volume": 99})})),
    scenario("a_depth_in_a_text_frame_nested_in_d", d("SBIN", "NSE", {"text": json.dumps({"mode": 4, "d": {"instrumentToken": 3045, "exchangeCode": 1, "bidPrices": [80540], "bidQtys": [100], "buyers": [1], "askPrices": [], "askQtys": [], "sellers": []}})})),
    scenario("a_depth_with_a_short_ladder_is_padded_to_five", d("SBIN", "NSE", h(snap(bids=[(1, 80540, 100)], asks=[(1, 80550, 110), (2, 80555, 120)])))),
    scenario("a_depth_with_an_empty_ladder", d("SBIN", "NSE", h(snap(bids=[], asks=[])))),
    scenario("a_depth_with_a_zero_price_level", d("SBIN", "NSE", h(snap(bids=[(1, 0, 100)] * 5)))),
    scenario("a_depth_with_every_value_zero", d("SBIN", "NSE", h(snap(avg=0, o=0, hi=0, low=0, close=0, buy=0, sell=0, volume=0, bids=[], asks=[])))),
    scenario("a_depth_for_a_future", d("NIFTY25NOV26FUT", "NFO", h(snap(49500, 2, avg=2410025)))),
    scenario("a_depth_for_a_bse_equity", d("TATAMOTORS", "BSE", h(snap(500570, 6)))),
    scenario("a_depth_for_an_index", d("NIFTY", "NSE_INDEX", h(snap(26000)))),
    scenario("a_depth_that_is_cut_short_is_no_depth", d("SBIN", "NSE", h(snap()[:100]))),
    scenario("a_depth_nothing_arrives", d("SBIN", "NSE")),
    scenario("a_depth_the_socket_does_not_open", d("SBIN", "NSE", h(snap()), connect=False)),
    scenario("a_depth_asked_for_twice", d("SBIN", "NSE", h(snap())), d("INFY", "NSE", h(snap(1594, avg=150010)))),
    scenario("several_quotes", m([SBIN, INFY, REL], h(detailed()), h(detailed(1594, ltp=150010)), h(detailed(2885, ltp=290000)))),
    scenario("several_quotes_in_the_other_order", m([SBIN, INFY], h(detailed(1594, ltp=150010)), h(detailed()))),
    scenario("several_quotes_one_never_comes", m([SBIN, INFY], h(detailed()))),
    scenario("several_quotes_none_come", m([SBIN, INFY])),
    scenario("several_quotes_a_frame_that_is_not_asked_for", m([SBIN], h(detailed(9999, ltp=150010)), h(detailed()))),
    scenario("several_quotes_the_same_token_twice_over_one_frame", m([SBIN, SBIN], h(detailed()))),
    scenario("several_quotes_a_text_frame_among_them", m([SBIN, INFY], T, h(detailed()), h(detailed(1594)))),
    scenario("several_quotes_a_frame_cut_short", m([SBIN, INFY], h(detailed()), h(detailed(1594)[:40]))),
    scenario("several_quotes_across_exchanges", m([SBIN, ("NIFTY25NOV26FUT", "NFO"), ("TATAMOTORS", "BSE"), ("NIFTY", "NSE_INDEX")], h(detailed()), h(detailed(49500, 2, oi=100)), h(detailed(500570, 6)), h(detailed(26000)))),
    scenario("several_quotes_the_same_token_on_two_exchanges", m([SBIN, ("TATAMOTORS", "BSE")], h(detailed()), h(detailed(500570, 6)))),
    scenario("several_quotes_the_socket_does_not_open", m([SBIN, INFY], h(detailed()), connect=False)),
    scenario("several_quotes_a_single_symbol", m([SBIN], h(detailed()))),
    scenario("several_quotes_with_every_value_zero", m([SBIN], h(detailed(ltp=0, volume=0, bid=0, ask=0, hi=0, low=0, o=0, close=0)))),
]
