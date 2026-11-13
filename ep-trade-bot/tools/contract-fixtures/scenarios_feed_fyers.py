"""Fyers feed (the HSM socket): the binary sign-in and subscribe messages it is sent and what the adapters publish for the snapshot and update packets that come back."""

import base64
import json
import struct

BROKER = "fyers"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "FYAPPID-100", "BROKER_API_SECRET": "fyers-secret"}

_payload = base64.urlsafe_b64encode(json.dumps({"hsm_key": "hsm-key-abc123", "exp": 4_000_000_000}).encode()).decode().rstrip("=")
ACCESS_TOKEN = f"FYAPPID-100:e30.{_payload}.sig"
BARE_TOKEN = ACCESS_TOKEN.split(":", 1)[1]
AUTH_TOKEN = BARE_TOKEN


def row(symbol, brsymbol, exchange, token, instrument="EQ", name=None):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": name or symbol, "exchange": exchange, "brexchange": exchange.replace("_INDEX", ""), "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE:SBIN-EQ", "NSE", "10100000003045"),
    row("INFY", "NSE:INFY-EQ", "NSE", "10100000001594"),
    row("NIFTY25NOV26FUT", "NSE:NIFTY26NOVFUT", "NFO", "101126112535001", "FUTIDX", "NIFTY"),
    row("NIFTY", "NSE:NIFTY50-INDEX", "NSE_INDEX", "101000000026000", "INDEX", "NIFTY"),
    row("SENSEX", "BSE:SENSEX-INDEX", "BSE_INDEX", "121000000000001", "INDEX", "SENSEX"),
    row("TCS", "BSE:TCS-A", "BSE", "121000000500325"),
    row("CRUDEOILM", "MCX:CRUDEOILM26DECFUT", "MCX", "112000000487465", "FUTCOM"),
    row("USDINR", "NSE:USDINR26NOVFUT", "CDS", "101200000027801", "FUTCUR"),
    row("SHORTTOKEN", "NSE:SHORT-EQ", "NSE", "12345"),
    row("ODDSEGMENT", "NSE:ODD-EQ", "NSE", "99990000000001"),
    row("EIGHTCHARS", "NSE:EIGHT-EQ", "NSE", "10101234"),
]

MISSING = -2147483648
DATA_FIELDS = ["ltp", "vol_traded_today", "last_traded_time", "exch_feed_time", "bid_size", "ask_size", "bid_price", "ask_price", "last_traded_qty", "tot_buy_qty",
               "tot_sell_qty", "avg_trade_price", "OI", "low_price", "high_price", "Yhigh", "Ylow", "lower_ckt", "upper_ckt", "open_price", "prev_close_price", "type", "symbol"]
INDEX_FIELDS = ["ltp", "prev_close_price", "exch_feed_time", "high_price", "low_price", "open_price", "type", "symbol"]
DEPTH_FIELDS = [f"bid_price{i}" for i in range(1, 6)] + [f"ask_price{i}" for i in range(1, 6)] + [f"bid_size{i}" for i in range(1, 6)] + [f"ask_size{i}" for i in range(1, 6)] \
    + [f"bid_order{i}" for i in range(1, 6)] + [f"ask_order{i}" for i in range(1, 6)] + ["type", "symbol"]


def _ints(names, values):
    out = b""
    for name in names:
        out += struct.pack(">i", values.get(name, MISSING))
    return out


def _string(text):
    raw = text.encode()
    return bytes([len(raw)]) + raw


def feed(records):
    body = b"".join(records)
    return struct.pack(">H", 7 + len(body)) + bytes([6]) + b"\x00\x00\x00\x00" + struct.pack(">H", len(records)) + body


def scrip_snapshot(topic_id, hsm, values, multiplier=1, precision=2, exchange="NSE", token="3045", symbol="SBIN-EQ", extra=0):
    return bytes([83]) + struct.pack("<H", topic_id) + _string(hsm) + bytes([len(DATA_FIELDS) + extra]) + _ints(DATA_FIELDS, values) + struct.pack(">i", 777) * extra + b"\x00\x00" + struct.pack(">H", multiplier) + bytes([precision]) \
        + _string(exchange) + _string(token) + _string(symbol)


def index_snapshot(topic_id, hsm, values):
    return bytes([83]) + struct.pack("<H", topic_id) + _string(hsm) + bytes([len(INDEX_FIELDS)]) + _ints(INDEX_FIELDS, values)


