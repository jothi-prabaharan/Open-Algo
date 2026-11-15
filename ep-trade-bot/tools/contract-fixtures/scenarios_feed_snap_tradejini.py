"""Tradejini quotes, depth and several quotes: nothing is asked for over REST. A short-lived stream connection is opened with the token, a list is subscribed (L1 for quotes, L5 for depth) and what arrives is merged into a quote or kept as a depth, and the connection is closed. Run on the real BrokerData and
the real stream client, with the network replaced by scripted frames that each subscribe request is answered with."""

import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from scenarios_feed_tradejini import ENV, SYMBOLS as BASE_SYMBOLS, frame, l1, l5, packet, row  # noqa: E402,F401

NOW_MS = 1_793_700_000_123
BROKER = "tradejini"
AUTH_TOKEN = "tj-access-token"
SYMBOLS = list(BASE_SYMBOLS) + [row(f"BULK{i}", f"EQT_BULK{i}_EQ_NSE", "NSE", 800000 + i) for i in range(105)]


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.tradejini.api import data as data_module
        from broker.tradejini.api.nxtradstream import NxtradStream

        driver = self
        ticks = [0.0]

        def monotonic():
            ticks[0] += 0.05
            return ticks[0]

        time.monotonic = monotonic

        class Scripted(NxtradStream):
            def connect(self, token):
                driver.connections.append(token)
                if driver.connect_fails:
                    self.isConnected = False
                    self._callback(self.connect_cb, self, {"s": "error", "reason": "refused"})
                else:
                    self.isConnected = True
                    self._callback(self.connect_cb, self, {"s": "connected"})

            def _deliver(self, kind, tokens):
                driver.sent.append({"type": kind, "tokens": tokens})
                for text in driver.script.pop(0) if driver.script else []:
                    self._NxtradStream__on_message(None, bytes.fromhex(text))
                return True

            def subscribeL1(self, tokens):
                return self._deliver("L1", tokens)

            def subscribeL2(self, tokens):
                return self._deliver("L5", tokens)

            def disconnect(self):
                self.isConnected = False

        self.data, self.scripted = data_module, Scripted

    def reset(self):
        self.sent, self.connections, self.script, self.connect_fails = [], [], [], False

    def _run(self, step, call):
        self.script = [list(attempt) for attempt in step.get("attempts", [])]
        self.connect_fails = step.get("connect_fails", False)
        self.data.NxtradStream = self.scripted
        out = {}
        try:
            out["result"] = call(self.data.BrokerData(AUTH_TOKEN))
        except Exception as e:  # noqa: BLE001
            out["error"] = str(e)
        out.update({"sent": list(self.sent), "connections": list(self.connections)})
        self.sent.clear()
        self.connections.clear()
        return out

    def quote(self, step):
        return self._run(step, lambda bd: bd.get_quotes(step["symbol"], step["exchange"]))

    def depth(self, step):
        return self._run(step, lambda bd: bd.get_depth(step["symbol"], step["exchange"]))

    def multiquotes(self, step):
        return self._run(step, lambda bd: bd.get_multiquotes(step["symbols"]))


def h(data):
    return data.hex()


def q(symbol="SBIN", exchange="NSE", *attempts, **extra):
    return {"quote": {"symbol": symbol, "exchange": exchange, "attempts": [list(a) for a in attempts], **extra}}


def d(symbol="SBIN", exchange="NSE", *attempts, **extra):
    return {"depth": {"symbol": symbol, "exchange": exchange, "attempts": [list(a) for a in attempts], **extra}}


def m(symbols, *attempts, **extra):
    return {"multiquotes": {"symbols": symbols, "attempts": [list(a) for a in attempts], **extra}}


