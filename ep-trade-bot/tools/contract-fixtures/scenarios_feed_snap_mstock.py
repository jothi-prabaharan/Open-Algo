"""mStock depth: nothing is asked for over REST. A temporary market-data socket is opened, signed in with LOGIN:<token>, a mode 3 subscribe for the one token is sent, and the first quote that can be read out of up to three frames is the answer. Run on the real
BrokerData and the real socket client, with a fake transport that hands back scripted frames."""

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from scenarios_feed_mstock import ENV, SYMBOLS, framed, ltp_packet, quote_packet, snap_packet  # noqa: E402,F401

NOW_MS = 1_793_700_000_123
BROKER = "mstock"
AUTH_TOKEN = "mst-session-jwt"


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        import websocket
        from broker.mstock.api import data as data_module

        self.data, self.websocket = data_module, websocket

    def reset(self):
        self.sent = []
        self.connections = []

    def depth(self, step):
        driver = self
        queue = [step.get("login_reply")] + list(step.get("frames", []))

        class Fake:
            def send(self, text):
                try:
                    driver.sent.append(json.loads(text))
                except ValueError:
                    driver.sent.append(text)

            def recv(self):
                item = queue.pop(0) if queue else {"error": True}
                if item is None or item.get("error"):
                    raise RuntimeError("no frame")
                if "hex" in item:
                    return bytes.fromhex(item["hex"])
                return item["text"]

            def settimeout(self, value):
                pass

            def close(self):
                pass

        def create_connection(url, **kwargs):
            driver.connections.append(url)
            return Fake()

        self.websocket.create_connection = create_connection
        out = {}
        try:
            bd = self.data.BrokerData(AUTH_TOKEN)
            out["result"] = bd.get_depth(step["symbol"], step["exchange"])
        except Exception as e:
            out["error"] = str(e)
        out["sent"] = list(self.sent)
        out["connections"] = list(self.connections)
        self.sent.clear()
        self.connections.clear()
        return out


def h(data):
    return {"hex": data.hex()}


def d(symbol="SBIN", exchange="NSE", *frames, **extra):
    return {"depth": {"symbol": symbol, "exchange": exchange, "frames": list(frames), **extra}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


T = {"text": "ok"}
SCENARIOS = [
    scenario("a_depth_snapshot", d("SBIN", "NSE", h(snap_packet()))),
    scenario("a_depth_snapshot_after_a_login_reply", d("SBIN", "NSE", h(snap_packet()), login_reply={"text": "login ok"})),
    scenario("a_depth_snapshot_after_a_text_frame", d("SBIN", "NSE", T, h(snap_packet()))),
    scenario("a_depth_snapshot_after_two_text_frames", d("SBIN", "NSE", T, T, h(snap_packet()))),
    scenario("a_depth_snapshot_after_three_text_frames_is_too_late", d("SBIN", "NSE", T, T, T, h(snap_packet()))),
    scenario("a_depth_snapshot_in_a_frame_with_a_header", d("SBIN", "NSE", h(framed([snap_packet()])))),
    scenario("a_depth_snapshot_in_a_frame_of_two_takes_the_first", d("SBIN", "NSE", h(framed([snap_packet(ltp=80500), snap_packet(ltp=80600)])))),
    scenario("the_first_readable_quote_is_the_answer_even_without_depth", d("SBIN", "NSE", h(ltp_packet()), h(snap_packet()))),
    scenario("a_quote_packet_has_no_ladder", d("SBIN", "NSE", h(quote_packet()))),
    scenario("a_frame_that_cannot_be_read_is_skipped", d("SBIN", "NSE", h(b"\x01" * 10), h(snap_packet()))),
    scenario("a_frame_whose_token_is_not_text_is_skipped", d("SBIN", "NSE", h(quote_packet(b"\xff\xfe" + b"\x00" * 23)), h(snap_packet()))),
    scenario("an_empty_frame_is_skipped", d("SBIN", "NSE", h(b""), h(snap_packet()))),
    scenario("a_packet_for_another_token_is_still_the_answer", d("SBIN", "NSE", h(snap_packet("9999")))),
    scenario("a_short_ladder_is_padded_to_five", d("SBIN", "NSE", h(snap_packet(bids=[(100, 80540, 1), (200, 80535, 2)], asks=[(110, 80550, 1)])))),
    scenario("an_empty_ladder_is_five_empty_levels", d("SBIN", "NSE", h(snap_packet(bids=[], asks=[])))),
    scenario("a_ladder_with_a_zero_level_keeps_it", d("SBIN", "NSE", h(snap_packet(bids=[(0, 0, 0)] * 5)))),
    scenario("totals_with_decimals_are_cut", d("SBIN", "NSE", h(snap_packet(buy=45000.9, sell=52000.5)))),
    scenario("totals_that_are_not_numbers_are_an_error", d("SBIN", "NSE", h(snap_packet(buy=float("nan"))))),
    scenario("a_future", d("NIFTY25NOV26FUT", "NFO", h(snap_packet("49500", exchange=2, oi=7000000, ltp=2410025)))),
    scenario("a_bse_equity", d("TATAMOTORS", "BSE", h(snap_packet("500570", exchange=3)))),
    scenario("a_bse_future", d("SENSEX26NOVFUT", "BFO", h(snap_packet("60001", exchange=4)))),
    scenario("a_currency_future", d("USDINR25NOV26FUT", "CDS", h(snap_packet("90001", exchange=13)))),
    scenario("a_commodity_future", d("GOLDM25NOV26FUT", "MCX", h(snap_packet("80001", exchange=5)))),
    scenario("an_nse_index", d("NIFTY", "NSE_INDEX", h(snap_packet("26000")))),
    scenario("a_bse_index", d("SENSEX", "BSE_INDEX", h(snap_packet("19000", exchange=3)))),
    scenario("an_exchange_the_stream_does_not_serve", d("CRUDEOIL25NOV26FUT", "NCO", h(snap_packet("70001")))),
    scenario("nothing_arrives", d("SBIN", "NSE")),
    scenario("only_text_arrives", d("SBIN", "NSE", T, T, T)),
    scenario("an_error_while_waiting_ends_the_wait", d("SBIN", "NSE", {"error": True}, h(snap_packet()))),
    scenario("a_login_reply_that_never_comes_is_no_obstacle", d("SBIN", "NSE", h(snap_packet()), login_reply=None)),
    scenario("a_quote_with_every_value_zero", d("SBIN", "NSE", h(snap_packet(ltp=0, ltq=0, avg=0, volume=0, buy=0.0, sell=0.0, o=0, h=0, low=0, close=0, oi=0)))),
    scenario("a_large_volume", d("SBIN", "NSE", h(snap_packet(volume=9_000_000_000_000)))),
    scenario("a_depth_asked_for_twice", d("SBIN", "NSE", h(snap_packet())), d("INFY", "NSE", h(snap_packet("1594", ltp=150010)))),
]
