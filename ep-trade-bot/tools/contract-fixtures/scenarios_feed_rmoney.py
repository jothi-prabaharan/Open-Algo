"""RMoney (XTS) feed: the Wisdom scenarios and RMoney's own, recorded from its adapter and client (batched subscriptions, numeric ids, its own event routing)."""

import copy
import json

import scenarios_feed_wisdom as w
from scenarios_feed_wisdom import NOW_MS, SYMBOLS as _SYMBOLS, ENV, ltp, quote, depth, sub, event  # noqa: F401

BROKER = "rmoney"
SYMBOLS = _SYMBOLS + [w.row(f"S{i:03d}", str(5000 + i)) for i in range(60)] + [w.row("NONNUM", "ABC123")]

EVENTS = {
    "1501-json-full": "_on_touchline_full", "1501-json-partial": "_on_touchline_partial", "1502-json-full": "_on_depth_full", "1502-json-partial": "_on_depth_partial",
    "1505-json-full": "_on_candle_full", "1505-json-partial": "_on_candle_partial", "1510-json-full": "_on_oi_full", "1510-json-partial": "_on_oi_partial",
    "1512-json-full": "_on_ltp_full", "1512-json-partial": "_on_ltp_partial", "1105-json-full": "_on_binary_full", "1105-json-partial": "_on_binary_partial",
}


class Driver(w.Driver):
    def __init__(self):
        super().__init__("rmoney", "RMoneyWebSocketAdapter", "RMoneyWebSocketClient")

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

            def close(self):
                pass

        def record(method):
            def send(url, json=None, headers=None, timeout=None):
                from urllib.parse import urlparse

                driver.http.append({"method": method, "path": urlparse(url).path, "body": json})
                quotes, driver.quotes = driver.quotes, []
                return Reply(quotes)

            return send

        adapter = self.adapter_cls()
        client = self.client_cls("market-key", "market-secret", "XTSUSER")
        client._http_session = type("S", (), {"post": staticmethod(record("POST")), "put": staticmethod(record("PUT")), "close": staticmethod(lambda: None)})()
        client.market_data_token, client.actual_user_id = "market-token", "XTSUSER"
        client.connected = True
        client.on_data = adapter._on_data
        adapter.ws_client, adapter.connected, adapter.running = client, True, True
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        adapter.disconnect = lambda: None
        # Queued subscriptions are sent when the step ends, not by a timer.
        adapter._start_batch_timer_locked = lambda: None
        self.adapter, self.client = adapter, client

    def _take(self):
        if self.adapter.subscription_queue:
            self.adapter._process_batch_subscriptions()
        return super()._take()

    def event(self, step):
        # The client writes the event's message code into what it is given; the recording keeps the payload as it was sent.
        getattr(self.client, EVENTS[step["name"]])(copy.deepcopy(step["payload"]))
        return self._take()


def ev(name, payload):
    return {"event": {"name": name, "payload": payload}}


def lower(d):
    out = {}
    for k, v in d.items():
        out[k[0].lower() + k[1:] if k in ("ExchangeSegment", "ExchangeInstrumentID", "MessageCode") else k] = v
    return out


def no_code(d):
    return {k: v for k, v in d.items() if k != "MessageCode"}


