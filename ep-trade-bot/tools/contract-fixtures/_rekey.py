"""Moves the market-data keys of a scenario set to another service path (iBulls and others use /apibinarymarketdata)."""


def rekey(scenarios, old="/apimarketdata", new="/apibinarymarketdata"):
    out = []
    for s in scenarios:
        s = dict(s)
        if "broker" in s:
            s["broker"] = {k.replace(old, new): v for k, v in s["broker"].items()}
        out.append(s)
    return out
