"""Records OpenAlgo's holdings reading (portfolio.holdings.parse_holdings and holdings_summary) for portfolio_holdings.fixtures.json."""

import json
import math
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-ph-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

from portfolio.holdings import holdings_summary, parse_holdings  # noqa: E402
from services.portfolio_service import _clean  # noqa: E402

CASES = [
    {"name": "typical", "rows": [
        {"symbol": "infy", "exchange": "nse", "quantity": 10, "average_price": 1500.5, "last_price": 1620.25, "pnl": 1197.5, "product": "CNC"},
        {"symbol": "RELIANCE", "exchange": "NSE", "quantity": "5", "average_price": "2400", "ltp": "2500.5", "pnl": "502.5", "product": "CNC"},
        {"symbol": "GOLDBEES", "exchange": "NSE", "quantity": 100, "average_price": 50.1, "last_price": 52.3, "pnl": 220, "product": "CNC"}]},
    {"name": "no_average_price", "rows": [
        {"symbol": "SBIN", "quantity": 20, "last_price": 800, "pnl": 100}, {"symbol": "TCS", "quantity": 2, "last_price": 3500, "pnl": -50}]},
    {"name": "price_recovered_from_average_and_pnl", "rows": [{"symbol": "HDFCBANK", "quantity": 10, "average_price": 1500, "pnl": 250}]},
    {"name": "dropped_rows", "rows": [
        {"symbol": "", "quantity": 1, "last_price": 10}, {"symbol": "ZERO", "quantity": 0, "last_price": 10}, {"symbol": "NEG", "quantity": -3, "last_price": 10},
        {"symbol": "NOPRICE", "quantity": 4}, {"symbol": "BADNUM", "quantity": "abc", "last_price": 10}, {"symbol": "DASH", "quantity": "-", "last_price": 10},
        {"symbol": "OK", "quantity": 1, "last_price": 10}]},
    {"name": "string_and_odd_numbers", "rows": [
        {"symbol": " tcs ", "exchange": " bse ", "quantity": " 3 ", "average_price": "1e3", "last_price": "1100.456", "pnl": "301.37", "product": "MIS"},
        {"symbol": "ABC", "exchange": None, "quantity": 7, "average_price": 0, "last_price": 12.345, "pnl": 3}]},
    {"name": "mixed_exchanges_and_equal_values", "rows": [
        {"symbol": "A", "exchange": "NSE", "quantity": 10, "average_price": 100, "last_price": 100, "pnl": 0},
        {"symbol": "B", "exchange": "BSE", "quantity": 10, "average_price": 100, "last_price": 100, "pnl": 0},
        {"symbol": "C", "exchange": "NFO", "quantity": 1, "average_price": 5000, "last_price": 4000, "pnl": -1000}]},
    {"name": "nothing", "rows": []},
]


def nan_free(x):
    if isinstance(x, float) and not math.isfinite(x):
        return None
    if isinstance(x, dict):
        return {k: nan_free(v) for k, v in x.items()}
    if isinstance(x, list):
        return [nan_free(v) for v in x]
    return x


out = [{"name": c["name"], "rows": c["rows"], "summary": nan_free(_clean(holdings_summary(parse_holdings(c["rows"]))))} for c in CASES]
dest = Path(__file__).with_name("portfolio_holdings.fixtures.json")
dest.write_text(json.dumps(out) + "\n", encoding="utf-8")
print(f"wrote {len(out)} cases to {dest}")
