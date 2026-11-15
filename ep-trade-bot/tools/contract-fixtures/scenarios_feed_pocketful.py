"""Pocketful feed: one socket address carrying the client id and the token, no sign-in frame, a heartbeat {"a": "h"} every fifteen seconds. Subscribes are JSON frames {a: subscribe, m: marketdata|compact_marketdata|full_snapquote, v: [[exchange code, token], ...]}, grouped by mode
and exchange code with no limit on a frame and no check for what was asked for before. LTP is the compact stream, Quote the detailed one and Depth the snapquote. A packet is read by its own mode and belongs to the first subscription (in the order they were made) that holds its token and
exchange code, whatever mode that subscription asked for, and is published to the topic of that subscription's mode."""

import json
import struct
import threading

NOW_MS = 1_793_700_000_123
BROKER = "pocketful"
AUTH_TOKEN = "pocketful-access-token"
CLIENT_ID = "PF100"
ENV = {"BROKER_API_KEY": "pf-client-key", "BROKER_API_SECRET": "pf-client-secret"}


def row(symbol, exchange, token, brexchange=None, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": str(token), "expiry": "", "strike": 0.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE", 3045), row("INFY", "NSE", 1594), row("RELIANCE", "NSE", 2885), row("TATAMOTORS", "BSE", 500570), row("SBINBSE", "BSE", 3045), row("NIFTY25NOV26FUT", "NFO", 49500, instrument="FUT"),
    row("SENSEX26NOVFUT", "BFO", 60001, instrument="FUT"), row("USDINR25NOV26FUT", "CDS", 90001, instrument="FUT"), row("GOLDM25NOV26FUT", "MCX", 80001, instrument="FUT"),
    row("NIFTY", "NSE_INDEX", 26000, "NSE", "INDEX"), row("SENSEX", "BSE_INDEX", 19000, "BSE", "INDEX"), row("CRUDEOIL25NOV26FUT", "NCO", 70001, instrument="FUT"),
]


