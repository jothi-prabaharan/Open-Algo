"""Wisdom (XTS) feed: the HTTP calls that subscribe and unsubscribe, and what the adapter publishes for the events and initial quotes it gets back."""

import json

BROKER = "wisdom"
NOW_MS = 1_793_700_000_123
ENV = {"BROKER_API_KEY": "wisdom-trading-key", "BROKER_API_SECRET": "wisdom-trading-secret", "BROKER_API_KEY_MARKET": "wisdom-market-key", "BROKER_API_SECRET_MARKET": "wisdom-market-secret"}


def row(symbol, token, exchange="NSE", brexchange=None, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": symbol, "name": symbol, "exchange": exchange, "brexchange": brexchange or exchange, "token": token,
            "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("RELIANCE", "2885"),
    row("SBIN", "3045"),
    row("INFY", "1594"),
    row("NIFTY", "26000", "NSE_INDEX", "NSE_INDEX", "INDEX"),
    row("NIFTY25NOV26FUT", "35001", "NFO", "NFO", "FUTIDX"),
    row("USDINR25NOV26FUT", "60001", "CDS", "CDS", "FUTCUR"),
    row("RELIANCE", "500325", "BSE", "BSE"),
    row("SENSEX", "19000", "BSE_INDEX", "BSE_INDEX", "INDEX"),
    row("SENSEX25NOV26FUT", "70001", "BFO", "BFO", "FUTIDX"),
    row("CRUDEOIL25NOV26FUT", "80001", "MCX", "MCX", "FUTCOM"),
]


def ltp(segment=1, token=2885, price=1450.5, **extra):
    d = {"MessageCode": 1512, "ExchangeSegment": segment, "ExchangeInstrumentID": token, "LastTradedPrice": price, "LastTradedTime": 1793700000, "LastTradedQunatity": 25}
    d.update(extra)
    return d


def quote(segment=1, token=2885, price=1450.5, **extra):
    d = {"MessageCode": 1501, "ExchangeSegment": segment, "ExchangeInstrumentID": token, "LastTradedPrice": price, "LastTradedTime": 1793700000, "LastTradedQunatity": 25,
         "TotalTradedQuantity": 1234567, "Open": 1440.0, "High": 1455.0, "Low": 1438.5, "Close": 1445.0, "AveragePrice": 1448.2, "TotalBuyQuantity": 50000, "TotalSellQuantity": 40000}
    d.update(extra)
    return d


def depth(segment=1, token=2885, levels=5, **extra):
    d = {"MessageCode": 1502, "ExchangeSegment": segment, "ExchangeInstrumentID": token,
         "Touchline": {"LastTradedPrice": 1450.5, "LastTradedTime": 1793700000, "TotalTradedQuantity": 1234567, "Open": 1440.0, "High": 1455.0, "Low": 1438.5, "Close": 1445.0,
                       "LastTradedQunatity": 25, "AverageTradedPrice": 1448.2, "TotalBuyQuantity": 50000, "TotalSellQuantity": 40000},
         "Bids": [{"Price": 1450.4 - 0.05 * i, "Size": 100 * (i + 1), "TotalOrders": i + 1} for i in range(levels)],
         "Asks": [{"Price": 1450.6 + 0.05 * i, "Size": 110 * (i + 1), "TotalOrders": 2 * (i + 1)} for i in range(levels)],
         "OpenInterest": 1500, "UpperCircuitLimit": 1600.0, "LowerCircuitLimit": 1300.0}
    d.update(extra)
    return d


