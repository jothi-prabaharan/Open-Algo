"""Motilal feed: a binary socket (wss://ws1feed.motilaloswal.com/jwebsocket/jwebsocket). A login packet carries the client code; the first frame back signs it in. A subscribe sends a 10-byte register packet for the scrip (one for every request, so a scrip asked for in two modes is
registered twice) and an unsubscribe the same packet with the flag off, which also drops what was kept for the scrip. 30-byte packets come back: A is the last price, G the day's open, high, low and close, B to F the five depth levels, m the open interest, W the circuit
limits, H an index value. OpenAlgo republishes every subscription from the store twice a second; this recording runs the adapter's own publishing once after each frame, for the subscriptions of the scrips that frame touched. Run on the real adapter with a fake socket."""

import sys
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import scenarios_feed_snap_motilal as snap  # noqa: E402
from scenarios_feed_snap_motilal import ACK, book_packets, dpr, h, index_body, ltp, ohlc, oi_body, packet, whole  # noqa: E402,F401

NOW_MS = 1_793_700_000_123
BROKER = "motilal"
AUTH_TOKEN = "motilal-auth-token:::motilal-access-token"
CLIENT_ID = "MO100"
ENV = {"BROKER_API_KEY": "motilal-api-key", "BROKER_API_SECRET": "motilal-api-secret"}
SYMBOLS = snap.SYMBOLS
START_FRAMES = [ACK.hex()]


