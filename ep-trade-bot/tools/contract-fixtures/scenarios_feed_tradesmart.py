"""TradeSmart feed (Noren websocket): the same scenarios as Shoonya's, recorded from Flattrade's own adapter and socket client."""

import json

import scenarios_feed_shoonya as s
from scenarios_feed_shoonya import NOW_MS, SYMBOLS, sub, frame, touch, depth, AK, SCENARIOS  # noqa: F401

BROKER = "tradesmart"
TOKEN = "tradesmart-session-token"
ENV = {"BROKER_API_KEY": "FA12345:::tradesmart-api-key", "BROKER_API_SECRET": "tradesmart-secret"}


class Driver:
    def __init__(self):
        import websocket_proxy  # noqa: F401
        from broker.tradesmart.streaming.tradesmart_adapter import TradeSmartWebSocketAdapter
        from broker.tradesmart.streaming.tradesmart_websocket import TradeSmartWebSocket

        self.adapter_cls, self.ws_cls = TradeSmartWebSocketAdapter, TradeSmartWebSocket

    def reset(self):
        self.sent, self.published = [], []
        adapter = self.adapter_cls()
        sent = self.sent

        class FakeSocket:
            def send(self, text):
                sent.append(json.loads(text))

            def close(self):
                pass

        adapter.actid, adapter.accesstoken = "FA12345", TOKEN
        ws = self.ws_cls(user_id="FA12345", actid="FA12345", accesstoken=TOKEN, on_message=adapter._on_message, on_error=adapter._on_error, on_close=adapter._on_close, on_open=adapter._on_open)
        ws.ws = FakeSocket()
        ws.running = ws.connected = True
        ws._start_heartbeat = lambda: None
        adapter.ws_client = ws
        adapter.running = adapter.connected = True
        import threading as real_threading

        class NoTimer:
            def __init__(self, *a, **k):
                pass

            daemon = True

            def start(self):
                pass

            def cancel(self):
                pass

        import broker.tradesmart.streaming.tradesmart_adapter as module

        module.threading = type("T", (), {**{k: getattr(real_threading, k) for k in dir(real_threading) if not k.startswith("__")}, "Timer": NoTimer})
        adapter.publish_market_data = lambda topic, data: self.published.append({"topic": topic, "data": data})
        self.adapter, self.ws = adapter, ws

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

    def signed_out(self, _):
        self.adapter.connected = self.ws.connected = False
        return self._take()

    def reconnect(self, _):
        self.adapter.connected = self.ws.connected = False
        self.ws._on_open(self.ws.ws)
        return self._take()

    def text(self, message):
        self.ws._on_message(self.ws.ws, message if isinstance(message, str) else json.dumps(message))
        return self._take()