class Driver:
    """Runs the real adapter and its real client; the Socket.IO connection is nothing and the HTTP calls are recorded."""

    def __init__(self, slug="wisdom", adapter="WisdomWebSocketAdapter", client="WisdomWebSocketClient"):
        import importlib

        import websocket_proxy  # noqa: F401

        self.ws_module = importlib.import_module(f"broker.{slug}.streaming.{slug}_websocket")
        self.adapter_cls = getattr(importlib.import_module(f"broker.{slug}.streaming.{slug}_adapter"), adapter)
        self.client_cls = getattr(self.ws_module, client)

    def reset(self):
        self.http, self.published, self.quotes = [], [], []
        driver = self

        class Reply:
            status_code = 200
            text = ""

            def __init__(self, quotes):
                self._quotes = quotes

            def json(self):
                return {"type": "success", "result": {"listQuotes": self._quotes}}

        def record(method):
            def send(url, json=None, headers=None, timeout=None):
                from urllib.parse import urlparse

                driver.http.append({"method": method, "path": urlparse(url).path, "body": json})
                quotes, driver.quotes = driver.quotes, []
                return Reply(quotes)

            return send

        self.ws_module.requests = type("R", (), {"post": staticmethod(record("POST")), "put": staticmethod(record("PUT"))})
        adapter = self.adapter_cls()
        client = self.client_cls("market-key", "market-secret", "XTSUSER")
        client.market_data_token, client.actual_user_id = "market-token", "XTSUSER"
        client.connected = True
        client.on_data = adapter._on_data
        adapter.ws_client, adapter.connected, adapter.running = client, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        # An unsubscribe closes the connection in the adapter; the recording goes on without it.
        adapter.disconnect = lambda: None
        self.adapter, self.client = adapter, client

    def _take(self):
        out = {"http": list(self.http), "published": list(self.published)}
        self.http.clear()
        self.published.clear()
        return out

    def quotes_next(self, quotes):
        self.quotes = [q if isinstance(q, str) else json.dumps(q) for q in quotes]
        return self._take()

    def subscribe(self, items):
        for item in items:
            self.adapter.subscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def unsubscribe(self, item):
        self.adapter.unsubscribe(item["symbol"], item["exchange"], item["mode"])
        return self._take()

    def reconnect(self, _):
        self.client.connected = True
        self.adapter._on_open(None)
        return self._take()

    def event(self, step):
        handler = getattr(self.client, "_on_message_" + step["name"].replace("-", "_"))
        handler(step["payload"])
        return self._take()


def sub(symbol, mode, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange, "mode": mode}


def event(name, payload):
    return {"event": {"name": name, "payload": payload}}


