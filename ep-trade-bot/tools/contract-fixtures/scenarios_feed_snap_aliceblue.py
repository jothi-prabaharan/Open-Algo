"""AliceBlue quotes, depth and several quotes: nothing is asked for over REST. A market-data socket is subscribed to the instruments, what arrives is merged into a quote or a depth kept for the instrument, and the subscription is given back. Run on the real BrokerData and the real socket class with a fake transport that answers each subscribe with scripted frames."""

import json
import time

NOW_MS = 1_793_700_000_123
BROKER = "aliceblue"
AUTH_TOKEN = "ab-user-session"
CLIENT_ID = "AB1234"
ENV = {"BROKER_API_KEY": "ab-app-code"}


def row(symbol, brsymbol, exchange, token, instrument="EQ"):
    return {"symbol": symbol, "brsymbol": brsymbol, "name": symbol, "exchange": exchange, "brexchange": exchange, "token": str(token), "expiry": "", "strike": -1.0, "lotsize": 1, "instrumenttype": instrument, "tick_size": 0.05}


SYMBOLS = [
    row("SBIN", "SBIN-EQ", "NSE", 3045), row("INFY", "INFY-EQ", "NSE", 1594), row("TATAMOTORS", "TATAMOTORS-EQ", "BSE", 500570), row("NIFTY25NOV26FUT", "NIFTY25NOV26FUT", "NFO", 49500, "FUT"),
    row("NIFTY", "Nifty 50", "NSE_INDEX", 26000, "INDEX"), row("SENSEX", "SENSEX", "BSE_INDEX", 1, "INDEX"), row("ODDTOKEN", "ODDTOKEN-EQ", "NSE", "777.0"),
]
SYMBOLS += [row(f"BULK{i}", f"BULK{i}-EQ", "NSE", 800000 + i) for i in range(105)]


class Driver:
    def __init__(self):
        from broker.aliceblue.api import data as data_module
        from broker.aliceblue.api.alicebluewebsocket import AliceBlueWebSocket

        self.data, self.cls = data_module, AliceBlueWebSocket
        ticks = [0.0]

        def monotonic():
            ticks[0] += 0.05
            return ticks[0]

        time.monotonic = monotonic

    def reset(self):
        self.sent, self.script, self.forced, self.first_down, self.none = [], [], 0, False, False
        self.ws = None

    def _new_socket(self, connected=True):
        driver = self
        ws = self.cls("AB1234", "ab-user-session")
        ws.is_connected = connected

        class Fake:
            def send(self, text):
                frame = json.loads(text)
                driver.sent.append(frame)
                if frame.get("t") in ("t", "d"):
                    frames = driver.script.pop(0) if driver.script else []
                    for item in frames:
                        ws.on_message(None, item if isinstance(item, str) else json.dumps(item))

            def close(self):
                pass

        ws.ws = Fake()
        self.ws = ws
        return ws

    def _socket(self, force_new=False):
        if force_new:
            self.forced += 1
            if self.none:
                return None
            return self._new_socket()
        if self.none:
            return None
        if self.ws is None:
            return self._new_socket(connected=not self.first_down)
        return self.ws

    def _broker_data(self):
        bd = self.data.BrokerData("ab-user-session")
        bd.get_websocket = self._socket
        return bd

    def _run(self, step, call):
        self.script = [list(a) for a in step.get("attempts", [])]
        self.first_down = step.get("first_socket_down", False)
        self.none = step.get("no_socket", False)
        out = {}
        try:
            out["result"] = call(self._broker_data())
        except Exception as e:
            out["error"] = str(e)
        out.update({"sent": list(self.sent), "forced": self.forced})
        self.sent.clear()
        return out

    def quote(self, step):
        return self._run(step, lambda bd: bd.get_quotes(step["symbol"], step["exchange"]))

    def depth(self, step):
        return self._run(step, lambda bd: bd.get_depth(step["symbol"], step["exchange"]))

    def multiquotes(self, step):
        return self._run(step, lambda bd: bd.get_multiquotes(step["symbols"]))


def tk(token="3045", exchange="NSE", **extra):
    d = {"t": "tk", "e": exchange, "tk": str(token), "ts": "SBIN-EQ", "lp": "805.45", "o": "801.00", "h": "809.50", "l": "799.00", "c": "800.00", "v": "1234567", "ap": "804.10", "pc": "0.68", "ft": "1793699999",
         "oi": "7000", "poi": "6500", "tbq": "50000", "tsq": "61000", "ltq": "25", "bp1": "805.40", "bq1": "100", "sp1": "805.50", "sq1": "110"}
    d.update(extra)
    return d


