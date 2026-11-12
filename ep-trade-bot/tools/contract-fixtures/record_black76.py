"""Records OpenAlgo's option maths (opengreeks Black-76) over a grid, for black76.fixtures.json. The .NET core must reproduce price, the five Greeks and implied volatility."""

import itertools
import json
from pathlib import Path

from opengreeks import black76 as b

OUT = Path(__file__).with_name("black76.fixtures.json")
cases = []
for flag, f, k_off, t, r, sigma in itertools.product(
    ["c", "p"], [24000.0, 52000.0, 78.5, 1950.25], [-0.08, -0.02, 0.0, 0.03, 0.1], [0.0001, 0.003, 0.02, 0.08, 0.5, 1.5], [0.0, 0.065, 0.1], [0.05, 0.15, 0.4, 1.2]
):
    k = round(f * (1 + k_off) / 0.05) * 0.05
    k = round(k, 2)
    price = b.black(flag, f, k, t, r, sigma)
    row = {"flag": flag, "f": f, "k": k, "t": t, "r": r, "sigma": sigma, "price": price,
           "delta": b.delta(flag, f, k, t, r, sigma), "gamma": b.gamma(flag, f, k, t, r, sigma), "theta": b.theta(flag, f, k, t, r, sigma),
           "vega": b.vega(flag, f, k, t, r, sigma), "rho": b.rho(flag, f, k, t, r, sigma)}
    try:
        row["iv"] = b.implied_volatility(price, f, k, r, t, flag)
    except Exception as e:  # noqa: BLE001 - the message is part of the contract
        row["iv"] = None
        row["iv_error"] = str(e)
    cases.append(row)

# IV edge cases: a price at or under intrinsic, absurdly high, and zero.
edge = []
for flag, f, k, t, r, price in [("c", 24000.0, 23000.0, 0.05, 0.065, 900.0), ("c", 24000.0, 23000.0, 0.05, 0.065, 1000.0), ("p", 24000.0, 25000.0, 0.05, 0.065, 5.0),
                                ("c", 24000.0, 24100.0, 0.05, 0.065, 0.0), ("c", 24000.0, 24100.0, 0.05, 0.065, 23999.0), ("c", 24000.0, 24100.0, 0.05, 0.065, 1e-9)]:
    row = {"flag": flag, "f": f, "k": k, "t": t, "r": r, "price": price}
    try:
        row["iv"] = b.implied_volatility(price, f, k, r, t, flag)
    except Exception as e:  # noqa: BLE001
        row["iv"] = None
        row["iv_error"] = str(e)
    edge.append(row)

OUT.write_text(json.dumps({"grid": cases, "edge": edge}, indent=0))
print(len(cases), "grid cases;", sum(1 for c in cases if c["iv"] is None), "without iv;", [(e["price"], e["iv"], e.get("iv_error")) for e in edge])