class Driver:
    """Runs the real adapter with a fake socket; the batch timer is fired by hand and the heartbeat and health threads do nothing."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.pocketful.streaming.pocketful_adapter import PocketfulWebSocketAdapter

        self.adapter_cls = PocketfulWebSocketAdapter
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

        class Fake:
            def send(self, text):
                sent.append(text)

            def close(self):
                pass

        adapter = self.adapter_cls()
        adapter.cleanup_zmq = lambda: None
        adapter._heartbeat_loop = lambda *a, **k: None
        adapter._health_check_loop = lambda *a, **k: None
        ws = Fake()
        adapter.ws_client = ws
        adapter.running = True
        adapter.connected = True
        adapter.access_token = AUTH_TOKEN
        adapter.user_id = CLIENT_ID
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

    def _fire(self):
        timers, self.timers[:] = list(self.timers), []
        for timer in timers:
            timer.fired = True
            timer.function()

    def _take(self, extra=None):
        out = {"sent": [json.loads(text) for text in self.sent], "published": list(self.published)}
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
        self.adapter._on_open(self.ws)
        self._fire()
        return self._take()

    def frame(self, hex_text):
        self.adapter._on_message(self.ws, bytes.fromhex(hex_text))
        return self._take()

    def text(self, text):
        self.adapter._on_message(self.ws, text if isinstance(text, str) else json.dumps(text))
        return self._take()


def sub(symbol, exchange, mode):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def detailed(token=3045, exchange=1, ltp=80545, ltt=1793699999, ltq=25, volume=1234567, bid=80540, bidq=100, ask=80550, askq=110, buy=45000, sell=52000, avg=80410, exts=1793699999, o=80100, hi=80950, low=79900, close=80000, oi=0):
    return struct.pack(">bbIIIIIIIIIQQIIIIIIIIIIII", 1, exchange, token, ltp, ltt, ltq, volume, bid, bidq, ask, askq, buy, sell, avg, exts, o, hi, low, close, 90000, 70000, 72000, 88000, oi, 0).hex()


def compact(token=3045, exchange=1, ltp=80545, change=545, ltt=1793699999, oi=0, bid=80540, ask=80550):
    return struct.pack(">bbIIIIIIIIII", 2, exchange, token, ltp, change, ltt, 72000, 88000, oi, 0, bid, ask).hex()


def snap(token=3045, exchange=1, bids=None, asks=None, avg=80410, o=80100, hi=80950, low=79900, close=80000, buy=45000, sell=52000, volume=1234567):
    bids = bids if bids is not None else [(3, 80540 - 5 * i, 100 * (i + 1)) for i in range(5)]
    asks = asks if asks is not None else [(2, 80550 + 5 * i, 110 * (i + 1)) for i in range(5)]

    def five(rows, at):
        rows = list(rows) + [(0, 0, 0)] * (5 - len(rows))
        return [r[at] for r in rows[:5]]

    return struct.pack(">bbI" + "I" * 30 + "IIIIIQQI", 4, exchange, token, *five(bids, 0), *five(bids, 1), *five(bids, 2), *five(asks, 0), *five(asks, 1), *five(asks, 2), avg, o, hi, low, close, buy, sell, volume).hex()


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


S, L, D = sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 3)
SCENARIOS = [
    scenario("subscribe_quote_is_the_detailed_stream", {"subscribe": [S]}),
    scenario("subscribe_ltp_is_the_compact_stream", {"subscribe": [L]}),
    scenario("subscribe_depth_is_the_snapquote_stream", {"subscribe": [D]}),
    scenario("subscribe_an_invalid_mode_is_refused", {"subscribe": [sub("SBIN", "NSE", 4)]}),
    scenario("subscribe_a_burst_is_grouped_by_mode_and_exchange", {"subscribe": [S, sub("INFY", "NSE", 2), sub("TATAMOTORS", "BSE", 2), sub("RELIANCE", "NSE", 1), sub("NIFTY25NOV26FUT", "NFO", 2), sub("INFY", "NSE", 1)]}),
    scenario("subscribe_every_exchange_code", {"subscribe": [S, sub("NIFTY25NOV26FUT", "NFO", 2), sub("USDINR25NOV26FUT", "CDS", 2), sub("GOLDM25NOV26FUT", "MCX", 2), sub("TATAMOTORS", "BSE", 2), sub("SENSEX26NOVFUT", "BFO", 2)]}),
    scenario("subscribe_indexes_use_their_parent_exchange", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2), sub("SENSEX", "BSE_INDEX", 2)]}),
    scenario("subscribe_an_exchange_with_no_code_is_nse", {"subscribe": [sub("CRUDEOIL25NOV26FUT", "NCO", 2)]}),
    scenario("subscribe_all_three_modes_of_one_symbol", {"subscribe": [S, L, D]}),
    scenario("subscribe_the_same_symbol_twice_in_a_burst", {"subscribe": [S, S]}),
    scenario("subscribe_the_same_symbol_again_later_is_sent_again", {"subscribe": [S]}, {"subscribe": [S]}),
    scenario("subscribe_one_after_another", {"subscribe": [S]}, {"subscribe": [sub("INFY", "NSE", 2)]}),
    scenario("subscribe_the_same_token_on_two_exchanges", {"subscribe": [S, sub("SBINBSE", "BSE", 2)]}),
    scenario("unsubscribe_quote_sends_its_stream", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}),
    scenario("unsubscribe_ltp_sends_the_compact_stream", {"subscribe": [L]}, {"unsubscribe": L}),
    scenario("unsubscribe_depth_sends_the_snapquote_stream", {"subscribe": [D]}, {"unsubscribe": D}),
    scenario("unsubscribe_one_mode_of_two", {"subscribe": [S, L]}, {"unsubscribe": L}),
    scenario("unsubscribe_something_never_subscribed_sends_nothing", {"subscribe": [S]}, {"unsubscribe": sub("INFY", "NSE", 2)}),
    scenario("unsubscribe_the_other_mode_of_a_symbol_sends_nothing", {"subscribe": [S]}, {"unsubscribe": L}),
    scenario("unsubscribe_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"unsubscribe": sub("NIFTY", "NSE_INDEX", 2)}),
    scenario("unsubscribe_the_last_symbol", {"subscribe": [S]}, {"unsubscribe": S}),
    scenario("unsubscribe_twice", {"subscribe": [S]}, {"unsubscribe": S}, {"unsubscribe": S}),
    scenario("unsubscribe_then_subscribe_again", {"subscribe": [S]}, {"unsubscribe": S}, {"subscribe": [S]}),
    scenario("reconnect_repeats_the_set_grouped", {"subscribe": [S, sub("INFY", "NSE", 2), L, D, sub("TATAMOTORS", "BSE", 2)]}, {"reconnect": {}}),
    scenario("reconnect_with_nothing_subscribed", {"reconnect": {}}),
    scenario("reconnect_after_an_unsubscribe", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"reconnect": {}}),
    scenario("reconnect_after_the_same_symbol_twice_asks_once", {"subscribe": [S]}, {"subscribe": [S]}, {"reconnect": {}}),
    scenario("reconnect_keeps_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"reconnect": {}}),
    scenario("a_detailed_packet_for_a_quote_subscriber", {"subscribe": [S]}, {"frame": detailed()}),
    scenario("a_detailed_packet_for_an_ltp_subscriber", {"subscribe": [L]}, {"frame": detailed()}),
    scenario("a_detailed_packet_for_a_depth_subscriber", {"subscribe": [D]}, {"frame": detailed()}),
    scenario("a_compact_packet_for_an_ltp_subscriber", {"subscribe": [L]}, {"frame": compact()}),
    scenario("a_compact_packet_for_a_quote_subscriber", {"subscribe": [S]}, {"frame": compact()}),
    scenario("a_snapquote_packet_for_a_depth_subscriber", {"subscribe": [D]}, {"frame": snap()}),
    scenario("a_snapquote_packet_for_a_quote_subscriber", {"subscribe": [S]}, {"frame": snap()}),
    scenario("a_packet_belongs_to_the_first_subscription_of_the_instrument", {"subscribe": [L, S]}, {"frame": detailed()}, {"frame": compact()}),
    scenario("a_packet_belongs_to_the_first_subscription_when_quote_came_first", {"subscribe": [S, L, D]}, {"frame": compact()}, {"frame": snap()}),
    scenario("a_packet_after_the_first_subscription_is_unsubscribed", {"subscribe": [L, S]}, {"unsubscribe": L}, {"frame": detailed()}),
    scenario("a_packet_after_a_symbol_is_subscribed_again_goes_to_the_older", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"subscribe": [L]}, {"frame": compact()}),
    scenario("a_packet_for_an_index", {"subscribe": [sub("NIFTY", "NSE_INDEX", 2)]}, {"frame": detailed(26000, 1, ltp=2410025)}),
    scenario("a_packet_for_a_bse_equity", {"subscribe": [sub("TATAMOTORS", "BSE", 2)]}, {"frame": detailed(500570, 6)}),
    scenario("a_packet_for_a_future_with_open_interest", {"subscribe": [sub("NIFTY25NOV26FUT", "NFO", 2)]}, {"frame": detailed(49500, 2, oi=7000000)}),
    scenario("a_packet_for_the_same_token_on_the_other_exchange", {"subscribe": [S, sub("SBINBSE", "BSE", 2)]}, {"frame": detailed(3045, 6)}),
    scenario("a_packet_for_the_wrong_exchange_code_is_dropped", {"subscribe": [S]}, {"frame": detailed(3045, 2)}),
    scenario("a_packet_for_an_instrument_nobody_asked_for", {"subscribe": [S]}, {"frame": detailed(9999)}),
    scenario("a_packet_after_an_unsubscribe_is_dropped", {"subscribe": [S, sub("INFY", "NSE", 2)]}, {"unsubscribe": S}, {"frame": detailed()}),
    scenario("a_detailed_packet_with_every_value_zero", {"subscribe": [S]}, {"frame": detailed(ltp=0, ltt=0, ltq=0, volume=0, bid=0, ask=0, buy=0, sell=0, avg=0, o=0, hi=0, low=0, close=0)}),
    scenario("a_compact_packet_with_every_value_zero", {"subscribe": [L]}, {"frame": compact(ltp=0, change=0, ltt=0, bid=0, ask=0)}),
    scenario("a_snapquote_packet_with_a_short_ladder", {"subscribe": [D]}, {"frame": snap(bids=[(1, 80540, 100)], asks=[(1, 80550, 110), (2, 80555, 120)])}),
    scenario("a_snapquote_packet_with_an_empty_ladder", {"subscribe": [D]}, {"frame": snap(bids=[], asks=[])}),
    scenario("a_snapquote_packet_with_a_zero_price_level", {"subscribe": [D]}, {"frame": snap(bids=[(1, 0, 100)] * 5)}),
    scenario("a_packet_that_is_cut_short_is_dropped", {"subscribe": [S]}, {"frame": detailed()[:100]}),
    scenario("a_packet_with_an_unknown_mode_is_dropped", {"subscribe": [S]}, {"frame": "07" + detailed()[2:]}),
    scenario("an_empty_binary_frame", {"subscribe": [S]}, {"frame": ""}),
    scenario("a_compact_packet_in_text", {"subscribe": [L]}, {"text": {"mode": 2, "instrument_token": 3045, "exchange_code": 1, "last_traded_price": 80545, "change": 5, "last_traded_time": 1793699999, "currentOpenInterest": 0, "bidPrice": 80540, "askPrice": 80550}}),
    scenario("a_compact_packet_in_text_with_other_names", {"subscribe": [L]}, {"text": {"mode": 2, "instrumentToken": "3045", "exchangeCode": 1, "last_traded_price": 80545}}),
    scenario("a_compact_packet_in_text_nested_in_d", {"subscribe": [L]}, {"text": {"mode": 2, "d": {"instrument_token": 3045, "exchange_code": 1, "last_traded_price": 80545}}}),
    scenario("a_compact_packet_in_text_nested_in_data", {"subscribe": [L]}, {"text": {"mode": 2, "data": {"instrument_token": 3045, "exchange_code": 1, "last_traded_price": 80545}}}),
    scenario("a_compact_packet_in_text_with_both_names_keeps_the_first", {"subscribe": [L]}, {"text": {"mode": 2, "instrument_token": 3045, "instrumentToken": 9999, "exchange_code": 1, "exchangeCode": 2, "last_traded_price": 80545}}),
    scenario("a_compact_packet_in_text_without_an_exchange_code_is_dropped", {"subscribe": [L]}, {"text": {"mode": 2, "instrument_token": 3045, "last_traded_price": 80545}}),
    scenario("a_snapquote_packet_in_text", {"subscribe": [D]}, {"text": {"mode": 4, "instrument_token": 3045, "exchange_code": 1, "bidPrices": [80540, 80535], "bidQtys": [100, 200], "buyers": [1, 2], "askPrices": [80550], "askQtys": [110], "sellers": [3], "averageTradePrice": 80410,
                                                                      "open": 80100, "high": 80950, "low": 79900, "close": 80000, "totalBuyQty": 45000, "totalSellQty": 52000, "volume": 99}}),
    scenario("a_snapquote_packet_in_text_without_a_ladder", {"subscribe": [D]}, {"text": {"mode": 4, "instrument_token": 3045, "exchange_code": 1, "averageTradePrice": 80410}}),
    scenario("a_snapquote_packet_in_text_as_the_first_of_a_list", {"subscribe": [D]}, {"text": {"mode": 4, "data": [1]}}),
    scenario("a_detailed_packet_in_text_is_dropped", {"subscribe": [S]}, {"text": {"mode": 1, "instrument_token": 3045, "exchange_code": 1, "last_traded_price": 80545}}),
    scenario("a_text_frame_without_a_mode_is_dropped", {"subscribe": [S]}, {"text": {"instrument_token": 3045, "exchange_code": 1}}),
    scenario("a_text_frame_that_is_not_json", {"subscribe": [S]}, {"text": "hello"}),
    scenario("a_text_frame_that_is_a_list", {"subscribe": [S]}, {"text": [1, 2]}),
    scenario("a_text_frame_of_another_mode_is_dropped", {"subscribe": [S]}, {"text": {"mode": 9, "instrument_token": 3045}}),
]
