# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 6 of docs/design-trained-models.md: a lie judge for the noisy convoy, trained on the simulator's truth.

Reads world sets written by the convoy's `--world` option (features before, the action, features after, and the
world's truth), keeps the drives, loads, unloads and refuels whose truth is Landed or Lied, and trains a small network
to give the probability that the action lied. Writes plain JSON weights (format creatura.network/1, kind "judge").

The thresholds follow arm Y's recipe on the held-out training seeds (ending in --held-out-digit) of the --calibrate
set: t1 at the 99.5th percentile of the score on landed actions, t2 at the highest score of a landed drive.

With --judge it scores T10b on an evaluation set: the share of lies above t1, the share of landed actions above t1,
and its false alarms plus misses against the fused check's on the same actions (the check is computed from the raw
readings in the --runs recording the evaluation set was exported from).

    uv run tools/train/train_landed.py datasets/noisy/train.jsonl models/convoy-judge-noisy.json \\
        --calibrate datasets/noisy/train-u.jsonl --judge datasets/noisy/eval-u.jsonl --runs datasets/noisy/u-battery.jsonl
"""

import argparse
import json
import pathlib
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_policy import FORMAT, forward, rounded  # noqa: E402

CHECKABLE = ("drive", "load", "unload", "refuel")


def load(paths, actions_count):
    rows = []
    for path in paths:
        rows += [r for r in (json.loads(line) for line in open(path))
                 if r["actionId"].split(":")[0] in CHECKABLE and r.get("truth") in ("Landed", "Lied")]
    before = np.asarray([r["before"] for r in rows], dtype=float)
    after = np.asarray([r["after"] for r in rows], dtype=float)
    action = np.zeros((len(rows), actions_count))
    action[np.arange(len(rows)), [r["action"] for r in rows]] = 1.0
    lied = np.asarray([r["truth"] == "Lied" for r in rows], dtype=float)
    return rows, np.concatenate([before, action, after], axis=1), lied


def score(layers, x):
    s, _ = forward(layers, x)
    return 1.0 / (1.0 + np.exp(-s[:, 0]))


def train(x, y, xv, yv, hidden, epochs, patience, rate, batch, seed, balance=True):
    rng = np.random.default_rng(seed)
    sizes = [x.shape[1], *hidden, 1]
    layers = [(rng.normal(0, np.sqrt(2 / n), (m, n)), np.zeros(m)) for n, m in zip(sizes, sizes[1:])]
    moments = [[np.zeros_like(p) for p in layer for _ in (0, 1)] for layer in layers]
    # Lies are rare: weight them so both classes count equally in the loss.
    weight = (len(y) - y.sum()) / max(1.0, y.sum()) if balance else 1.0
    step, best, best_epoch, kept = 0, np.inf, 0, layers

    def loss(layers, x, y):
        p = np.clip(score(layers, x), 1e-6, 1 - 1e-6)
        return float(-(weight * y * np.log(p) + (1 - y) * np.log(1 - p)).mean())

    for epoch in range(1, epochs + 1):
        order = rng.permutation(len(x))
        for start in range(0, len(x), batch):
            idx = order[start:start + batch]
            s, inputs = forward(layers, x[idx])
            p = 1.0 / (1.0 + np.exp(-s))
            w = np.where(y[idx] == 1, weight, 1.0)[:, None]
            grad = w * (p - y[idx][:, None]) / len(idx)  # weighted cross-entropy through the sigmoid
            step += 1
            updated = []
            for i in reversed(range(len(layers))):
                wt, b = layers[i]
                gw, gb = grad.T @ inputs[i], grad.sum(axis=0)
                if i > 0:
                    grad = (grad @ wt) * (inputs[i] > 0)
                params = []
                for j, (param, g) in enumerate(((wt, gw), (b, gb))):
                    m, v = moments[i][2 * j], moments[i][2 * j + 1]
                    m[...] = 0.9 * m + 0.1 * g
                    v[...] = 0.999 * v + 0.001 * g * g
                    params.append(param - rate * (m / (1 - 0.9 ** step)) / (np.sqrt(v / (1 - 0.999 ** step)) + 1e-8))
                updated.append(tuple(params))
            layers = updated[::-1]
        held = loss(layers, xv, yv)
        if held < best:
            best, best_epoch, kept = held, epoch, layers
        if epoch % 5 == 0:
            print(f"epoch {epoch}: held-out loss {held:.4f} (best {best:.4f} at {best_epoch})", flush=True)
        if epoch - best_epoch >= patience:
            break
    return kept, best_epoch, best


def thresholds(layers, rows, x, y, digit):
    held = np.asarray([r["seed"] % 10 == digit for r in rows])
    s = score(layers, x[held])
    landed = y[held] == 0
    drives = np.asarray([r["actionId"].startswith("drive:") for r in rows])[held]
    t1 = float(np.percentile(s[landed], 99.5))
    t2 = float(s[landed & drives].max())
    lies = s[~landed]
    return {"t1": round(t1, 6), "t2": round(t2, 6), "heldOutLanded": int(landed.sum()), "heldOutLies": int((~landed).sum()),
            "liesAboveT1": round(float((lies > t1).mean()), 4) if len(lies) else None,
            "liesAboveT2": round(float((lies > t2).mean()), 4) if len(lies) else None}


def fused(runs_path):
    """The fused check's verdict on every recorded checkable action, from the raw readings: (run, truck, tick, action) -> lied."""
    capacity = {"Truck 1": 10, "Truck 2": 8, "Truck 3": 12}
    out = {}
    run = None
    for line in open(runs_path):
        d = json.loads(line)
        if d["type"] == "run":
            run = f"{d['arm']}/{d['seed']}"
            continue
        dec = d["decision"]
        chosen = dec.get("chosen") or ""
        kind = chosen.split(":")[0]
        if kind not in ("drive", "refuel") or dec.get("verdict") is None:
            continue
        before = dec["seat"]["facts"]
        after = dec.get("factsAfter") or []

        def gauge(facts):
            return next((int(f[5:]) for f in facts if f.startswith("fuel:") and f[5:].lstrip("-").isdigit()), None)

        if kind == "drive":
            _, road, to = chosen.split(":")
            start = ROADS[road][0] if ROADS[road][1] == to else ROADS[road][1]
            g0, g1 = gauge(before), gauge(after)
            lied = f"at:{start}" in after and g0 is not None and g1 is not None and g1 >= g0
        else:
            lied = (gauge(after) or 0) < capacity[dec["truck"]] - 1
        out[(run, dec["truck"], dec["tick"], chosen)] = lied
    return out


