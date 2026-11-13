"""Upstox feed: what the market-data socket is sent to subscribe (JSON in binary frames) and what the adapter publishes for the protobuf frames it gets back."""

import json

BROKER = "upstox"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "upstox-api-key", "BROKER_API_SECRET": "upstox-secret"}


def row(symbol, brexchange, token, exchange="NSE", instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "NSE_EQ", "NSE_EQ|INE062A01020"),
    row("INFY", "NSE_EQ", "NSE_EQ|INE009A01021"),
    row("TCS", "NSE_EQ", "NSE_EQ|INE467B01029"),
    row("NIFTY25NOV26FUT", "NSE_FO", "NSE_FO|35001", "NFO", "FUTIDX"),
    row("NIFTY25NOV2624000CE", "NSE_FO", "NSE_FO|40001", "NFO", "OPTIDX"),
    row("NIFTY", "NSE_INDEX", "NSE_INDEX|Nifty 50", "NSE_INDEX", "INDEX"),
    row("RELIANCE", "BSE_EQ", "BSE_EQ|INE002A01018", "BSE"),
    row("PLAIN", "NSE_EQ", "99999"),
]
# Enough symbols to meet the per-mode and combined caps.
MANY = [row(f"SYM{i:04d}", "NSE_EQ", f"NSE_EQ|INE{i:06d}") for i in range(2002)]
SYMBOLS += MANY


def pb():
    from broker.upstox.streaming import MarketDataFeedV3_pb2 as m
    return m


def ltpc(ltp=805.45, ltt=1793699999000, ltq=5, cp=799.95, iep=None):
    m = pb()
    msg = m.LTPC(ltp=ltp, ltt=ltt, ltq=ltq, cp=cp)
    if iep is not None:
        msg.iep.value = iep
    return msg


def ohlcs(*rows_):
    m = pb()
    return m.MarketOHLC(ohlc=[m.OHLC(interval=i, open=o, high=h, low=l, close=c, vol=v, ts=t) for i, o, h, l, c, v, t in rows_])


DAILY = ("1d", 800.0, 810.0, 795.5, 799.95, 1234567, 1793600000000)
MINUTE = ("I1", 804.0, 806.0, 803.0, 805.0, 1000, 1793699940000)


def levels(*pairs):
    m = pb()
    return m.MarketLevel(bidAskQuote=[m.Quote(bidQ=bq, bidP=bp, askQ=aq, askP=ap) for bq, bp, aq, ap in pairs])


def market_feed(**extra):
    m = pb()
    ff = m.MarketFullFeed(ltpc=extra.pop("ltpc", ltpc()), marketOHLC=extra.pop("ohlc", ohlcs(DAILY, MINUTE)), atp=803.1, vtt=1234567, oi=0.0, tbq=170.0, tsq=97.0)
    if "level" in extra:
        ff.marketLevel.CopyFrom(extra.pop("level"))
    for key, value in extra.items():
        setattr(ff, key, value)
    return m.Feed(fullFeed=m.FullFeed(marketFF=ff), requestMode=m.full_d5)


def index_feed(ltp=24050.5):
    m = pb()
    return m.Feed(fullFeed=m.FullFeed(indexFF=m.IndexFullFeed(ltpc=ltpc(ltp), marketOHLC=ohlcs(("1d", 24000.0, 24100.0, 23900.0, 23950.0, 0, 1793600000000)))), requestMode=m.full_d5)


def ltpc_feed(**kw):
    m = pb()
    return m.Feed(ltpc=ltpc(**kw), requestMode=m.ltpc)


def response(feeds, current_ts=1793699999999, kind=1):
    m = pb()
    out = m.FeedResponse(type=kind, currentTs=current_ts)
    for key, feed in feeds.items():
        out.feeds[key].CopyFrom(feed)
    return out.SerializeToString().hex()


def market_info(feeds=None, **maps):
    m = pb()
    out = m.FeedResponse(type=m.market_info, currentTs=1793699999999)
    for key, feed in (feeds or {}).items():
        out.feeds[key].CopyFrom(feed)
    for segment, status in maps.get("segment", {}).items():
        out.marketInfo.segmentStatus[segment] = status
    for segment, status in maps.get("cas", {}).items():
        out.marketInfo.casMarketStatus[segment].status = status
        out.marketInfo.casMarketStatus[segment].updatedTime = 1793699999000
    return out.SerializeToString().hex()


