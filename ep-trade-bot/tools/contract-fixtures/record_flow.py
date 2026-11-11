"""Records the Flow engine's behaviour from OpenAlgo's own code, for flow.fixtures.json.

Runs services.flow_executor_service (WorkflowContext, NodeExecutor, execute_node_chain) with a frozen clock and no broker client, on scenarios that
need none: substitution, arithmetic, variables, conditions, gates, timing and the graph walk. The .NET engine replays each scenario and must agree.
"""

import json
import os
import sys
import tempfile
from datetime import datetime as _real_datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
TMP = Path(tempfile.mkdtemp(prefix="oa-flow-"))
for name, file in [("DATABASE_URL", "openalgo.db"), ("SANDBOX_DATABASE_URL", "sandbox.db"), ("LOGS_DATABASE_URL", "logs.db"),
                   ("LATENCY_DATABASE_URL", "latency.db"), ("HEALTH_DATABASE_URL", "health.db")]:
    os.environ[name] = f"sqlite:///{TMP / file}"
os.environ["HISTORIFY_DATABASE_URL"] = str(TMP / "historify.duckdb")
os.environ["LOG_DIR"] = str(TMP / "log")
os.environ.setdefault("API_KEY_PEPPER", "0" * 64)
os.environ.setdefault("APP_KEY", "fixture-recorder-app-key")
os.environ["FERNET_SALT"] = "ab" * 16

import services.flow_executor_service as fx  # noqa: E402

NOW = [_real_datetime(2026, 11, 11, 10, 0, 0)]


class Frozen(_real_datetime):
    @classmethod
    def now(cls, tz=None):
        return NOW[0]


fx.datetime = Frozen
SLEEPS = []
fx.time_module.sleep = lambda s: SLEEPS.append(s)


def at(h, m, s=0):
    NOW[0] = _real_datetime(2026, 11, 11, h, m, s)
    return f"2026-11-11T{h:02d}:{m:02d}:{s:02d}"


def ctx_with(variables):
    c = fx.WorkflowContext()
    for k, v in variables.items():
        c.set_variable(k, v)
    return c


def jsonable(v):
    return json.loads(json.dumps(v, default=str))


INTERPOLATE = [
    ({"a": 21.0, "n": 5, "s": "hi", "b": True, "none": None, "d": {"x": [1, 2, {"y": 3.5}], "k": "v"}, "l": [10, 20, 30]},
     ["{{a}}", "{{n}}", "{{s}}", "{{b}}", "{{none}}", "{{d.x[2].y}}", "{{d.k}}", "{{l[1]}}", "{{l[5]}}", "{{nope}}", "price={{a}} qty={{n}}!",
      "{{ a }}", "{{d.x}}", "{{d}}", "{{l}}", "no tokens", "{{a}}{{n}}", "{{d.missing}}", "{{s.x}}", "{{l[0][1]}}"]),
    ({"f": [1e16, 1e15, 123456789.123, 0.0001, 0.00001, 1e-7, 1.5e300, -2.5, 100.0, 0.1, 1 / 3, 2 ** 53 + 0.0]},
     ["{{f[0]}}", "{{f[1]}}", "{{f[2]}}", "{{f[3]}}", "{{f[4]}}", "{{f[5]}}", "{{f[6]}}", "{{f[7]}}", "{{f[8]}}", "{{f[9]}}", "{{f[10]}}", "{{f[11]}}"]),
]
MATH = ["1+2", "2*3+4", "(1+2)*3", "10/4", "10%3", "-10%3", "10%-3", "2**10", "2**-1", "-2**2", "2**3**2", "-(3)", "+5", "floor(3.7)", "floor(-3.2)",
        "floor(10/3)", "1/0", "1%0", "2**101", "2**100", "(-8)**0.5", "0**-1", "", "abc", "1+", "(1+2", "3 4", "sqrt(4)", "floor(1,2)", "1e3+1", ".5+.5",
        "5.", "  7  ", "2 ** 2", "10 // 3", "7 - -3", "100*1.1", "0.1+0.2", "1e400"]


