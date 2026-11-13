"""Record market-data feed fixtures from the Python OpenAlgo broker adapters.

Runs the real adapter and its real websocket client with nothing connected: a fake socket records what the client sends, binary frames are fed to the
client's own parser, and the adapter's publish call is captured. A scenario is a list of steps (subscribe, unsubscribe, frame, reconnect); for each
step the output is recorded. The .NET codec replays the same steps and must produce the same messages and the same ticks.

Run from the repository root:
    uv run python ep-trade-bot/tools/contract-fixtures/record_feed.py <set>
"""

import importlib
import json
import os
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

TMP = Path(tempfile.mkdtemp(prefix="oa-feed-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

FIXTURE = sys.argv[1]
sys.path.insert(0, str(HERE))
SCENARIOS = importlib.import_module(f"scenarios_feed_{FIXTURE}")
for _k, _v in getattr(SCENARIOS, "ENV", {}).items():
    os.environ[_k] = _v

import dotenv  # noqa: E402

dotenv.load_dotenv = lambda *a, **k: False
dotenv.main.load_dotenv = dotenv.load_dotenv

from database.symbol import SymToken, db_session, init_db  # noqa: E402

init_db()
for row in SCENARIOS.SYMBOLS:
    db_session.add(SymToken(**row))
db_session.commit()

import database.token_db_enhanced as tde  # noqa: E402

tde.load_cache_for_broker(SCENARIOS.BROKER)

# The adapters are imported first: patching the clock under an import can leave a wait that never ends.
driver = SCENARIOS.Driver()

# Nothing in a recording waits, and every timestamp the adapter makes is the same instant.
NOW = SCENARIOS.NOW_MS
time.time = lambda: NOW / 1000.0
time.sleep = lambda *a, **k: None

out = []
for sc in SCENARIOS.SCENARIOS:
    driver.reset()
    steps = []
    for step in sc["steps"]:
        kind = next(iter(step))
        result = getattr(driver, kind)(step[kind])
        steps.append({"step": step, "result": result})
    out.append({"name": sc["name"], "steps": steps})
    print(f"ok {sc['name']}")

dest = HERE / f"feed_{FIXTURE}.fixtures.json"
dest.write_text(json.dumps({"broker": SCENARIOS.BROKER, "access_token": getattr(SCENARIOS, "AUTH_TOKEN", None), "start_frames": getattr(SCENARIOS, "START_FRAMES", []), "now_ms": NOW, "symbols": SCENARIOS.SYMBOLS, "scenarios": out}, indent=2) + "\n", encoding="utf-8")
print(f"wrote {len(out)} scenarios to {dest}")