def varint(n):
    out = b""
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out += bytes([b | 0x80])
        else:
            return out + bytes([b])


def ld(field, payload):
    return varint(field << 3 | 2) + varint(len(payload)) + payload


def fv(field, n):
    return varint(field << 3) + varint(n)


def f64(field, x):
    import struct
    return varint(field << 3 | 1) + struct.pack("<d", x)


def raw_quote_with_explicit_defaults():
    """A sender that writes zero values on the wire and a field this schema does not know: the reader must treat them as absent and skip them."""
    ltpc_bytes = f64(1, 805.45) + fv(3, 5) + fv(99, 7)
    ff = ld(1, ltpc_bytes) + f64(5, 803.1) + f64(9, 170.0) + f64(10, 97.0) + f64(12, 0.0) + fv(13, 0) + fv(14, 0) + fv(15, 0) + fv(16, 0) + fv(98, 1)
    feed = ld(2, ld(1, ff))
    entry = ld(1, b"NSE_EQ|INE062A01020") + ld(2, feed)
    return (fv(1, 1) + ld(2, entry) + fv(3, 1793699999999)).hex()


def raw_entry_longer_than_the_frame():
    """A map entry that says it is 40 bytes longer than what follows: a frame cut off in transit."""
    entry = ld(1, b"NSE_EQ|INE062A01020") + ld(2, ld(1, f64(1, 805.45)))
    return (fv(1, 1) + varint(2 << 3 | 2) + varint(len(entry) + 40) + entry).hex()


class Driver:
    """Runs the real adapter and its real client with a fake socket."""

    def __init__(self):
        import websocket_proxy  # noqa: F401  (imports the adapters in the order they need)
        from broker.upstox.streaming.upstox_adapter import UpstoxWebSocketAdapter
        from broker.upstox.streaming.upstox_client import UpstoxWebSocketClient

        self.adapter_cls, self.client_cls = UpstoxWebSocketAdapter, UpstoxWebSocketClient

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        client = self.client_cls("upstox-token")
        sent = self.sent

        class FakeSocket:
            def send(self, data, opcode=None):
                message = json.loads(data.decode() if isinstance(data, bytes) else data)
                message["guid"] = "<guid>"
                sent.append(message)

        client.ws = FakeSocket()
        client._connected = True
        client.running = True
        client._start_health_check = lambda: None
        adapter.ws_client = client
        adapter._wire_callbacks()
        adapter.connected, adapter.running = True, True
        adapter._start_batch_timer = lambda: None
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.client = adapter, client

    def _take(self):
        out = {"sent": list(self.sent), "published": list(self.published)}
        self.sent.clear()
        self.published.clear()
        return out

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        self.adapter._process_batch_subscriptions()
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def reconnect(self, _):
        self.client._on_ws_open(None)
        return self._take()

    def frame(self, hex_text):
        self.client._on_ws_message(None, bytes.fromhex(hex_text))
        return self._take()

    def text(self, message):
        self.client._on_ws_message(None, message if isinstance(message, str) else json.dumps(message))
        return self._take()


def sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def frame(data):
    return {"frame": data}


SBIN_KEY, INFY_KEY, FUT_KEY, IDX_KEY = "NSE_EQ|INE062A01020", "NSE_EQ|INE009A01021", "NSE_FO|35001", "NSE_INDEX|Nifty 50"