def record_interpolate():
    out = []
    for variables, texts in INTERPOLATE:
        c = ctx_with(variables)
        for t in texts:
            out.append({"vars": variables, "text": t, "interpolated": c.interpolate(t), "raw": jsonable(c.resolve_raw(t))})
    return out


def record_math():
    out = []
    ne = fx.NodeExecutor.__new__(fx.NodeExecutor)
    for e in MATH:
        try:
            v = ne._safe_eval_math(e)
            out.append({"expr": e, "value": v if v == v and abs(v) != float("inf") else str(v)})
        except Exception as ex:  # noqa: BLE001
            out.append({"expr": e, "error": True})
    return out


def record_times():
    out = []
    for text in ["09:15", "9:5", "15:30:45", "25:99:99", "", "abc", "12", "12:", ":30", "09:15:07", "7:07", "a:b", "10:20:30:40"]:
        out.append({"text": text, "parsed": list(fx.parse_time_string(text, 9, 15))})
    return out


def run_graph(name, nodes, edges, variables=None, time_at=(10, 0, 0)):
    stamp = at(*time_at)
    SLEEPS.clear()
    logs, context = [], ctx_with(variables or {})
    executor = fx.NodeExecutor(None, context, logs, default_strategy=name)
    edge_map, incoming = {}, {}
    for e in edges:
        edge_map.setdefault(e["source"], []).append(e)
        incoming.setdefault(e["target"], []).append(e)
    trig = next((n for n in nodes if n["type"] in ["start", "webhookTrigger", "priceAlert", "orderUpdateTrigger"]), None)
    raised = None
    try:
        fx.execute_node_chain(trig["id"], nodes, edge_map, incoming, executor, context, {}, depth=0)
    except Exception as ex:  # noqa: BLE001
        raised = str(ex)
    return {
        "name": name, "nodes": nodes, "edges": edges, "vars": variables or {}, "at": stamp,
        "variables": jsonable(context.variables),
        "errors": executor.errors,
        "raised": raised,
        "logs": [l["message"] for l in logs if l["message"].startswith("[LOG]")],
        "sleeps": list(SLEEPS),
    }


def n(i, t, **data):
    return {"id": i, "type": t, "data": data}


def e(s, t, h=""):
    return {"source": s, "target": t, "sourceHandle": h}