def pair(symbol, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


FULL = h(frame(l1()))
PART = h(frame(packet(10, (26, 1), (27, 3045), (29, 80545), (30, 80100))))
REST = h(frame(packet(10, (26, 1), (27, 3045), (31, 80950), (32, 79900), (33, 80000), (40, 1234567), (49, 80540), (52, 80550))))
BULK = [pair(f"BULK{i}") for i in range(105)]

SCENARIOS = [
    # ---- one quote ----------------------------------------------------------------------------------------------
    scenario("a_quote", q("SBIN", "NSE", [FULL])),
    scenario("a_quote_that_arrives_in_two_packets", q("SBIN", "NSE", [PART, REST])),
    scenario("a_quote_that_is_missing_a_field_is_returned_as_it_stands", q("SBIN", "NSE", [PART])),
    scenario("a_quote_missing_the_volume", q("SBIN", "NSE", [h(frame(packet(10, (26, 1), (27, 3045), (29, 80545), (30, 80100), (31, 80950), (32, 79900), (33, 80000), (49, 80540), (52, 80550))))])),
    scenario("a_future_quote_waits_for_the_open_interest", q("NIFTY25NOV26FUT", "NFO", [h(frame(l1(49500, 3, extra={44: 7000000})))])),
    scenario("a_future_quote_without_the_open_interest_is_returned_as_it_stands", q("NIFTY25NOV26FUT", "NFO", [h(frame(packet(10, (26, 3), (27, 49500), (29, 2410025), (30, 2400000), (31, 2420000), (32, 2395000), (33, 2399000), (40, 100), (49, 2410000), (52, 2410050))))])),
    scenario("a_commodity_quote", q("GOLDM25NOV26FUT", "MCX", [h(frame(l1(80001, 8, extra={44: 500})))])),
    scenario("a_currency_quote", q("USDINR25NOV26FUT", "CDS", [h(frame(l1(90001, 5, extra={29: 835000000, 49: 834900000, 52: 835100000, 44: 10})))])),
    scenario("a_bse_quote", q("TATAMOTORS", "BSE", [h(frame(l1(500570, 2)))])),
    scenario("an_index_quote_asks_for_the_cash_exchange", q("NIFTY", "NSE_INDEX", [h(frame(l1(26000, 1)))])),
    scenario("a_bse_index_quote", q("SENSEX", "BSE_INDEX", [h(frame(l1(19000, 2)))])),
    scenario("a_quote_with_decimals_in_the_price", q("SBIN", "NSE", [h(frame(l1(extra={29: 80547, 49: 80541, 52: 80553})))])),
    scenario("a_quote_with_zero_prices", q("SBIN", "NSE", [h(frame(l1(extra={29: 0, 30: 0, 31: 0, 32: 0, 33: 0, 49: 0, 52: 0, 40: 0})))])),
    scenario("a_quote_whose_packet_is_for_another_token_is_all_zeros", q("SBIN", "NSE", [h(frame(l1(1594)))])),
    scenario("a_quote_with_no_answer_is_all_zeros", q("SBIN", "NSE")),
    scenario("a_quote_whose_connection_is_refused_is_all_zeros", q("SBIN", "NSE", [FULL], connect_fails=True)),
    scenario("a_quote_frame_that_cannot_be_read_is_all_zeros", q("SBIN", "NSE", [h(frame(packet(10, (26, 77), (27, 3045), (29, 1))))])),
    scenario("a_quote_the_server_sent_as_depth_only_is_all_zeros", q("SBIN", "NSE", [h(frame(l5()))])),
    # ---- depth --------------------------------------------------------------------------------------------------
    scenario("a_depth", d("SBIN", "NSE", [h(frame(l5()))])),
    scenario("a_depth_with_two_levels_is_padded", d("SBIN", "NSE", [h(frame(l5(bids=[(80540, 100, 1), (80535, 200, 2)], asks=[(80550, 110, 2)], count=2)))])),
    scenario("a_depth_with_seven_levels_keeps_five", d("SBIN", "NSE", [h(frame(l5(bids=[(80540 - i, 100 + i, 1) for i in range(7)], asks=[(80550 + i, 110 + i, 2) for i in range(7)], count=7)))])),
    scenario("a_depth_with_no_levels", d("SBIN", "NSE", [h(frame(l5(bids=[], asks=[], count=0)))])),
    scenario("a_depth_with_a_count_of_zero_but_levels", d("SBIN", "NSE", [h(frame(l5(count=0)))])),
    scenario("a_depth_of_a_future", d("NIFTY25NOV26FUT", "NFO", [h(frame(l5(49500, 3, total_buy=0, total_sell=0)))])),
    scenario("a_depth_of_currency", d("USDINR25NOV26FUT", "CDS", [h(frame(l5(90001, 5, bids=[(834900000, 1000, 1)], asks=[(835100000, 1000, 1)], count=1)))])),
    scenario("a_depth_of_an_index_asks_for_the_cash_exchange", d("NIFTY", "NSE_INDEX", [h(frame(l5(26000, 1)))])),
    scenario("a_depth_of_a_bse_scrip", d("TATAMOTORS", "BSE", [h(frame(l5(500570, 2)))])),
    scenario("a_depth_of_another_token_is_still_the_answer", d("SBIN", "NSE", [h(frame(l5(1594)))])),
    scenario("a_depth_with_zero_prices", d("SBIN", "NSE", [h(frame(l5(bids=[(0, 0, 0)] * 5, asks=[(0, 0, 0)] * 5)))])),
    scenario("a_depth_with_no_answer_is_all_zeros", d("SBIN", "NSE")),
    scenario("a_depth_whose_connection_is_refused_is_all_zeros", d("SBIN", "NSE", [h(frame(l5()))], connect_fails=True)),
    scenario("a_depth_that_arrives_after_a_quote_packet", d("SBIN", "NSE", [h(frame(l1(), l5()))])),
    scenario("a_depth_frame_with_only_a_quote_is_all_zeros", d("SBIN", "NSE", [h(frame(l1()))])),
    scenario("a_depth_frame_that_cannot_be_read_is_all_zeros", d("SBIN", "NSE", [h(frame(packet(11, (26, 77), (27, 3045))))])),
    # ---- several quotes -----------------------------------------------------------------------------------------
    scenario("several_quotes", m([pair("SBIN"), pair("INFY")], [h(frame(l1(3045), l1(1594, 1, extra={29: 150010})))])),
    scenario("several_quotes_in_two_frames", m([pair("SBIN"), pair("INFY")], [h(frame(l1(3045))), h(frame(l1(1594, 1, extra={29: 150010})))])),
    scenario("several_quotes_one_missing", m([pair("SBIN"), pair("INFY")], [h(frame(l1(3045)))])),
    scenario("several_quotes_one_partial", m([pair("SBIN"), pair("INFY")], [h(frame(l1(3045), packet(10, (26, 1), (27, 1594), (29, 150010))))])),
    scenario("several_quotes_on_several_exchanges", m([pair("SBIN"), pair("NIFTY25NOV26FUT", "NFO"), pair("TATAMOTORS", "BSE"), pair("NIFTY", "NSE_INDEX")], [h(frame(l1(3045), l1(49500, 3, extra={44: 7}), l1(500570, 2), l1(26000, 1)))])),
    scenario("several_quotes_with_nothing_arriving", m([pair("SBIN"), pair("INFY")])),
    scenario("several_quotes_refused_connection_is_an_error", m([pair("SBIN"), pair("INFY")], [h(frame(l1(3045)))], connect_fails=True)),
    scenario("several_quotes_with_a_symbol_the_master_does_not_know", m([pair("SBIN"), pair("NOSUCH")], [h(frame(l1(3045)))])),
    scenario("several_quotes_only_unknown_symbols", m([pair("NOSUCH")])),
    scenario("several_quotes_the_same_scrip_twice", m([pair("SBIN"), pair("SBIN")], [h(frame(l1(3045)))])),
    scenario("a_hundred_and_five_quotes_go_in_two_batches", m(BULK, [h(frame(*[l1(800000 + i) for i in range(100)]))], [h(frame(*[l1(800000 + i) for i in range(100, 105)]))])),
    scenario("a_batch_with_a_quote_missing", m(BULK[:3], [h(frame(l1(800000), l1(800002)))])),
]
