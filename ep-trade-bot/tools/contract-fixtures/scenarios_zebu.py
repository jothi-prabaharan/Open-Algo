"""Zebu (Noren API at go.mynt.in): the Shoonya cases against Zebu's own plugin, with Zebu's holdings."""

import copy

import scenarios_shoonya as _s
from scenarios_shoonya import *  # noqa: F403

BROKER = "zebu"
ENV = {"BROKER_API_KEY": "FA12345:::zebu-client-id", "BROKER_API_SECRET": "zebu-secret"}

_HOLD = {"body": [
    {"stat": "Ok", "exch_tsym": [{"exch": "NSE", "token": "1594", "tsym": "INFY-EQ", "pp": "2", "ti": "0.05", "ls": "1", "product": "BTST"}, {"exch": "BSE", "token": "500209", "tsym": "INFY", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "20", "dpqty": "20", "npoadt1qty": "3", "colqty": "0", "benqty": "0", "unplgdqty": "0", "brkcolqty": "0", "btstqty": "0", "usedqty": "0", "upldprc": "1400.00", "profitandloss": "120.5", "pnlpercentage": "0.43"},
    {"stat": "Ok", "exch_tsym": [{"exch": "BSE", "token": "500325", "tsym": "BSEONLY", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "4", "dpqty": "4", "npoadt1qty": "0", "colqty": "0", "benqty": "0", "unplgdqty": "0", "brkcolqty": "0", "btstqty": "0", "usedqty": "0", "upldprc": "250.00"},
    {"stat": "Ok", "exch_tsym": [{"exch": "NSE", "token": "7777", "tsym": "OLD-EQ", "pp": "2", "ti": "0.05", "ls": "1"}],
     "holdqty": "5", "dpqty": "2", "npoadt1qty": "6", "colqty": "0", "benqty": "1", "unplgdqty": "1", "brkcolqty": "2", "btstqty": "1", "usedqty": "2", "upldprc": "12.50"},
    {"stat": "Not_Ok", "emsg": "bad row", "exch_tsym": [{"exch": "NSE", "token": "9", "tsym": "BAD-EQ"}], "holdqty": "1", "dpqty": "1", "npoadt1qty": "0", "upldprc": "10.00"},
]}

SCENARIOS = copy.deepcopy(_s.SCENARIOS)
for _sc in SCENARIOS:
    if _sc["name"] == "holdings":
        _sc["broker"] = {f"POST {_s.BASE}/Holdings": _HOLD}