def depth_snapshot(topic_id, hsm, values, multiplier=1, precision=2, exchange="NSE", token="3045", symbol="SBIN-EQ"):
    return bytes([83]) + struct.pack("<H", topic_id) + _string(hsm) + bytes([len(DEPTH_FIELDS)]) + _ints(DEPTH_FIELDS, values) + b"\x00\x00" + struct.pack(">H", multiplier) + bytes([precision]) \
        + _string(exchange) + _string(token) + _string(symbol)


def update(topic_id, values_in_order):
    return bytes([85]) + struct.pack("<H", topic_id) + bytes([len(values_in_order)]) + b"".join(struct.pack(">i", v) for v in values_in_order)


def auth_reply(ok=True):
    status = b"K" if ok else b"E"
    return struct.pack(">H", 5 + len(status)) + bytes([1, 1, 1]) + struct.pack(">H", len(status)) + status


# Every scenario starts with the sign-in already accepted, as the recorder's sockets are.
START_FRAMES = [auth_reply().hex()]

SBIN_SF = "sf|nse_cm|3045"
INFY_SF = "sf|nse_cm|1594"
SBIN_DP = "dp|nse_cm|3045"
NIFTY_IF = "if|nse_cm|NIFTY"

QUOTE_VALUES = {"ltp": 80545, "vol_traded_today": 1234567, "last_traded_time": 1793699999, "exch_feed_time": 1793699998, "bid_size": 120, "ask_size": 80, "bid_price": 80540,
                "ask_price": 80550, "last_traded_qty": 5, "tot_buy_qty": 170, "tot_sell_qty": 97, "avg_trade_price": 80310, "OI": 0, "low_price": 79550, "high_price": 81000,
                "lower_ckt": 72000, "upper_ckt": 88000, "open_price": 80000, "prev_close_price": 79995}
DEPTH_VALUES = {**{f"bid_price{i}": 80540 - 5 * (i - 1) for i in range(1, 6)}, **{f"ask_price{i}": 80550 + 5 * (i - 1) for i in range(1, 6)},
                **{f"bid_size{i}": 100 * i for i in range(1, 6)}, **{f"ask_size{i}": 90 * i for i in range(1, 6)},
                **{f"bid_order{i}": i for i in range(1, 6)}, **{f"ask_order{i}": i + 1 for i in range(1, 6)}}
INDEX_VALUES = {"ltp": 2405050, "prev_close_price": 2395000, "exch_feed_time": 1793699998, "high_price": 2410000, "low_price": 2390000, "open_price": 2400000}