def tf(token="3045", exchange="NSE", **extra):
    d = {"t": "tf", "e": exchange, "tk": str(token), "lp": "806.00", "pc": "0.75", "v": "1234900", "ft": "1793700001"}
    d.update(extra)
    return d


def dk(token="3045", exchange="NSE", **extra):
    d = {"t": "dk", "e": exchange, "tk": str(token), "ts": "SBIN-EQ", "lp": "805.45", "o": "801", "h": "809.5", "l": "799", "c": "800", "v": "1234567", "ltq": "25", "tbq": "50000", "tsq": "61000", "oi": "7000", "poi": "6500"}
    for i in range(1, 6):
        d[f"bp{i}"], d[f"bq{i}"], d[f"bo{i}"] = f"{805.4 - 0.05 * (i - 1):.2f}", str(100 * i), str(i)
        d[f"sp{i}"], d[f"sq{i}"], d[f"so{i}"] = f"{805.5 + 0.05 * (i - 1):.2f}", str(110 * i), str(2 * i)
    d.update(extra)
    return d


def df(token="3045", exchange="NSE", **extra):
    d = {"t": "df", "e": exchange, "tk": str(token), "lp": "806.00", "bp1": "805.90", "bq1": "50", "bo1": "1"}
    d.update(extra)
    return d


def q(symbol="SBIN", exchange="NSE", *attempts, **extra):
    return {"quote": {"symbol": symbol, "exchange": exchange, "attempts": list(attempts), **extra}}


def d(symbol="SBIN", exchange="NSE", *attempts, **extra):
    return {"depth": {"symbol": symbol, "exchange": exchange, "attempts": list(attempts), **extra}}


def m(symbols, *attempts, **extra):
    return {"multiquotes": {"symbols": symbols, "attempts": list(attempts), **extra}}


def scenario(name, *steps):
    return {"name": name, "steps": list(steps)}


def pair(symbol, exchange="NSE"):
    return {"symbol": symbol, "exchange": exchange}


