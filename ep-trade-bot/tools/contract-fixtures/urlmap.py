"""List every /api/v1 method and path the Python OpenAlgo app serves.

Builds a bare Flask app with only the /api/v1 blueprint and reads its URL map, so
the count is what Flask itself would route. Run from the repository root with the
environment of an install (or the recorder's temporary one):

    uv run python ep-trade-bot/tools/contract-fixtures/urlmap.py
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from flask import Flask  # noqa: E402

from restx_api import api_v1_bp  # noqa: E402

app = Flask("urlmap")
app.register_blueprint(api_v1_bp)
rows = sorted(
    (rule.rule, method)
    for rule in app.url_map.iter_rules()
    if rule.rule.startswith("/api/v1")
    for method in rule.methods - {"HEAD", "OPTIONS"}
)
for path, method in rows:
    print(method, path)
print(f"{len(rows)} method and path pairs", file=sys.stderr)