ROADS = {}


def judge(layers, path, t1, runs_path, actions):
    rows, x, y = load([path], len(actions))
    s = score(layers, x)
    lied, landed = y == 1, y == 0
    ours = s > t1
    result = {"set": str(path), "actions": len(rows), "lies": int(lied.sum()), "landed": int(landed.sum()),
              "liesAboveT1": round(float(ours[lied].mean()), 4) if lied.any() else None,
              "landedAboveT1": round(float(ours[landed].mean()), 5),
              "judgeFalseAlarms": int((ours & landed).sum()), "judgeMisses": int((~ours & lied).sum())}
    if runs_path:
        check = fused(runs_path)
        theirs = []
        for r in rows:
            key = (r["run"], r["truck"], r["tick"], r["actionId"])
            # Loads and unloads keep the effect check, which the noise does not touch: it calls a lie a lie.
            theirs.append(check.get(key, r["truth"] == "Lied") if r["actionId"].split(":")[0] in ("drive", "refuel") else r["truth"] == "Lied")
        theirs = np.asarray(theirs)
        result |= {"fusedFalseAlarms": int((theirs & landed).sum()), "fusedMisses": int((~theirs & lied).sum())}
        result["held"] = bool(result["liesAboveT1"] > 0.90 and result["landedAboveT1"] < 0.01
                              and result["judgeFalseAlarms"] + result["judgeMisses"] < result["fusedFalseAlarms"] + result["fusedMisses"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("train", type=pathlib.Path, nargs="+")
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--actions", type=pathlib.Path, default=pathlib.Path("datasets/convoy-j/card.json"))
    parser.add_argument("--map", type=pathlib.Path, default=pathlib.Path("datasets/noisy/roads.json"),
                        help="the map's roads as {id: [a, b]}, for the fused check")
    parser.add_argument("--held-out-digit", type=int, default=9)
    parser.add_argument("--calibrate", type=pathlib.Path, required=True, help="the set whose held-out seeds fix t1 and t2")
    parser.add_argument("--hidden", type=int, nargs="+", default=[64, 64])
    parser.add_argument("--epochs", type=int, default=200)
    parser.add_argument("--patience", type=int, default=15)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--unbalanced", action="store_true", help="do not weight the lies up to the landed actions' count")
    parser.add_argument("--judge", type=pathlib.Path)
    parser.add_argument("--runs", type=pathlib.Path)
    parser.add_argument("--results", type=pathlib.Path, default=pathlib.Path("datasets/noisy/results-judge.json"))
    args = parser.parse_args()

    ROADS.update(json.loads(args.map.read_text()))
    card = json.loads(args.actions.read_text())
    actions = card["actions"]
    started = time.time()
    rows, x, y = load(args.train, len(actions))
    if any(r["evaluation"] for r in rows):
        raise SystemExit("an evaluation set was given to train on: the battery's seeds are never trained on")
    seeds = np.asarray([r["seed"] for r in rows])
    held = seeds % 10 == args.held_out_digit
    print(f"{len(rows)} actions ({int(y.sum())} lies), {x.shape[1]} inputs; {int((~held).sum())} to train, {int(held.sum())} held out")
    layers, epoch, best = train(x[~held], y[~held], x[held], y[held], args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed, balance=not args.unbalanced)
    layers = rounded(layers)
    print(f"kept epoch {epoch}: held-out loss {best:.4f}, {time.time() - started:.0f} s")

    crows, cx, cy = load([args.calibrate], len(actions))
    fixed = thresholds(layers, crows, cx, cy, args.held_out_digit)
    print(json.dumps(fixed))
    verdicts = [judge(layers, args.judge, fixed["t1"], args.runs, actions)] if args.judge else []
    for v in verdicts:
        print(json.dumps(v))

    trained = sorted({int(s) for s in seeds[~held]})
    out = {
        "format": FORMAT, "kind": "judge", "world": card["world"], "map": card["map"], "schemaHash": card["schemaHash"],
        "inputs": "features+action+features", "outputs": "the probability the action lied", "features": card["features"], "actions": actions,
        "layers": [{"activation": "relu" if i < len(layers) - 1 else "sigmoid", "weights": w.tolist(), "bias": b.tolist()}
                   for i, (w, b) in enumerate(layers)],
        "training": {"by": "example, on the simulator's truth", "sets": [str(p) for p in args.train], "seeds": [trained[0], trained[-1]],
                     "heldOutSeeds": f"ending in {args.held_out_digit}", "actions": int((~held).sum()), "lies": int(y[~held].sum()),
                     "hidden": args.hidden, "balanced": not args.unbalanced, "epoch": epoch, "randomSeed": args.seed, "heldOutLoss": round(best, 4), "thresholds": fixed},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    args.results.parent.mkdir(parents=True, exist_ok=True)
    args.results.write_text(json.dumps({"thresholds": fixed, "verdicts": verdicts}, indent=2) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB) and {args.results}")


if __name__ == "__main__":
    main()