SCENARIOS = [
    # ---- one quote -----------------------------------------------------------------------------------------------
    scenario("a_quote", q("SBIN", "NSE", [tk()])),
    scenario("a_quote_with_a_feed_tick_after_the_acknowledgement", q("SBIN", "NSE", [tk(), tf()])),
    scenario("a_quote_from_a_feed_tick_alone", q("SBIN", "NSE", [tf(bp1="805.4", sp1="805.5", bq1="9", sq1="8", oi="11", poi="10", tbq="1", tsq="2", ltq="3")])),
    scenario("a_feed_tick_updates_only_what_it_names", q("SBIN", "NSE", [tk(), {"t": "tf", "e": "NSE", "tk": "3045", "bp1": "805.0"}])),
    scenario("a_quote_whose_numbers_are_text_with_decimals_is_still_read", q("SBIN", "NSE", [tk(v="1200", oi="7000.9", poi="6500.4")])),
    scenario("an_open_interest_that_is_empty_is_zero", q("SBIN", "NSE", [tk(oi="", poi="")])),
    scenario("a_quote_of_a_derivative", q("NIFTY25NOV26FUT", "NFO", [tk("49500", "NFO")])),
    scenario("a_quote_of_an_index_asks_the_cash_exchange", q("NIFTY", "NSE_INDEX", [tk("26000", "NSE")])),
    scenario("a_quote_of_the_bse_index", q("SENSEX", "BSE_INDEX", [tk("1", "BSE")])),
    scenario("a_quote_with_a_token_with_a_decimal_point", q("ODDTOKEN", "NSE", [tk("777")])),
    scenario("a_quote_for_another_token_is_not_this_ones", q("SBIN", "NSE", [tk("9999")], [tk()])),
    scenario("a_quote_that_arrives_on_the_second_try", q("SBIN", "NSE", [], [tk()])),
    scenario("a_quote_that_never_arrives", q("SBIN", "NSE", [], [])),
    scenario("a_quote_whose_first_attempt_is_garbage", q("SBIN", "NSE", ["hello"], [tk()])),
    scenario("a_tick_that_cannot_be_read_leaves_no_quote", q("SBIN", "NSE", [tk(lp="n/a")], [tk(lp="n/a")])),
    scenario("a_quote_when_the_first_socket_is_down", q("SBIN", "NSE", [tk()], first_socket_down=True)),
    scenario("a_quote_when_there_is_no_socket", q("SBIN", "NSE", [tk()], no_socket=True)),
    scenario("two_quotes_in_a_row_use_the_same_socket", q("SBIN", "NSE", [tk()]), q("INFY", "NSE", [tk("1594")])),
    # ---- depth ---------------------------------------------------------------------------------------------------
    scenario("a_depth", d("SBIN", "NSE", [dk()])),
    scenario("a_depth_with_a_feed_tick_after_it", d("SBIN", "NSE", [dk(), df()])),
    scenario("a_depth_from_a_feed_tick_alone", d("SBIN", "NSE", [df(sp1="806.0", sq1="40", so1="2", tbq="5", tsq="6", ltq="7", oi="8", poi="9", o="1", h="2", l="0.5", c="1.5", v="10")])),
    scenario("a_depth_with_fewer_than_five_levels", d("SBIN", "NSE", [dk(bp3="0", bp4="0", bp5="0", sp5="0")])),
    scenario("a_depth_with_a_gap_in_the_levels", d("SBIN", "NSE", [dk(bp2="0")])),
    scenario("a_feed_tick_changes_one_level_and_keeps_the_rest", d("SBIN", "NSE", [dk(), {"t": "df", "e": "NSE", "tk": "3045", "bp2": "805.3", "bq2": "7", "so3": "9"}])),
    scenario("a_feed_tick_names_a_level_beyond_those_held", d("SBIN", "NSE", [dk(bp3="0", bp4="0", bp5="0", sp3="0", sp4="0", sp5="0"), {"t": "df", "e": "NSE", "tk": "3045", "bp4": "805.1", "bq4": "3", "sp5": "806.5", "sq5": "2"}])),
    scenario("a_depth_that_never_arrives", d("SBIN", "NSE", [])),
    scenario("a_depth_of_an_index", d("NIFTY", "NSE_INDEX", [dk("26000", "NSE")])),
    scenario("a_depth_with_a_quantity_that_is_not_a_number_is_dropped", d("SBIN", "NSE", [dk(bq2="x")])),
    scenario("a_depth_when_there_is_no_socket", d("SBIN", "NSE", [dk()], no_socket=True)),
    scenario("a_depth_when_the_first_socket_is_down", d("SBIN", "NSE", [dk()], first_socket_down=True)),
    scenario("a_depth_then_a_quote_of_the_same_instrument", d("SBIN", "NSE", [dk()]), q("SBIN", "NSE", [tk()])),
    # ---- several quotes ------------------------------------------------------------------------------------------
    scenario("several_quotes", m([pair("SBIN"), pair("INFY"), pair("TATAMOTORS", "BSE")], [tk(), tk("1594"), tk("500570", "BSE")])),
    scenario("several_quotes_with_one_missing", m([pair("SBIN"), pair("INFY")], [tk()], [])),
    scenario("several_quotes_with_all_missing", m([pair("SBIN"), pair("INFY")], [], [])),
    scenario("several_quotes_with_a_symbol_that_has_no_token", m([pair("SBIN"), pair("NOPE")], [tk()])),
    scenario("several_quotes_where_none_has_a_token", m([pair("NOPE"), pair("NOPE2")])),
    scenario("several_quotes_with_an_index_and_an_equity", m([pair("NIFTY", "NSE_INDEX"), pair("SBIN")], [tk("26000", "NSE"), tk()])),
    scenario("several_quotes_with_the_same_instrument_twice", m([pair("SBIN"), pair("SBIN")], [tk()])),
    scenario("several_quotes_with_a_feed_tick_for_each", m([pair("SBIN"), pair("INFY")], [tk(), tk("1594"), tf(), tf("1594", lp="1500")])),
    scenario("a_hundred_and_five_quotes_go_in_two_batches", m([pair(f"BULK{i}") for i in range(105)], [tk(str(800000 + i)) for i in range(100)], [tk(str(800000 + i)) for i in range(100, 105)])),
    scenario("several_quotes_when_there_is_no_socket", m([pair("SBIN")], [tk()], no_socket=True)),
    scenario("several_quotes_when_the_first_socket_is_down", m([pair("SBIN")], [tk()], first_socket_down=True)),
]
