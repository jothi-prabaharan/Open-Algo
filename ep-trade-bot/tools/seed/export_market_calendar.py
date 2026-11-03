"""Export the market calendar a fresh OpenAlgo install holds, as EP Trade bot's seed.

Runs OpenAlgo's own startup path (ensure_market_calendar_tables_exists: the
seed, the 2026 correction, the timings seed and both migrations) against an
empty SQLite database and writes what it stored, so the seed is OpenAlgo's
final state rather than a copy of one of its intermediate lists.

Run from the repository root:
    uv run python ep-trade-bot/tools/seed/export_market_calendar.py
"""

import json
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-calendar-"))
os.environ["DATABASE_URL"] = f"sqlite:///{TMP / 'openalgo.db'}"
os.environ["LOG_DIR"] = str(TMP / "log")

import dotenv  # noqa: E402

dotenv.load_dotenv = lambda *a, **k: False

import database.market_calendar_db as cal  # noqa: E402

cal.ensure_market_calendar_tables_exists()

holidays = []
for h in cal.Holiday.query.order_by(cal.Holiday.holiday_date, cal.Holiday.id).all():
    rows = cal.HolidayExchange.query.filter(cal.HolidayExchange.holiday_id == h.id).order_by(cal.HolidayExchange.id).all()
    holidays.append(
        {
            "date": h.holiday_date.isoformat(),
            "description": h.description,
            "holiday_type": h.holiday_type,
            "year": h.year,
            "exchanges": [
                {"exchange": r.exchange_code, "is_open": bool(r.is_open), "start_time": r.start_time, "end_time": r.end_time}
                for r in rows
            ],
        }
    )
timings = [
    {"exchange": t.exchange_code, "start_time": t.start_time, "end_time": t.end_time, "start_offset": t.start_offset, "end_offset": t.end_offset}
    for t in cal.MarketTiming.query.order_by(cal.MarketTiming.id).all()
]
dest = ROOT / "ep-trade-bot/src/EPTradeBot.Infrastructure/Persistence/Seed/market_calendar.json"
dest.write_text(json.dumps({"holidays": holidays, "timings": timings}, indent=1) + "\n", encoding="utf-8")
print(f"wrote {len(holidays)} holidays and {len(timings)} timings to {dest}")