class Driver:
    """Runs the real outer adapter, the real inner adapter and the real HSM client with a fake socket and a stand-in for the symbol-token call."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        import broker.fyers.streaming.fyers_token_converter as converter
        from broker.fyers.streaming.fyers_adapter import FyersAdapter
        from broker.fyers.streaming.fyers_hsm_websocket import FyersHSMWebSocket
        from broker.fyers.streaming.fyers_websocket_adapter import FyersWebSocketAdapter

        self.classes = (FyersWebSocketAdapter, FyersAdapter, FyersHSMWebSocket)
        table = {r["brsymbol"]: r["token"] for r in SYMBOLS}

        class Reply:
            def __init__(self, brsymbols):
                self.brsymbols = brsymbols

            def json(self):
                valid = {b: table[b] for b in self.brsymbols if b in table}
                return {"s": "ok", "validSymbol": valid, "invalidSymbol": [b for b in self.brsymbols if b not in table]}

        converter.requests.post = lambda url, headers=None, json=None, timeout=None: Reply(json["symbols"])

    def reset(self, authenticated=True):
        outer_cls, inner_cls, hsm_cls = self.classes
        self.sent, self.published = [], []
        sent = self.sent

        class FakeSocket:
            def send(self, data, opcode=None):
                sent.append(bytes(data).hex())

            def close(self):
                pass

        hsm = hsm_cls(ACCESS_TOKEN, "", "fixtureuser")
        hsm.ws = FakeSocket()
        hsm.connected = True
        hsm.authenticated = authenticated
        hsm._start_health_check = lambda: None
        inner = inner_cls(ACCESS_TOKEN, "fixtureuser")
        inner.ws_client = hsm
        inner.connected = True
        hsm.set_callbacks(on_message=inner._on_message, on_error=inner._on_error, on_open=inner._on_open, on_close=inner._on_close)
        outer = outer_cls()
        outer.fyers_adapter = inner
        outer.connected, outer.running = True, True
        outer.access_token = ACCESS_TOKEN
        self._hold = object()
        outer._hsm_batch_timer = self._hold
        class _Quiet:
            def close(self, **kwargs):
                pass

        outer.socket = _Quiet()
        outer.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.outer, self.inner, self.hsm = outer, inner, hsm

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.outer.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.outer._flush_hsm_batch()
        self.outer._hsm_batch_timer = self._hold
        return self._take()

    def unsubscribe(self, item):
        self.outer.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def signed_out(self, _):
        self.hsm.authenticated = False
        return self._take()

    def reconnect(self, _):
        self.hsm.authenticated = False
        self.hsm._on_ws_open(self.hsm.ws)
        return self._take()

    def frame(self, hex_text):
        self.hsm._on_ws_message(self.hsm.ws, bytes.fromhex(hex_text))
        return self._take()


def sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(data):
    return {"frame": data.hex()}


SCENARIOS = [
    {"name": "subscribe_in_each_mode", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("SBIN", 3)]}]},
    {"name": "quote_and_ltp_share_a_message_and_depth_has_its_own", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("SBIN", 3), sub("INFY", 3)]}]},
    {"name": "every_exchange_and_an_index", "steps": [{"subscribe": [
        sub("SBIN", 1), sub("NIFTY25NOV26FUT", 1, "NFO"), sub("NIFTY", 1, "NSE_INDEX"), sub("SENSEX", 1, "BSE_INDEX"), sub("TCS", 1, "BSE"), sub("CRUDEOILM", 1, "MCX"), sub("USDINR", 1, "CDS")]}]},
    {"name": "an_index_subscribed_for_depth_uses_the_index_feed", "steps": [{"subscribe": [sub("NIFTY", 3, "NSE_INDEX")]}]},
    {"name": "tokens_that_cannot_be_made_into_a_feed_token_are_left_out", "steps": [{"subscribe": [sub("SHORTTOKEN", 1), sub("ODDSEGMENT", 1), sub("SBIN", 1)]}]},
    {"name": "a_mode_the_exchange_does_not_have", "steps": [{"subscribe": [sub("SBIN", 4), sub("SBIN", 0)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("INFY", 1)]}, {"subscribe": [sub("SBIN", 3)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 1)]}]},
    {"name": "unsubscribe_sends_nothing_to_fyers", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 1)]}, {"unsubscribe": sub("SBIN", 1)}]},
    {"name": "sign_in_and_subscribe_after_a_reconnect", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("SBIN", 3)]}, {"reconnect": None}, frame(auth_reply())]},
    {"name": "a_rejected_sign_in_subscribes_nothing", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"reconnect": None}, frame(auth_reply(False))]},
    {"name": "quote_snapshot", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "ltp_snapshot_publishes_the_quote_shape", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "snapshot_then_a_live_update", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)])), frame(feed([update(1, [80600, 1234999])]))]},
    {"name": "an_update_that_changes_nothing_publishes_nothing", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)])), frame(feed([update(1, [80545, 1234567])]))]},
    {"name": "an_update_with_missing_fields_keeps_the_rest", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)])), frame(feed([update(1, [MISSING, 1235000, 1793700001])]))]},
    {"name": "an_update_before_any_snapshot_is_skipped", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(feed([update(7, [80600, 1234999])]))]},
    {"name": "a_currency_pair_has_four_decimals", "steps": [
        {"subscribe": [sub("USDINR", 2, "CDS")]}, frame(feed([scrip_snapshot(2, "sf|cde_fo|27801", {**QUOTE_VALUES, "ltp": 957425, "prev_close_price": 956000}, precision=4, exchange="CDE", token="27801", symbol="USDINR26NOVFUT")]))]},
    {"name": "a_price_multiplier", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES, multiplier=10)]))]},
    {"name": "a_zero_multiplier_counts_as_one", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES, multiplier=0)]))]},
    {"name": "depth_snapshot", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(feed([depth_snapshot(3, SBIN_DP, DEPTH_VALUES)]))]},
    {"name": "depth_carries_the_last_price_from_the_quote_feed", "steps": [
        {"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)])), frame(feed([depth_snapshot(3, SBIN_DP, DEPTH_VALUES)]))]},
    {"name": "depth_with_empty_levels_leaves_them_out", "steps": [
        {"subscribe": [sub("SBIN", 3)]}, frame(feed([depth_snapshot(3, SBIN_DP, {**DEPTH_VALUES, "bid_price4": 0, "ask_price5": 0, "bid_price5": MISSING})]))]},
    {"name": "depth_update", "steps": [
        {"subscribe": [sub("SBIN", 3)]}, frame(feed([depth_snapshot(3, SBIN_DP, DEPTH_VALUES)])), frame(feed([update(3, [80545] + [MISSING] * 4 + [80555])]))]},
    {"name": "index_snapshot", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX")]}, frame(feed([index_snapshot(4, NIFTY_IF, INDEX_VALUES)]))]},
    {"name": "index_snapshot_for_depth_gives_the_index_value_and_no_levels", "steps": [
        {"subscribe": [sub("NIFTY", 3, "NSE_INDEX")]}, frame(feed([index_snapshot(4, NIFTY_IF, INDEX_VALUES)]))]},
    {"name": "index_snapshot_for_quote_and_depth_gives_both", "steps": [
        {"subscribe": [sub("NIFTY", 2, "NSE_INDEX"), sub("NIFTY", 3, "NSE_INDEX")]}, frame(feed([index_snapshot(4, NIFTY_IF, INDEX_VALUES)]))]},
    {"name": "index_snapshot_with_no_value_gives_no_depth", "steps": [
        {"subscribe": [sub("NIFTY", 3, "NSE_INDEX")]}, frame(feed([index_snapshot(4, NIFTY_IF, {**INDEX_VALUES, "ltp": MISSING})]))]},
    {"name": "index_update", "steps": [
        {"subscribe": [sub("NIFTY", 1, "NSE_INDEX")]}, frame(feed([index_snapshot(4, NIFTY_IF, INDEX_VALUES)])), frame(feed([update(4, [2406000])]))]},
    {"name": "ltp_then_quote_subscription_of_one_symbol_publishes_as_the_later_one", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "two_symbols_in_one_frame", "steps": [
        {"subscribe": [sub("SBIN", 2), sub("INFY", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES), scrip_snapshot(5, INFY_SF, {**QUOTE_VALUES, "ltp": 150010}, token="1594", symbol="INFY-EQ")]))]},
    {"name": "a_feed_for_a_token_nobody_subscribed_is_ignored", "steps": [
        {"subscribe": [sub("SBIN", 2), sub("INFY", 2)]}, frame(feed([scrip_snapshot(9, "sf|nse_cm|9999", QUOTE_VALUES, token="9999", symbol="OTHER-EQ")]))]},
    {"name": "frame_after_the_symbol_was_unsubscribed", "steps": [
        {"subscribe": [sub("SBIN", 2), sub("INFY", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)])), {"unsubscribe": sub("SBIN", 2)}, frame(feed([update(1, [80600, 1234999])]))]},
    {"name": "a_token_of_eight_characters_is_too_short", "steps": [{"subscribe": [sub("EIGHTCHARS", 1), sub("SBIN", 1)]}]},
    {"name": "a_second_request_for_the_same_feed_is_sent_once_after_a_reconnect", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 2)]}, {"reconnect": None}, frame(auth_reply())]},
    {"name": "two_requests_for_one_symbol_in_one_batch_the_later_decides_the_mode", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "a_one_byte_unknown_record_ends_the_frame", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([bytes([68]), scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "fields_beyond_the_table_are_left_out", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(1, SBIN_SF, QUOTE_VALUES, extra=2)]))]},
    {"name": "an_update_for_an_unknown_topic_is_stepped_over", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([update(9, [1, 2, 3]), scrip_snapshot(1, SBIN_SF, QUOTE_VALUES)]))]},
    {"name": "a_subscription_before_the_sign_in_is_accepted_waits", "steps": [{"signed_out": None}, {"subscribe": [sub("SBIN", 1)]}, frame(auth_reply())]},
    {"name": "a_subscription_after_a_reconnect_but_before_the_answer_waits", "steps": [{"reconnect": None}, {"subscribe": [sub("SBIN", 1)]}, frame(auth_reply())]},
    {"name": "a_feed_nobody_asked_for_is_not_guessed_at_even_with_one_subscription", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(feed([scrip_snapshot(9, "sf|nse_cm|9999", QUOTE_VALUES, token="9999", symbol="OTHER-EQ")]))]},
    {"name": "frames_that_carry_no_market_data", "steps": [
        {"subscribe": [sub("SBIN", 2), sub("INFY", 2)]}, {"frame": ""}, {"frame": "00"}, {"frame": "000306"}, frame(feed([bytes([68, 0, 0])])), frame(struct.pack(">H", 3) + bytes([13, 0, 0]))]},
]