class Driver:
    """Runs the real adapter and its real socket client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.motilal.api import motilal_websocket
        from broker.motilal.streaming import motilal_adapter

        self.adapter_cls = motilal_adapter.MotilalWebSocketAdapter
        self.client_cls = motilal_websocket.MotilalWebSocket

    def reset(self):
        import time

        self.sent, self.published, self.resubscribe_pending = [], [], False
        sent = self.sent

        class Fake:
            def send(self, data, opcode=None):
                sent.append(bytes(data).hex() if isinstance(data, (bytes, bytearray)) else data)

            def close(self):
                pass

        class OneCycle:
            def is_set(self):
                return False

            def wait(self, timeout=None):
                return True

            def clear(self):
                pass

            def set(self):
                pass

        adapter = self.adapter_cls()
        adapter.cleanup_zmq = lambda: None
        client = self.client_cls(CLIENT_ID, AUTH_TOKEN, "motilal-api-key")
        client.ws = Fake()
        client.is_connected = True
        # The socket client repeats its own list of scrips when the server answers; the adapter repeats the same list, and one copy is the same state.
        client._resubscribe = lambda: None
        client.disconnect = lambda: None
        adapter.ws_client = client
        adapter.running = True
        adapter.connected = True
        adapter._stop_poll = OneCycle()
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.client = adapter, client
        self._time = time

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
        return self._take({"errors": errors} if errors else None)

    def unsubscribe(self, item):
        result = self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take({"error": result.get("error_code")} if result.get("status") != "success" else None)

    def reconnect(self, _):
        """A new connection: nothing signed in; the socket sends its login."""
        self.client.is_connected = False
        self.client.on_open(self.client.ws)
        self.resubscribe_pending = True
        return self._take()

    def frame(self, hex_text):
        data = bytes.fromhex(hex_text)
        client = self.client
        before = set(client.last_quotes) | set(client.last_depth) | set(client.last_oi) | set(client.last_index)
        # What the frame touches: the stores it writes, by the key the client resolves for each packet.
        touched = set()
        for i in range(len(data) // 30):
            packet_bytes = data[i * 30:(i + 1) * 30]
            char = packet_bytes[0:1].decode("utf-8", errors="ignore")
            scrip = int.from_bytes(packet_bytes[1:5], "little", signed=True)
            kind = packet_bytes[9:10].decode("utf-8", errors="ignore")
            if kind in ("A", "B", "C", "D", "E", "F", "G", "H", "m", "W"):
                touched.add(client._store_key(client._resolve_exchange_name(char, scrip), scrip))
        client.on_message(None, data)
        if self.resubscribe_pending:
            self.resubscribe_pending = False
            self.adapter._resubscribe_all()
        everything = dict(self.adapter.subscriptions)
        self.adapter.subscriptions = {k: v for k, v in everything.items() if client._store_key(v["motilal_exchange"], v["token"]) in touched}
        self._time.sleep = lambda *a, **k: None
        self.adapter._poll_market_data()
        self.adapter.subscriptions = everything
        del before
        return self._take()

    def text(self, text):
        self.client.on_message(None, text)
        return self._take()


def sub(symbol, exchange, mode, depth=5):
    return {"symbol": symbol, "exchange": exchange, "mode": mode, "depth_level": depth}


def scenario(name, *steps):
    return {"name": name, "steps": [{k: v} for k, v in steps]}


def S(*items):
    return ("subscribe", list(items))


def U(symbol, exchange, mode):
    return ("unsubscribe", sub(symbol, exchange, mode))


def F(data):
    return ("frame", data.hex() if isinstance(data, (bytes, bytearray)) else data)


def R():
    return ("reconnect", None)


A_FULL = whole(3045, with_oi=None)
SCENARIOS = [
    scenario("subscribe_ltp", S(sub("SBIN", "NSE", 1))),
    scenario("subscribe_quote", S(sub("SBIN", "NSE", 2))),
    scenario("subscribe_depth", S(sub("SBIN", "NSE", 3))),
    scenario("subscribe_a_future", S(sub("NIFTY25NOV26FUT", "NFO", 2))),
    scenario("subscribe_a_bse_equity", S(sub("TATAMOTORS", "BSE", 2))),
    scenario("subscribe_a_bse_future", S(sub("SENSEX26NOVFUT", "BFO", 2))),
    scenario("subscribe_a_currency_future", S(sub("USDINR25NOV26FUT", "CDS", 2))),
    scenario("subscribe_a_commodity_future", S(sub("GOLDM25NOV26FUT", "MCX", 2))),
    scenario("subscribe_an_index", S(sub("NIFTY", "NSE_INDEX", 1))),
    scenario("subscribe_several_at_once", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 2), sub("RELIANCE", "NSE", 3))),
    scenario("subscribe_the_same_scrip_in_two_modes_registers_twice", S(sub("SBIN", "NSE", 1)), S(sub("SBIN", "NSE", 2))),
    scenario("subscribe_the_same_request_twice", S(sub("SBIN", "NSE", 2)), S(sub("SBIN", "NSE", 2))),
    scenario("subscribe_with_an_invalid_mode_is_refused", S(sub("SBIN", "NSE", 4))),
    scenario("unsubscribe_ltp", S(sub("SBIN", "NSE", 1)), U("SBIN", "NSE", 1)),
    scenario("unsubscribe_a_mode_never_subscribed_still_sends_the_packet", U("SBIN", "NSE", 1)),
    scenario("unsubscribe_one_of_two_modes_still_drops_the_scrip", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2)), U("SBIN", "NSE", 1), F(whole())),
    scenario("unsubscribe_depth", S(sub("SBIN", "NSE", 3)), U("SBIN", "NSE", 3)),
    scenario("an_ltp_tick", S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "A", ltp()))),
    scenario("an_ltp_tick_carries_the_cumulative_volume", S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "A", ltp(cumulative=4_000_000)))),
    scenario("a_quote_tick_waits_for_the_last_price", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "G", ohlc()))),
    scenario("a_quote_tick", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "G", ohlc()) + packet("N", 3045, "A", ltp()))),
    scenario("a_quote_tick_in_two_frames", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "G", ohlc())), F(packet("N", 3045, "A", ltp()))),
    scenario("a_quote_tick_with_the_average_price_and_last_quantity", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "A", ltp(805.45, 77, 5000, 803.75)))),
    scenario("a_quote_tick_prices_are_rounded_to_two_places", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "A", ltp(805.4567, 25, 100, 803.7777)) + packet("N", 3045, "G", ohlc(801.011, 809.555, 799.999, 800.004)))),
    scenario("a_quote_tick_with_every_value_zero", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "A", ltp(0.0, 0, 0, 0.0)) + packet("N", 3045, "G", ohlc(0.0, 0.0, 0.0, 0.0)))),
    scenario("a_depth_tick_waits_for_the_book", S(sub("SBIN", "NSE", 3)), F(packet("N", 3045, "A", ltp()))),
    scenario("a_depth_tick_waits_for_the_quote", S(sub("SBIN", "NSE", 3)), F(book_packets("N", 3045))),
    scenario("a_depth_tick", S(sub("SBIN", "NSE", 3)), F(whole())),
    scenario("a_depth_tick_with_a_short_book_is_padded", S(sub("SBIN", "NSE", 3)), F(book_packets("N", 3045, [(805.4, 100, 3)], [(805.5, 110, 2)]) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_tick_with_one_side_only", S(sub("SBIN", "NSE", 3)), F(packet("N", 3045, "B", snap.level(805.4, 100, 3, 0.0, 0, 0)) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_tick_with_a_gap_in_the_levels_is_squeezed", S(sub("SBIN", "NSE", 3)), F(packet("N", 3045, "B", snap.level()) + packet("N", 3045, "E", snap.level(805.2, 400, 6, 805.7, 440, 5)) + packet("N", 3045, "A", ltp()))),
    scenario("a_depth_tick_with_open_interest", S(sub("NIFTY25NOV26FUT", "NFO", 3)), F(whole(49500, with_oi=(7000, 7500, 6500), rate=24010.5, oi=6500))),
    scenario("a_depth_tick_with_circuit_limits", S(sub("SBIN", "NSE", 3)), F(packet("N", 3045, "W", dpr(880.0, 720.0)) + whole())),
    scenario("open_interest_before_the_quote_is_not_in_the_quote", S(sub("NIFTY25NOV26FUT", "NFO", 3)), F(packet("N", 49500, "m", oi_body(7100, 7500, 6500))), F(whole(49500, rate=24010.5, oi=6500))),
    scenario("open_interest_after_the_quote_updates_it", S(sub("NIFTY25NOV26FUT", "NFO", 2)), F(packet("N", 49500, "A", ltp(24010.5, 25, 100, 24000.0, 6500))), F(packet("N", 49500, "m", oi_body(7100, 7500, 6500)))),
    scenario("a_future_and_its_cash_scrip_with_the_same_wire_character", S(sub("SBIN", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 2)), F(packet("N", 3045, "A", ltp()) + packet("N", 49500, "A", ltp(24010.5)))),
    scenario("a_bse_equity_tick", S(sub("TATAMOTORS", "BSE", 2)), F(packet("B", 500570, "A", ltp(498.1)))),
    scenario("a_bse_future_tick", S(sub("SENSEX26NOVFUT", "BFO", 2)), F(packet("G", 60001, "A", ltp(80100.0)))),
    scenario("a_currency_future_tick", S(sub("USDINR25NOV26FUT", "CDS", 2)), F(packet("C", 90001, "A", ltp(83.5)))),
    scenario("a_commodity_future_tick", S(sub("GOLDM25NOV26FUT", "MCX", 2)), F(packet("M", 80001, "A", ltp(75000.0)))),
    scenario("an_index_tick_for_an_index_asked_for_as_a_scrip", S(sub("NIFTY", "NSE_INDEX", 1)), F(packet("N", 26000, "H", index_body()))),
    scenario("a_frame_for_a_scrip_nobody_asked_for_publishes_nothing", S(sub("SBIN", "NSE", 2)), F(packet("N", 1594, "A", ltp(1499.5)))),
    scenario("a_frame_with_two_scrips_publishes_both", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)), F(packet("N", 3045, "A", ltp()) + packet("N", 1594, "A", ltp(1499.5)))),
    scenario("a_frame_publishes_each_mode_of_a_scrip", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2), sub("SBIN", "NSE", 3)), F(whole())),
    scenario("a_frame_for_one_scrip_leaves_the_other_alone", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)), F(packet("N", 3045, "A", ltp())), F(packet("N", 1594, "A", ltp(1499.5)))),
    scenario("a_later_frame_replaces_the_value", S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "A", ltp(805.45))), F(packet("N", 3045, "A", ltp(806.5)))),
    scenario("a_heartbeat_and_an_unknown_packet_publish_nothing", S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "1", snap.pad(b"")) + packet("N", 3045, "Z", snap.pad(b"")))),
    scenario("a_frame_cut_short_reads_the_whole_packets_in_it", S(sub("SBIN", "NSE", 2)), F((packet("N", 3045, "A", ltp()) + packet("N", 3045, "G", ohlc()))[:45])),
    scenario("a_frame_of_under_a_packet_publishes_nothing", S(sub("SBIN", "NSE", 2)), F(packet("N", 3045, "A", ltp())[:29])),
    scenario("a_frame_that_is_a_text", S(sub("SBIN", "NSE", 1)), ("text", '{"status": "ERROR", "message": "bad"}')),
    scenario("after_an_unsubscribe_the_data_is_dropped", S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "A", ltp())), U("SBIN", "NSE", 1), S(sub("SBIN", "NSE", 1)), F(packet("N", 3045, "G", ohlc()))),
    scenario("a_reconnect_signs_in_again", S(sub("SBIN", "NSE", 2), sub("INFY", "NSE", 1)), R(), F(ACK)),
    scenario("a_reconnect_registers_each_subscription", S(sub("SBIN", "NSE", 1), sub("SBIN", "NSE", 2), sub("NIFTY25NOV26FUT", "NFO", 3)), R(), F(ACK)),
    scenario("a_reconnect_with_nothing_subscribed", R(), F(ACK)),
    scenario("a_reconnect_then_a_tick", S(sub("SBIN", "NSE", 1)), R(), F(ACK), F(packet("N", 3045, "A", ltp()))),
    scenario("a_reconnect_then_an_unsubscribe", S(sub("SBIN", "NSE", 1), sub("INFY", "NSE", 1)), R(), F(ACK), U("INFY", "NSE", 1)),
]