SCENARIOS = [
    {"name": "subscribe_in_each_mode", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("NIFTY25NOV26FUT", 3, "NFO")]}]},
    {"name": "quote_and_depth_share_one_full_message", "steps": [{"subscribe": [sub("SBIN", 2), sub("INFY", 3), sub("TCS", 2)]}]},
    {"name": "ltp_and_quote_are_separate_messages", "steps": [{"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("TCS", 1)]}]},
    {"name": "subscribe_one_at_a_time", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 2)]}, {"subscribe": [sub("SBIN", 3)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 1)]}, {"subscribe": [sub("SBIN", 1)]}]},
    {"name": "a_mode_the_exchange_does_not_have", "steps": [{"subscribe": [sub("SBIN", 4), sub("SBIN", 0)]}]},
    {"name": "an_index_and_a_bse_equity_and_a_plain_token", "steps": [{"subscribe": [sub("NIFTY", 1, "NSE_INDEX"), sub("RELIANCE", 2, "BSE"), sub("PLAIN", 1)]}]},
    {"name": "unsubscribe_always_sends_the_key", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, {"unsubscribe": sub("SBIN", 1)}, {"unsubscribe": sub("SBIN", 1)}]},
    {"name": "unsubscribe_something_never_subscribed", "steps": [{"unsubscribe": sub("SBIN", 1)}]},
    {"name": "reconnect_repeats_every_subscription_by_mode", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 2), sub("TCS", 3), sub("NIFTY25NOV26FUT", 1, "NFO")]}, {"unsubscribe": sub("INFY", 2)}, {"reconnect": None}]},
    {"name": "the_per_mode_cap_drops_what_does_not_fit", "steps": [{"subscribe": [sub(f"SYM{i:04d}", 2) for i in range(2001)]}]},
    {"name": "the_combined_cap_trims_a_second_mode", "steps": [
        {"subscribe": [sub(f"SYM{i:04d}", 1) for i in range(1000)]}, {"subscribe": [sub(f"SYM{i:04d}", 2) for i in range(1000, 1600)]}]},
    {"name": "quote_and_depth_of_one_symbol_in_one_batch", "steps": [{"subscribe": [sub("SBIN", 2), sub("SBIN", 3)]}]},
    {"name": "the_same_subscription_twice_publishes_once", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 1)]}, frame(response({SBIN_KEY: ltpc_feed()}))]},
    {"name": "unsubscribing_frees_room_under_the_combined_cap", "steps": [
        {"subscribe": [sub(f"SYM{i:04d}", 1) for i in range(1000)]}, {"subscribe": [sub(f"SYM{i:04d}", 2) for i in range(1000, 1500)]},
        {"unsubscribe": sub("SYM1000", 2)}, {"subscribe": [sub("SYM1600", 2), sub("SYM1601", 2)]}]},
    {"name": "a_frame_keyed_differently_is_matched_by_the_token", "steps": [{"subscribe": [sub("PLAIN", 1)]}, frame(response({"NSE_FO|99999": ltpc_feed()}))]},
    {"name": "a_market_status_frame_that_carries_feeds_publishes_nothing", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, frame(market_info(feeds={SBIN_KEY: ltpc_feed()}, segment={"NSE_EQ": 2}))]},
    {"name": "depth_level_with_a_quantity_and_no_price_is_left_out", "steps": [
        {"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed(level=levels((50, 0.0, 0, 0.0), (120, 805.4, 40, 0.0)))}))]},
    {"name": "the_daily_candle_is_found_after_others", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed(ohlc=ohlcs(MINUTE, DAILY))}))]},
    {"name": "an_indicative_price_is_not_carried_to_a_later_tick", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: ltpc_feed(iep=806.25)}))]},
    {"name": "zero_values_on_the_wire_and_unknown_fields_read_as_absent", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(raw_quote_with_explicit_defaults())]},
    {"name": "a_frame_cut_short_publishes_nothing", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed()})[:-30])]},
    {"name": "a_length_that_runs_past_the_frame_publishes_nothing", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(raw_entry_longer_than_the_frame())]},
    {"name": "ltp_frame", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({SBIN_KEY: ltpc_feed()}))]},
    {"name": "ltp_frame_with_an_indicative_price", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({SBIN_KEY: ltpc_feed(iep=806.25)}))]},
    {"name": "ltp_frame_with_an_indicative_price_of_zero", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({SBIN_KEY: ltpc_feed(iep=0.0)}))]},
    {"name": "quote_frame", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed()}))]},
    {"name": "quote_frame_without_a_daily_candle_uses_the_first", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed(ohlc=ohlcs(MINUTE))}))]},
    {"name": "quote_frame_with_no_candles", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed(ohlc=ohlcs())}))]},
    {"name": "index_quote_frame", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX")]}, frame(response({IDX_KEY: index_feed()}))]},
    {"name": "quote_frame_in_an_auction_carries_the_indicative_fields", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed(iep=806.5, rp=800.0, ieq=1200, iiqTotal=-4200, iiqM=300, casEligible=True)}))]},
    {"name": "quote_frame_indicative_price_from_the_ltpc_wrapper", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed(ltpc=ltpc(iep=807.0))}))]},
    {"name": "depth_frame", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed(level=levels((120, 805.4, 80, 805.5), (50, 805.35, 10, 805.55), (30, 805.3, 7, 805.6), (20, 805.25, 5, 805.65), (10, 805.2, 3, 805.7)))}))]},
    {"name": "depth_frame_with_seven_unsorted_levels", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed(level=levels(
        (1, 805.1, 1, 805.9), (2, 805.4, 2, 805.5), (3, 805.3, 3, 805.6), (4, 805.2, 4, 805.7), (5, 805.35, 5, 805.55), (6, 805.0, 6, 806.0), (7, 805.45, 7, 805.45)))}))]},
    {"name": "depth_frame_with_empty_sides_is_padded", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed(level=levels((120, 805.4, 0, 0.0), (0, 0.0, 80, 805.5)))}))]},
    {"name": "depth_frame_without_levels", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed()}))]},
    {"name": "depth_frame_in_an_auction", "steps": [{"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: market_feed(level=levels((1, 805.4, 1, 805.5)), iep=806.0, iiqTotal=-5)}))]},
    {"name": "a_full_frame_reaches_an_ltp_subscriber_with_nothing_but_names", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({SBIN_KEY: market_feed()}))]},
    {"name": "an_ltp_frame_reaches_a_quote_subscriber_through_the_cached_price", "steps": [
        {"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed()})), frame(response({SBIN_KEY: ltpc_feed(ltp=806.0)}))]},
    {"name": "an_ltp_frame_before_any_cache_reaches_a_quote_subscriber_as_nothing", "steps": [{"subscribe": [sub("SBIN", 2)]}, frame(response({SBIN_KEY: ltpc_feed()}))]},
    {"name": "an_ltp_frame_reaches_a_depth_subscriber_through_the_cached_price", "steps": [
        {"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: ltpc_feed(ltp=806.0)}))]},
    {"name": "a_depth_frame_with_a_zero_price_takes_the_cached_price", "steps": [
        {"subscribe": [sub("SBIN", 3)]}, frame(response({SBIN_KEY: ltpc_feed(ltp=806.0)})), frame(response({SBIN_KEY: market_feed(ltpc=ltpc(ltp=0.0), level=levels((1, 805.4, 1, 805.5)))}))]},
    {"name": "two_modes_of_one_instrument_each_get_a_tick", "steps": [{"subscribe": [sub("SBIN", 1), sub("SBIN", 2)]}, frame(response({SBIN_KEY: market_feed()}))]},
    {"name": "one_frame_with_two_instruments", "steps": [
        {"subscribe": [sub("SBIN", 1), sub("INFY", 1)]}, frame(response({SBIN_KEY: ltpc_feed(), INFY_KEY: ltpc_feed(ltp=1500.1)}))]},
    {"name": "a_plain_token_is_matched_by_its_key", "steps": [{"subscribe": [sub("PLAIN", 1)]}, frame(response({"NSE_EQ|99999": ltpc_feed()}))]},
    {"name": "frame_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({INFY_KEY: ltpc_feed()}))]},
    {"name": "frame_after_everything_was_unsubscribed", "steps": [{"subscribe": [sub("SBIN", 1)]}, {"unsubscribe": sub("SBIN", 1)}, frame(response({SBIN_KEY: ltpc_feed()}))]},
    {"name": "market_status_frames_publish_nothing", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, frame(market_info(segment={"NSE_EQ": 2}, cas={"NSE_EQ": "CAS_LM_START"}))]},
    {"name": "a_frame_with_no_feeds", "steps": [{"subscribe": [sub("SBIN", 1)]}, frame(response({}))]},
    {"name": "frames_that_carry_no_market_data", "steps": [
        {"subscribe": [sub("SBIN", 1)]}, {"frame": ""}, {"frame": "ffff"}, {"text": "not json"}, {"text": {"status": "failed", "method": "sub", "error": "bad key"}}, {"text": {"status": "success"}}]},
]
