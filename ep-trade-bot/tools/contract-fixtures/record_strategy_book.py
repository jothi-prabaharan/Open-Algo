"""Records OpenAlgo's per-strategy position book (database.strategy_book_db) for strategy_book.fixtures.json.

Each scenario is a list of steps: a tag for an order, or a fill for it, on a given session date. Every fill's returned summary and the final legs are recorded.
"""

import json
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-sb-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import database.strategy_book_db as sb  # noqa: E402

sb.init_strategy_book_db()
state = {"today": "2026-11-02"}
sb._session_date = lambda: state["today"]


def tag(order, strategy="alpha", symbol="SBIN", product="MIS", user="u1", exchange="NSE", day=None):
    return {"op": "tag", "order": order, "user": user, "strategy": strategy, "symbol": symbol, "exchange": exchange, "product": product, "day": day}


def fill(order, qty, avg, action, day=None):
    return {"op": "fill", "order": order, "qty": qty, "avg": avg, "action": action, "day": day}


SCENARIOS = [
    {"name": "open_add_and_close", "steps": [tag("o1"), fill("o1", 10, 100, "BUY"), tag("o2"), fill("o2", 10, 110, "BUY"), tag("o3"), fill("o3", 20, 120, "SELL")]},
    {"name": "short_then_cover_at_a_loss", "steps": [tag("o1"), fill("o1", 5, 200, "SELL"), tag("o2"), fill("o2", 5, 210, "BUY")]},
    {"name": "flip_through_zero", "steps": [tag("o1"), fill("o1", 10, 100, "BUY"), tag("o2"), fill("o2", 25, 105, "SELL")]},
    {"name": "partial_fills_at_different_levels", "steps": [tag("o1"), fill("o1", 4, 100, "BUY"), fill("o1", 10, 101.5, "BUY"), fill("o1", 10, 101.5, "BUY")]},
    {"name": "duplicate_and_out_of_order_events", "steps": [tag("o1"), fill("o1", 10, 100, "BUY"), fill("o1", 10, 100, "BUY"), fill("o1", 6, 100, "BUY")]},
    {"name": "fill_before_its_tag", "steps": [fill("o1", 10, 100, "BUY"), tag("o1"), tag("o2"), fill("o2", 10, 90, "SELL")]},
    {"name": "untagged_order_is_ignored", "steps": [fill("ghost", 5, 50, "BUY"), tag("o1", strategy=""), fill("o1", 5, 50, "BUY")]},
    {"name": "two_strategies_and_two_symbols", "steps": [tag("o1", strategy="alpha"), fill("o1", 10, 100, "BUY"), tag("o2", strategy="beta"), fill("o2", 10, 100, "SELL"),
                                                      tag("o3", symbol="INFY"), fill("o3", 3, 1500, "BUY"), tag("o4", strategy="alpha", product="CNC"), fill("o4", 7, 99, "BUY")]},
    {"name": "next_session_resets_today", "steps": [tag("o1"), fill("o1", 10, 100, "BUY", "2026-11-02"), tag("o2"), fill("o2", 10, 110, "SELL", "2026-11-02"),
                                                  tag("o3"), fill("o3", 10, 120, "BUY", "2026-11-03"), tag("o4"), fill("o4", 4, 125, "SELL", "2026-11-03")]},
    {"name": "read_on_a_later_session", "read_day": "2026-11-05", "steps": [tag("o1"), fill("o1", 10, 100, "BUY"), tag("o2"), fill("o2", 4, 110, "SELL")]},
    {"name": "fractional_and_tiny_quantities", "steps": [tag("o1"), fill("o1", 0.5, 100.25, "BUY"), tag("o2"), fill("o2", 0.5, 100.5, "SELL")]},
    {"name": "lowercase_action", "steps": [tag("o1"), fill("o1", 3, 10, "buy"), tag("o2"), fill("o2", 3, 12, "sell")]},
]

out = []
for sc in SCENARIOS:
    sb.reset_strategy("u1", "alpha"), sb.reset_strategy("u1", "beta")
    sb.db_session.query(sb.StrategyOrderTag).delete()
    sb.db_session.query(sb.StrategyPendingFill).delete()
    sb.db_session.query(sb.StrategyPosition).delete()
    sb.db_session.commit()
    sb.db_session.remove()
    results = []
    for step in sc["steps"]:
        if step["day"]:
            state["today"] = step["day"]
        if step["op"] == "tag":
            ok = sb.record_order_tag(step["order"], step["user"], step["strategy"], step["symbol"], step["exchange"], step["product"])
            results.append({"op": "tag", "ok": bool(ok)})
        else:
            results.append({"op": "fill", "result": sb.apply_fill(step["order"], step["qty"], step["avg"], step["action"])})
    if sc.get("read_day"):
        state["today"] = sc["read_day"]
    legs = sorted(sb.get_strategy_legs(), key=lambda l: (l["strategy"], l["symbol"], l["product"]))
    for leg in legs:
        leg.pop("updated_at", None)
    out.append({"name": sc["name"], "steps": sc["steps"], "results": results, "legs": legs, "read_day": sc.get("read_day") or state["today"]})
    state["today"] = "2026-11-02"

dest = Path(__file__).with_name("strategy_book.fixtures.json")
dest.write_text(json.dumps(out) + "\n", encoding="utf-8")
print(f"wrote {len(out)} scenarios to {dest}")
