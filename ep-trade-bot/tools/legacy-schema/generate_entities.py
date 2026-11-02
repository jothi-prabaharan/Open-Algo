"""One-shot generator: OpenAlgo SQLAlchemy tables -> EF Core entities and configurations.

Input is ``openalgo-schema.json``, produced from the live OpenAlgo metadata by
``dump_schema.py`` (run from the repository root with the OpenAlgo venv).

The generated C# is committed and from then on maintained by hand; re-running
this script overwrites files under ``Entities/Legacy`` and
``Persistence/Configurations/Legacy`` only. Every table keeps its OpenAlgo name
and column names so that the behavioural reference and the importer line up
one to one.

Usage (from ep-trade-bot/):
    python3 tools/legacy-schema/generate_entities.py
"""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCHEMA = json.loads((Path(__file__).parent / "openalgo-schema.json").read_text(encoding="utf-8"))
ENT_DIR = ROOT / "src/EPTradeBot.Core/Entities/Legacy"
CFG_DIR = ROOT / "src/EPTradeBot.Infrastructure/Persistence/Configurations/Legacy"

AUDIT = {
    "ag_audit", "alert_log", "analyzer_logs", "order_logs", "command_logs", "whatsapp_command_logs",
    "error_404_tracker", "invalid_api_key_tracker", "ip_bans", "traffic_logs", "login_attempts",
    "pending_orders",
}
SCHEMA_OF = {"order_latency": "latency", "health_alerts": "telemetry", "health_metrics": "telemetry"}


def schema_for(table: str) -> str:
    if table in SCHEMA_OF:
        return SCHEMA_OF[table]
    if table in AUDIT:
        return "audit"
    if table.startswith("sandbox_"):
        return "sandbox"
    return "core"


CLASS_OVERRIDES = {
    "auth": "BrokerAuth",
    "symtoken": "SymToken",
    "settings": "AppSetting",
    "qty_freeze": "QtyFreeze",
    "alert_log": "AlertLog",
    "bot_config": "TelegramBotConfig",
    "command_logs": "TelegramCommandLog",
    "notification_queue": "TelegramNotification",
    "user_preferences": "TelegramUserPreference",
    "pending_orders": "ActionCenterPendingOrder",
    "order_logs": "ApiOrderLog",
    "watchlists": "Watchlist",
    "sandbox_gtt": "SandboxGtt",
    "sandbox_config": "SandboxConfig",
    "leverage_config": "LeverageConfig",
    "whatsapp_config": "WhatsappConfig",
    "master_contract_status": "MasterContractStatus",
    "session_revocations": "SessionRevocation",
    "market_holiday_exchanges": "MarketHolidayExchange",
    "strategy_portfolio": "StrategyPortfolioEntry",
}
NAMESPACE_OF = {"core": "Core", "sandbox": "Sandbox", "audit": "Audit", "latency": "Latency", "telemetry": "Telemetry"}


def pascal(name: str) -> str:
    return "".join(p[:1].upper() + p[1:] for p in re.split(r"[_\s]+", name) if p)


def singular(word: str) -> str:
    if word.endswith("ies"):
        return word[:-3] + "y"
    if word.endswith("sses"):
        return word[:-2]
    if word.endswith("s") and not word.endswith("ss"):
        return word[:-1]
    return word


def class_name(table: str) -> str:
    if table in CLASS_OVERRIDES:
        return CLASS_OVERRIDES[table]
    parts = table.split("_")
    parts[-1] = singular(parts[-1])
    return pascal("_".join(parts))


def clr(col: dict) -> tuple[str, list[str], str | None]:
    """Returns (clr type, fluent calls, initializer)."""
    t = col["type"]
    calls: list[str] = []
    m = re.match(r"VARCHAR\((\d+)\)", t)
    if m:
        calls.append(f".HasMaxLength({m.group(1)})")
        base = "string"
    elif t in ("VARCHAR", "TEXT"):
        base = "string"
    elif t == "INTEGER":
        base = "int"
    elif t == "BIGINT":
        base = "long"
    elif t == "BOOLEAN":
        base = "bool"
    elif t == "FLOAT":
        base = "double"
    elif (m := re.match(r"(?:DECIMAL|NUMERIC)\((\d+), (\d+)\)", t)):
        base = "decimal"
        calls.append(f".HasPrecision({m.group(1)}, {m.group(2)})")
    elif t == "JSON":
        base = "string"
        calls.append('.HasColumnType("jsonb")')
    elif t == "DATETIME":
        base = "DateTime"
    elif t == "DATE":
        base = "DateOnly"
    elif t == "TIME":
        base = "TimeOnly"
    elif t == "BLOB":
        base = "byte[]"
    else:
        raise ValueError(f"unmapped type {t}")

    nullable = col["nullable"] and not col["pk"]
    init = None
    default = col.get("default")
    sdefault = col.get("sdefault")
    if default == "now()" or sdefault == "now()" or (default == "callable" and base == "DateTime"):
        init = "DateTime.UtcNow"
    elif default == "callable" and t == "JSON":
        init = '"{}"' if col["name"] == "leg_state" else '"[]"'
    elif default not in (None, "callable"):
        lit = default
        if base == "bool":
            init = "true" if lit == "True" else "false"
        elif base == "string":
            init = json.dumps(lit.strip("'"))
        elif base in ("int", "long"):
            init = str(int(float(lit.strip("'"))))
        elif base == "double":
            init = repr(float(lit.strip("'")))
        elif base == "decimal":
            init = lit.strip("'") + "m"

    if base == "string" or base == "byte[]":
        typ = base + ("?" if nullable else "")
        if not nullable and init is None:
            init = '""' if base == "string" else "[]"
    else:
        typ = base + ("?" if nullable else "")
    if not nullable and base not in ("string", "byte[]"):
        pass
    if not nullable:
        calls.append(".IsRequired()")
    return typ, calls, init