SCENARIOS = [sc for sc in w.SCENARIOS if sc["name"] not in ()] + [
    {"name": "sixty_instruments_go_in_two_requests", "steps": [{"subscribe": [sub(f"S{i:03d}", 2) for i in range(60)]}]},
    {"name": "modes_are_grouped_into_requests", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("SBIN", 2), sub("INFY", 1), sub("NIFTY25NOV26FUT", 2, "NFO"), sub("RELIANCE", 3)]}]},
    {"name": "a_token_that_is_not_a_number_is_sent_as_text", "steps": [{"subscribe": [sub("NONNUM", 2)]}]},
    {"name": "camel_case_keys", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1501-json-full", lower(quote()))]},
    {"name": "segment_as_a_name", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1501-json-full", quote(ExchangeSegment="NSECM"))]},
    {"name": "segment_as_digits_in_text", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1501-json-full", quote(ExchangeSegment="1"))]},
    {"name": "payload_code_is_replaced_by_the_event_name", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, ev("1512-json-full", quote())]},
    {"name": "touchline_event_for_an_ltp_subscriber_is_an_ltp", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, ev("1501-json-full", quote())]},
    {"name": "touchline_event_for_a_depth_subscriber_is_a_quote", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1501-json-full", quote())]},
    {"name": "touchline_event_for_ltp_and_quote_subscribers_is_a_quote", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("RELIANCE", 2)]}, ev("1501-json-full", quote())]},
    {"name": "depth_event_for_a_quote_subscriber", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1502-json-full", depth())]},
    {"name": "candle_event_is_not_published", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1505-json-full", quote())]},
    {"name": "open_interest_event_is_not_published", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1510-json-partial", quote())]},
    {"name": "an_event_name_with_no_code", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, {"event": {"name": "1501-json-full", "payload": "not json"}}]},
    {"name": "depth_with_bid_and_ask_info", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1502-json-full", {k: v for k, v in depth().items() if k not in ("Bids", "Asks")} | {"BidInfo": depth()["Bids"], "AskInfo": depth()["Asks"]})]},
    {"name": "depth_with_empty_bids_falls_back_to_bid_info", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1502-json-full", depth(Bids=[], BidInfo=depth()["Bids"]))]},
    {"name": "depth_with_a_book_inside_the_touchline", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1502-json-full", {"MessageCode": 1502, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "Touchline": dict(depth()["Touchline"], Bids=depth()["Bids"], Asks=depth()["Asks"])})]},
    {"name": "depth_with_one_side_only", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1502-json-full", {k: v for k, v in depth().items() if k != "Asks"})]},
    {"name": "depth_with_no_book_at_all", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1502-json-full", {k: v for k, v in depth().items() if k not in ("Bids", "Asks")})]},
    {"name": "quote_with_a_touchline_and_average_price", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1501-json-full", {"MessageCode": 1501, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "Touchline": dict(depth()["Touchline"], AveragePrice=1448.9)})]},
    {"name": "legacy_text_event", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, ev("1105-json-full", "t:1_2885,110:1450.5,111:25,112:1234567,113:1448.2,114:1440,115:1455,116:1438.5,117:1445,118:50000,119:40000,120:abc")]},
    {"name": "legacy_text_event_for_an_instrument_nobody_subscribed", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, ev("1105-json-partial", "t:1_3045,110:1450.5")]},
    {"name": "legacy_text_event_that_is_malformed", "steps": [{"subscribe": [sub("RELIANCE", 1)]}, ev("1105-json-full", "t:1_2885"), ev("1105-json-full", "x:1_2885,110:1"), ev("1105-json-full", "t:12885,110:1")]},
    {"name": "initial_quote_without_a_code_takes_the_subscription_code", "steps": [{"quotes_next": [no_code(quote())]}, {"subscribe": [sub("RELIANCE", 2)]}]},
    {"name": "initial_quote_without_a_code_for_ltp", "steps": [{"quotes_next": [no_code(ltp())]}, {"subscribe": [sub("RELIANCE", 1)]}]},
    {"name": "event_name_decides_the_mode_not_the_payload", "steps": [{"subscribe": [sub("RELIANCE", 1), sub("RELIANCE", 2)]}, ev("1512-json-full", quote())]},
    {"name": "legacy_text_event_for_a_quote_subscriber", "steps": [{"subscribe": [sub("RELIANCE", 2)]}, ev("1105-json-full", "t:1_2885,110:1450.5,113:1448.2")]},
    {"name": "touchline_without_quote_fields_for_a_depth_subscriber", "steps": [{"subscribe": [sub("RELIANCE", 3)]}, ev("1501-json-full", {"MessageCode": 1501, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5})]},
    {"name": "initial_quote_with_a_camel_case_code", "steps": [{"quotes_next": [{"messageCode": 1501, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5, "Open": 1}]}, {"subscribe": [sub("RELIANCE", 2)]}]},
    {"name": "initial_quote_with_an_xts_code_key", "steps": [{"quotes_next": [{"xtsMessageCode": 1512, "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5}]}, {"subscribe": [sub("RELIANCE", 1), sub("RELIANCE", 2)]}]},
    {"name": "initial_quote_with_a_code_in_text", "steps": [{"quotes_next": [{"MessageCode": "1512", "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5}]}, {"subscribe": [sub("RELIANCE", 1), sub("RELIANCE", 2)]}]},
    {"name": "initial_quote_with_a_code_that_is_not_a_number", "steps": [{"quotes_next": [{"MessageCode": "abc", "ExchangeSegment": 1, "ExchangeInstrumentID": 2885, "LastTradedPrice": 1450.5}]}, {"subscribe": [sub("RELIANCE", 1)]}]},
]