def record_graphs():
    g = []
    S = n("s", "start")
    # variables and substitution
    g.append(run_graph("set-and-log", [S, n("v", "variable", variableName="px", operation="set", value="{{webhook.ltp}}"), n("l", "log", message="px={{px}}")],
                       [e("s", "v"), e("v", "l")], {"webhook": {"ltp": 101.5}}))
    g.append(run_graph("set-json", [S, n("v", "variable", variableName="o", operation="set", value='{"a": [1, 2]}'), n("l", "log", message="{{o.a[1]}}")],
                       [e("s", "v"), e("v", "l")]))
    for op, start, val in [("add", 5, "2.5"), ("subtract", 5, "2"), ("multiply", 5, "3"), ("divide", 5, "2"), ("divide", 5, "0"), ("add", None, "1"),
                           ("increment", 7, ""), ("decrement", 7, ""), ("append", "ab", "cd"), ("append", None, "cd"), ("add", "x", "1"), ("bogus", 1, "1"),
                           ("parse_json", None, '{"k": 3}'), ("parse_json", None, "{bad")]:
        vars_ = {} if start is None else {"x": start}
        g.append(run_graph(f"var-{op}-{start}-{val}", [S, n("v", "variable", variableName="x", operation=op, value=val), n("l", "log", message="x={{x}}")],
                           [e("s", "v"), e("v", "l")], vars_))
    for path in ["a.b", "a.c[1]", "a.c[9]", "a.zz", "", "a[0]", "a..b", "a.b]", "[0]"]:
        g.append(run_graph(f"get-{path}", [S, n("v", "variable", variableName="y", operation="get", sourceVariable="src", jsonPath=path), n("l", "log", message="y={{y}}")],
                           [e("s", "v"), e("v", "l")], {"src": {"a": {"b": 7}, "c": [1, 2, 3]}}))
    g.append(run_graph("get-missing-source", [S, n("v", "variable", variableName="y", operation="get", sourceVariable="nope"), n("l", "log", message="after")],
                       [e("s", "v"), e("v", "l")]))
    g.append(run_graph("stringify", [S, n("v", "variable", variableName="y", operation="stringify", sourceVariable="src"), n("l", "log", message="{{y}}")],
                       [e("s", "v"), e("v", "l")], {"src": {"a": [1, 2]}}))
    # math
    g.append(run_graph("math-ok", [S, n("m", "mathExpression", expression="({{a}} * {{b}}) + 1", outputVariable="r"), n("l", "log", message="r={{r}}")],
                       [e("s", "m"), e("m", "l")], {"a": 3, "b": 4.5}))
    g.append(run_graph("math-default-output", [S, n("m", "mathExpression", expression="10/4"), n("l", "log", message="{{result}}")], [e("s", "m"), e("m", "l")]))
    g.append(run_graph("math-error-stops-branch", [S, n("m", "mathExpression", expression="1/0", outputVariable="r"), n("l", "log", message="not reached")],
                       [e("s", "m"), e("m", "l")]))
    g.append(run_graph("math-empty", [S, n("m", "mathExpression", expression=""), n("l", "log", message="not reached")], [e("s", "m"), e("m", "l")]))
    g.append(run_graph("math-unresolved", [S, n("m", "mathExpression", expression="{{nope}} + 1"), n("l", "log", message="not reached")], [e("s", "m"), e("m", "l")]))
    # var conditions
    def branch(name, cond_type, data, variables=None, time_at=(10, 0, 0)):
        return run_graph(name, [S, n("c", cond_type, **data), n("t", "log", message="TRUE"), n("f", "log", message="FALSE"), n("p", "log", message="PASS")],
                         [e("s", "c"), e("c", "t", "true"), e("c", "f", "false"), e("c", "p")], variables, time_at)
    for op in [">", "gt", "<", "lt", "==", "eq", "!=", "neq", ">=", "gte", "<=", "lte", "~", ""]:
        for l, r in [(5, 3), (3, 5), (4, 4)]:
            g.append(branch(f"var-{op}-{l}-{r}", "varCondition", {"leftValue": l, "rightValue": r, "operator": op} if op else {"leftValue": l, "rightValue": r}))
    g.append(branch("var-interp", "varCondition", {"leftValue": "{{webhook.qty}}", "operator": ">", "rightValue": "10"}, {"webhook": {"qty": "12"}}))
    g.append(branch("var-empty-left", "varCondition", {"leftValue": "", "operator": ">", "rightValue": "1"}))
    g.append(branch("var-unresolved", "varCondition", {"leftValue": "{{typo}}", "operator": ">", "rightValue": "1"}))
    g.append(branch("var-text", "varCondition", {"leftValue": "abc", "operator": ">", "rightValue": "1"}))
    g.append(branch("var-zero", "varCondition", {"leftValue": 0, "operator": "<", "rightValue": 1}))
    g.append(branch("var-yes-no-handles", "varCondition", {"leftValue": 1, "operator": ">", "rightValue": 0}))
    # time nodes
    for hm in [(9, 14, 59), (9, 15, 0), (12, 0, 0), (15, 30, 0), (15, 30, 1), (23, 59, 0), (0, 0, 0)]:
        for inv in (False, True):
            g.append(branch(f"window-{hm}-{inv}", "timeWindow", {"startTime": "09:15", "endTime": "15:30", "invertCondition": inv}, time_at=hm))
    for hm in [(21, 59, 0), (23, 0, 0), (1, 59, 0), (2, 0, 1), (12, 0, 0)]:
        g.append(branch(f"overnight-{hm}", "timeWindow", {"startTime": "22:00", "endTime": "02:00"}, time_at=hm))
    for op in [">=", "<=", ">", "<", "==", "!=", ""]:
        for hm in [(15, 14, 59), (15, 15, 0), (15, 15, 30), (15, 16, 0)]:
            g.append(branch(f"tc-{op}-{hm}", "timeCondition", {"targetTime": "15:15", "operator": op} if op else {"targetTime": "15:15"}, time_at=hm))
    g.append(branch("tc-seconds", "timeCondition", {"targetTime": "15:29:59", "operator": ">="}, time_at=(15, 29, 30)))
    g.append(branch("tc-seconds-after", "timeCondition", {"targetTime": "15:29:59", "operator": ">="}, time_at=(15, 29, 59)))
    g.append(branch("tc-bad-target", "timeCondition", {"targetTime": "zz", "operator": ">="}, time_at=(9, 30, 0)))
    # delay / wait
    for data in [{"delayValue": 5}, {"delayValue": 2, "delayUnit": "minutes"}, {"delayValue": 1, "delayUnit": "hours"}, {"delayMs": 2500}, {}, {"delayValue": 400}]:
        g.append(run_graph(f"delay-{data}", [S, n("d", "delay", **data), n("l", "log", message="after")], [e("s", "d"), e("d", "l")]))
    for target, hm in [("10:10", (10, 0, 0)), ("09:30", (10, 0, 0)), ("10:00", (10, 0, 0)), ("11:00", (10, 0, 0)), ("10:30:15", (10, 0, 0)), ("", (10, 0, 0))]:
        g.append(run_graph(f"wait-{target}-{hm}", [S, n("w", "waitUntil", targetTime=target) if target else n("w", "waitUntil"), n("l", "log", message="after")],
                           [e("s", "w"), e("w", "l")], time_at=hm))
    # gates and the walk
    def two(name, a, b, gate, **extra):
        nodes = [S, n("a", "varCondition", leftValue=a, operator=">", rightValue=0), n("b", "varCondition", leftValue=b, operator=">", rightValue=0),
                 n("g", gate, **extra), n("t", "log", message="G-TRUE"), n("f", "log", message="G-FALSE")]
        edges = [e("s", "a"), e("s", "b"), e("a", "g", "true"), e("b", "g", "true"), e("g", "t", "true"), e("g", "f", "false")]
        return run_graph(name, nodes, edges)
    for gate in ("andGate", "orGate"):
        for a, b in [(1, 1), (1, -1), (-1, 1), (-1, -1)]:
            g.append(two(f"{gate}-{a}-{b}", a, b, gate))
    g.append(two("and-declared-3-wired-2", 1, 1, "andGate", inputCount=3))
    g.append(two("and-declared-2-wired-2", 1, 1, "andGate", inputCount=2))
    for a in (1, -1):
        g.append(run_graph(f"not-{a}", [S, n("a", "varCondition", leftValue=a, operator=">", rightValue=0), n("g", "notGate"), n("t", "log", message="N-TRUE"), n("f", "log", message="N-FALSE")],
                           [e("s", "a"), e("a", "g", "true"), e("g", "t", "true"), e("g", "f", "false")]))
    # a gate reachable by a third path: a feeds b, and both feed the gate, so the gate is met twice and must answer once
    g.append(run_graph("gate-reached-twice", [S, n("a", "varCondition", leftValue=1, operator=">", rightValue=0), n("b", "varCondition", leftValue=1, operator=">", rightValue=0),
                                                n("g", "andGate"), n("t", "log", message="ONCE")],
                       [e("s", "a"), e("a", "b", "true"), e("b", "g", "true"), e("a", "g", "true"), e("g", "t", "true")]))
    g.append(run_graph("gate-with-errored-input", [S, n("a", "varCondition", leftValue="{{typo}}", operator=">", rightValue=0), n("b", "varCondition", leftValue=1, operator=">", rightValue=0),
                                                      n("g", "andGate"), n("t", "log", message="G-TRUE"), n("f", "log", message="G-FALSE")],
                       [e("s", "a"), e("s", "b"), e("a", "g", "true"), e("b", "g", "true"), e("g", "t", "true"), e("g", "f", "false")]))
    g.append(run_graph("diamond-condition-once", [S, n("x", "log", message="X"), n("y", "log", message="Y"), n("c", "varCondition", leftValue=1, operator=">", rightValue=0), n("t", "log", message="ONCE")],
                       [e("s", "x"), e("s", "y"), e("x", "c"), e("y", "c"), e("c", "t", "true")]))
    g.append(run_graph("chain", [S, n("a", "log", message="A"), n("b", "log", message="B"), n("c", "log", message="C"), n("d", "log", message="D")],
                       [e("s", "a"), e("a", "b"), e("a", "c"), e("c", "d")]))
    g.append(run_graph("unknown-type-continues", [S, n("u", "madeUpNode"), n("l", "log", message="after-unknown")], [e("s", "u"), e("u", "l")]))
    g.append(run_graph("webhook-trigger", [n("w", "webhookTrigger"), n("l", "log", message="hit {{webhook.sym}}")], [e("w", "l")], {"webhook": {"sym": "SBIN"}}))
    g.append(run_graph("group-passes-through", [S, n("gr", "group"), n("l", "log", message="inside")], [e("s", "gr"), e("gr", "l")]))
    g.append(run_graph("cycle-hits-depth-bound", [S, n("a", "log", message="A"), n("b", "log", message="B")], [e("s", "a"), e("a", "b"), e("b", "a")]))
    # the visit bound: wide and shallow, so the depth bound is not what stops it
    wide_nodes, wide_edges = [S], []
    for i in range(30):
        prev = "s"
        for j in range(20):
            nid = f"c{i}_{j}"
            wide_nodes.append(n(nid, "variable", variableName="k", operation="increment"))
            wide_edges.append(e(prev, nid))
            prev = nid
    g.append(run_graph("wide-graph-hits-visit-bound", wide_nodes, wide_edges))
    # a not gate's branches leave from yes and no
    g.append(run_graph("yes-no-handles", [S, n("a", "varCondition", leftValue=1, operator=">", rightValue=0), n("t", "log", message="YES"), n("f", "log", message="NO")],
                       [e("s", "a"), e("a", "t", "yes"), e("a", "f", "no")]))
    g.append(run_graph("yes-no-handles-false", [S, n("a", "varCondition", leftValue=-1, operator=">", rightValue=0), n("t", "log", message="YES"), n("f", "log", message="NO")],
                       [e("s", "a"), e("a", "t", "yes"), e("a", "f", "no")]))
    g.append(run_graph("set-json-list", [S, n("v", "variable", variableName="o", operation="set", value="[1, 2, 3]"), n("l", "log", message="{{o[2]}}")], [e("s", "v"), e("v", "l")]))
    g.append(run_graph("set-text-braces-not-json", [S, n("v", "variable", variableName="o", operation="set", value="{not json"), n("l", "log", message="{{o}}")], [e("s", "v"), e("v", "l")]))
    # an order node with an unresolved field is refused before it reaches any broker
    g.append(run_graph("order-unresolved-field", [S, n("o", "placeOrder", symbol="SBIN", exchange="NSE", action="BUY", quantity="{{webhook.qty}}", product="MIS", priceType="MARKET"),
                                                    n("l", "log", message="not reached")], [e("s", "o"), e("o", "l")], {"webhook": {"x": 1}}))
    return g


def main():
    out = {
        "interpolate": record_interpolate(),
        "math": record_math(),
        "times": record_times(),
        "graphs": record_graphs(),
    }
    Path(__file__).with_name("flow.fixtures.json").write_text(json.dumps(out, indent=1, default=str), encoding="utf-8")
    print({k: len(v) for k, v in out.items()})


if __name__ == "__main__":
    main()