def index_name(table: str, cols: list[str], unique: bool) -> str:
    name = ("ux_" if unique else "ix_") + table + "_" + "_".join(cols)
    return name[:63]


def main() -> None:
    classes = {t: class_name(t) for t in SCHEMA}
    assert len(set(classes.values())) == len(classes), "class name collision"
    pk_of = {t: [c["name"] for c in v["columns"] if c["pk"]] for t, v in SCHEMA.items()}
    for d in (ENT_DIR, CFG_DIR):
        if d.exists():
            for f in d.rglob("*.cs"):
                f.unlink()

    for table, spec in sorted(SCHEMA.items()):
        schema = schema_for(table)
        ns = NAMESPACE_OF[schema]
        cls = classes[table]
        props = []
        cfg = []
        names = {}
        for col in spec["columns"]:
            prop = pascal(col["name"])
            if prop == cls:
                prop += "Value"
            names[col["name"]] = prop
            typ, calls, init = clr(col)
            props.append(f"    public {typ} {prop} {{ get; set; }}" + (f" = {init};" if init else ""))
            line = f'        b.Property(x => x.{prop}).HasColumnName("{col["name"]}"){"".join(calls)};'
            cfg.append(line)

        pk = pk_of[table]
        keyexpr = f"x.{names[pk[0]]}" if len(pk) == 1 else "new { " + ", ".join(f"x.{names[c]}" for c in pk) + " }"
        head = [f'        b.ToTable("{table}", schema: "{schema}");', f"        b.HasKey(x => {keyexpr});"]
        if len(pk) == 1:
            pcol = next(c for c in spec["columns"] if c["name"] == pk[0])
            if pcol["type"] in ("INTEGER", "BIGINT"):
                head.append(f"        b.Property(x => x.{names[pk[0]]}).ValueGeneratedOnAdd();")
            else:
                head.append(f"        b.Property(x => x.{names[pk[0]]}).ValueGeneratedNever();")

        idx_lines = []
        seen = set()
        for col in spec["columns"]:
            if col["unique"] and not col["pk"]:
                seen.add((col["name"],))
                idx_lines.append(
                    f'        b.HasIndex(x => x.{names[col["name"]]}).IsUnique().HasDatabaseName("{index_name(table, [col["name"]], True)}");'
                )
        for group, unique in [(u["cols"], True) for u in spec["uniques"]] + [(i["cols"], i["unique"]) for i in spec["indexes"]]:
            keyt = tuple(group)
            if keyt in seen or list(keyt) == pk:
                continue
            seen.add(keyt)
            expr = f"x.{names[group[0]]}" if len(group) == 1 else "new { " + ", ".join(f"x.{names[c]}" for c in group) + " }"
            idx_lines.append(
                f'        b.HasIndex(x => {expr}){".IsUnique()" if unique else ""}.HasDatabaseName("{index_name(table, list(group), unique)}");'
            )

        fk_lines = []
        for col in spec["columns"]:
            for target in col["fk"]:
                ttable, tcol = target.split(".")
                tcls = classes[ttable]
                tprop = pascal(tcol)
                if tprop == tcls:
                    tprop += "Value"
                principal = "" if pk_of[ttable] == [tcol] else f".HasPrincipalKey(p => p.{tprop})"
                tns = NAMESPACE_OF[schema_for(ttable)]
                tref = tcls if tns == ns else f"EPTradeBot.Core.Entities.Legacy.{tns}.{tcls}"
                fk_lines.append(
                    f"        b.HasOne<{tref}>().WithMany(){principal}.HasForeignKey(x => x.{names[col['name']]}).OnDelete(DeleteBehavior.Cascade);"
                )

        edir = ENT_DIR / ns
        cdir = CFG_DIR / ns
        edir.mkdir(parents=True, exist_ok=True)
        cdir.mkdir(parents=True, exist_ok=True)
        src_mod = spec["module"]
        (edir / f"{cls}.cs").write_text(
            "// Generated from OpenAlgo table '" + table + "' (" + src_mod + "). Maintained by hand from here on.\n"
            f"namespace EPTradeBot.Core.Entities.Legacy.{ns};\n\n"
            f"/// <summary>Port of OpenAlgo table <c>{table}</c>.</summary>\n"
            f"public sealed class {cls}\n{{\n" + "\n".join(props) + "\n}\n",
            encoding="utf-8",
        )
        (cdir / f"{cls}Configuration.cs").write_text(
            "// Generated from OpenAlgo table '" + table + "'. Maintained by hand from here on.\n"
            f"using EPTradeBot.Core.Entities.Legacy.{ns};\n"
            "using Microsoft.EntityFrameworkCore;\n"
            "using Microsoft.EntityFrameworkCore.Metadata.Builders;\n\n"
            f"namespace EPTradeBot.Infrastructure.Persistence.Configurations.Legacy.{ns};\n\n"
            f"internal sealed class {cls}Configuration : IEntityTypeConfiguration<{cls}>\n{{\n"
            f"    public void Configure(EntityTypeBuilder<{cls}> b)\n    {{\n"
            + "\n".join(head + cfg + idx_lines + fk_lines)
            + "\n    }\n}\n",
            encoding="utf-8",
        )
    print(f"generated {len(SCHEMA)} entities")


if __name__ == "__main__":
    main()