SCENARIOS = [
    {"name": "subscribe_ltp", "steps": [{"subscribe": [sub("RELIANCE", 1)]}]},
    {"name": "subscribe_quote", "steps": [{"subscribe": [sub("RELIANCE", 2)]}]},
    {"name": "subscribe_depth", "steps": [{"subscribe": [sub("RELIANCE", 3)]}]},
    {"name": "subscribe_several_one_call_each", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("SBIN", 2), sub("INFY", 3)]}]},
    {"name": "every_segment", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX"), sub("NIFTY25NOV26FUT", 2, "NFO"), sub("USDINR25NOV26FUT", 2, "CDS"), sub("RELIANCE", 2, "BSE"),
                                                       sub("SENSEX", 2, "BSE_INDEX"), sub("SENSEX25NOV26FUT", 2, "BFO"), sub("CRUDEOIL25NOV26FUT", 2, "MCX")]}]},
    {"name": "a_mode_xts_does_not_have", "steps": [{"subscribe": [sub("RELIANCE", 4)]}, {"subscribe": [sub("RELIANCE", 0)]}]},
    {"name": "the_same_subscription_twice", "steps": [{"subscribe": [sub("RELIANCE", 2), sub("RELIANCE", 2)]}]},
    {"name": "unsubscribe_puts_the_same_code", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, {"unsubscribe": sub("RELIANCE", 1)}]},
    {"name": "unsubscribe_quote_and_depth", "steps": [{"subscribe": [sub("RELIANCE", 2), sub("SBIN", 3)]}, {"unsubscribe": sub("RELIANCE", 2)}, {"unsubscribe": sub("SBIN", 3)}]},
    {"name": "reconnect_subscribes_everything_again", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("SBIN", 2)]}, {"reconnect": None}]},
    {"name": "reconnect_after_an_unsubscribe", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("SBIN", 2)]}, {"unsubscribe": sub("RELIANCE", 1)}, {"reconnect": None}]},
    {"name": "ltp_event", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, event("1512-json-full", ltp())]},
    {"name": "ltp_event_partial", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, event("1512-json-partial", ltp())]},
    {"name": "quote_event", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", quote())]},
    {"name": "quote_event_partial", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-partial", quote())]},
    {"name": "quote_event_with_the_other_spelling_of_quantity", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", {k: v for k, v in quote(LastTradedQuantity=7).items() if k != "LastTradedQunatity"})]},
    {"name": "quote_event_with_the_other_average_price", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", {k: v for k, v in quote(AverageTradedPrice=1447.7).items() if k != "AveragePrice"})]},
    {"name": "quote_event_with_missing_fields", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", {"MessageCode": 1501, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5})]},
    {"name": "quote_event_as_json_text", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", json.dumps(quote()))]},
    {"name": "depth_event_five_levels", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", depth())]},
    {"name": "depth_event_partial_as_json_text", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-partial", json.dumps(depth()))]},
    {"name": "depth_event_with_two_levels_is_padded", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", depth(levels=2))]},
    {"name": "depth_event_with_twenty_levels", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", depth(levels=20))]},
    {"name": "depth_event_with_twenty_five_levels", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", depth(levels=25))]},
    {"name": "depth_event_without_a_book", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", {k: v for k, v in depth().items() if k not in ("Bids", "Asks")})]},
    {"name": "depth_event_with_odd_levels", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", depth(Bids=[{"Price": 1.5}, "x", {"Size": 3}], Asks=[]))]},
    {"name": "depth_event_without_a_touchline", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", {k: v for k, v in depth().items() if k != "Touchline"})]},
    {"name": "index_event", "steps": [{"subscribe": [sub("NIFTY", 2, "NSE_INDEX")]}, event("1501-json-full", quote(1, 26000, 24100.25))]},
    {"name": "futures_event", "steps": [{"subscribe": [sub("NIFTY25NOV26FUT", 2, "NFO")]}, event("1501-json-full", quote(2, 35001, 24005.5))]},
    {"name": "bse_event", "steps": [{"subscribe": [sub("RELIANCE", 2, "BSE")]}, event("1501-json-full", quote(11, 500325, 1450.9))]},
    {"name": "mcx_event", "steps": [{"subscribe": [sub("CRUDEOIL25NOV26FUT", 1, "MCX")]}, event("1512-json-full", ltp(51, 80001, 6100.0))]},
    {"name": "the_same_token_in_two_segments_is_kept_apart", "steps": [{"subscribe": [sub("RELIANCE", 2), sub("RELIANCE", 2, "BSE")]}, event("1501-json-full", quote(1, 2885, 1450.5)), event("1501-json-full", quote(11, 500325, 1450.9))]},
    {"name": "an_event_for_another_mode_is_ignored", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, event("1501-json-full", quote())]},
    {"name": "an_event_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", quote(1, 3045))]},
    {"name": "ltp_and_quote_of_one_instrument", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("RELIANCE", 2)]}, event("1512-json-full", ltp()), event("1501-json-full", quote())]},
    {"name": "an_event_with_an_unknown_segment", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", quote(9, 2885))]},
    {"name": "an_event_with_an_unknown_message_code", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", quote(MessageCode=1999))]},
    {"name": "an_event_with_no_message_code", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", {k: v for k, v in quote().items() if k != "MessageCode"})]},
    {"name": "an_event_with_no_instrument", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, event("1501-json-full", {"MessageCode": 1501, "LastTradedPrice": 1})]},
    {"name": "a_text_event_that_is_not_json", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, event("1502-json-full", "not json")]},
    {"name": "initial_quote_answers_the_subscription", "steps": [{"quotes_next": [quote()]}, {"subscribe": [sub("RELIANCE", 2)]}]},
    {"name": "initial_quotes_of_each_mode", "steps": [{"quotes_next": [ltp()]}, {"subscribe": [sub("RELIANCE", 1)]}, {"quotes_next": [depth()]}, {"subscribe": [sub("RELIANCE", 3)]}]},
    {"name": "initial_quote_that_is_not_json", "steps": [{"quotes_next": ["not json", quote()]}, {"subscribe": [sub("RELIANCE", 2)]}]},
    {"name": "reconnect_initial_quotes", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, {"quotes_next": [quote()]}, {"reconnect": None}]},
]
