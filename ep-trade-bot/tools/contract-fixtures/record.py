"""Record /api/v1 contract fixtures from the Python OpenAlgo implementation.

Runs the real Flask blueprint, the real service layer and the real broker
plugin. Only three things are replaced: the database lookups that need a
logged-in install (API key, broker token, analyzer flag), the symbol master
(seeded with a few rows) and the HTTP transport (canned broker replies).

For every scenario it writes what the API answered and every request the
plugin sent to the broker. The .NET contract tests replay the same request
against EP Trade bot with the same broker replies and compare both.

Run from the repository root:
    uv run python ep-trade-bot/tools/contract-fixtures/record.py <broker>
"""

import importlib
import json
import os
import sys
import tempfile
from pathlib import Path
from urllib.parse import parse_qsl, urlsplit

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

TMP = Path(tempfile.mkdtemp(prefix="oa-fixtures-"))
for name, file in [
    ("DATABASE_URL", "openalgo.db"),
    ("SANDBOX_DATABASE_URL", "sandbox.db"),
    ("LOGS_DATABASE_URL", "logs.db"),
    ("LATENCY_DATABASE_URL", "latency.db"),
    ("HEALTH_DATABASE_URL", "health.db"),
]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16
os.environ["BROKER_API_KEY"] = "kite-api-key"
os.environ["BROKER_API_SECRET"] = "kite-api-secret"
os.environ["ORDER_RATE_LIMIT"] = "10000 per second"
os.environ["API_RATE_LIMIT"] = "10000 per second"
os.environ["SMART_ORDER_RATE_LIMIT"] = "10000 per second"
os.environ["SMART_ORDER_DELAY"] = "0"

BROKER = sys.argv[1] if len(sys.argv) > 1 else "zerodha"
sys.path.insert(0, str(HERE))
SCENARIOS = importlib.import_module(f"scenarios_{BROKER}")
for _k, _v in getattr(SCENARIOS, "ENV", {}).items():
    os.environ[_k] = _v

import dotenv  # noqa: E402

dotenv.load_dotenv = lambda *a, **k: False
dotenv.main.load_dotenv = dotenv.load_dotenv

import httpx  # noqa: E402

import utils.httpx_client as httpx_client  # noqa: E402

API_KEY = "fixture-api-key-0000000000000000000000000000000000000000000000"
AUTH_TOKEN = getattr(SCENARIOS, "AUTH_TOKEN", "kite-access-token")

state = {"canned": {}, "sent": [], "calls": {}}


def _handler(request: httpx.Request) -> httpx.Response:
    url = urlsplit(str(request.url))
    body = request.content.decode() if request.content else ""
    ctype = request.headers.get("content-type", "")
    if "application/x-www-form-urlencoded" in ctype:
        parsed_body = {"form": dict(parse_qsl(body, keep_blank_values=True))}
    elif "json" in ctype and body:
        parsed_body = {"json": json.loads(body)}
    else:
        parsed_body = {}
    state["sent"].append(
        {
            "method": request.method,
            "host": url.netloc,
            "path": url.path,
            "query": [list(kv) for kv in parse_qsl(url.query, keep_blank_values=True)],
            "headers": {
                k: v
                for k, v in request.headers.items()
                if k.lower() in ("authorization", "x-kite-version", "content-type", "accept", "access-token", "client-id", "x-privatekey", "x-clientcode", "api-key")
            },
            **parsed_body,
        }
    )
    key = f"{request.method} {url.path}"
    reply = state["canned"].get(key)
    if isinstance(reply, list):
        # Successive calls get successive replies; the last one repeats.
        index = state["calls"].get(key, 0)
        state["calls"][key] = index + 1
        reply = reply[min(index, len(reply) - 1)]
    if reply is None:
        return httpx.Response(599, json={"status": "error", "message": f"no canned reply for {key}"})
    return httpx.Response(reply.get("status", 200), json=reply["body"])


_client = httpx.Client(transport=httpx.MockTransport(_handler), timeout=5)
httpx_client.get_httpx_client = lambda *a, **k: _client

import database.auth_db as auth_db  # noqa: E402
import database.settings_db as settings_db  # noqa: E402

def _token_broker(api_key, include_feed_token=False):
    if api_key != API_KEY:
        return (None, None, None) if include_feed_token else (None, None)
    return (AUTH_TOKEN, "feed-token", BROKER) if include_feed_token else (AUTH_TOKEN, BROKER)


auth_db.get_auth_token_broker = _token_broker
auth_db.verify_api_key = lambda k: "fixtureuser" if k == API_KEY else None
auth_db.get_auth_token = lambda *a, **k: AUTH_TOKEN
settings_db.get_analyze_mode = lambda: False

from database.symbol import SymToken, db_session, init_db  # noqa: E402

init_db()
for _mod in ("database.auth_db", "database.apilog_db", "database.analyzer_db", "database.settings_db", "database.user_db"):
    try:
        importlib.import_module(_mod).init_db()
    except Exception as exc:  # a reference module that is absent is not needed
        print(f"skip {_mod}: {exc}")
if getattr(SCENARIOS, "SEED_API_KEY_ROW", False):
    # Some plugins look up the install's API key row to call the quote service.
    auth_db.upsert_api_key("fixtureuser", API_KEY)

for row in SCENARIOS.SYMBOLS:
    db_session.add(SymToken(**row))
db_session.commit()

import database.token_db_enhanced as tde  # noqa: E402

tde.load_cache_for_broker(BROKER)

from flask import Flask  # noqa: E402

from limiter import limiter  # noqa: E402
from restx_api import api_v1_bp  # noqa: E402

app = Flask("fixture-recorder")
app.config["TESTING"] = True
app.secret_key = "fixture"
limiter.init_app(app)
app.register_blueprint(api_v1_bp)
client = app.test_client()

out = []
for sc in SCENARIOS.SCENARIOS:
    state["canned"] = sc.get("broker", {})
    state["sent"] = []
    state["calls"] = {}
    body = dict(sc["request"])
    if sc.get("auth", "body") == "body":
        body.setdefault("apikey", API_KEY)
    headers = {"X-API-KEY": API_KEY} if sc.get("auth") == "header" else {}
    method = sc.get("method", "POST")
    if method == "GET":
        resp = client.get(sc["path"], query_string=body, headers=headers)
    else:
        resp = client.post(sc["path"], json=body, headers=headers)
    out.append(
        {
            "name": sc["name"],
            "method": method,
            "path": sc["path"],
            "auth": sc.get("auth", "body"),
            "request": body,
            "broker": state["canned"],
            "expected": {"status": resp.status_code, "body": resp.get_json(silent=True)},
            "sent": state["sent"],
            "ignore": sc.get("ignore", []),
        }
    )
    print(f"{resp.status_code} {sc['name']}")

dest = HERE / f"{BROKER}.fixtures.json"
dest.write_text(json.dumps({"broker": BROKER, "api_key": API_KEY, "auth_token": AUTH_TOKEN, "client_id": getattr(SCENARIOS, "CLIENT_ID", None), "read_posts": getattr(SCENARIOS, "READ_POSTS", []), "symbols": SCENARIOS.SYMBOLS, "fixtures": out}, indent=2) + "\n", encoding="utf-8")
print(f"wrote {len(out)} fixtures to {dest}")
