# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 5 of docs/design-trained-models.md: a world model for the convoy, trained by example, and its surprise.

Reads a world set written by the convoy's `--world` option (features before, the action, features after), trains a
small network to predict the features after from the features before and the action, and writes plain JSON weights
(format creatura.network/1, kind "world"). The surprise of an action that ran is the cross-entropy between what the
model predicted and what was observed, summed over the features.

With --judge it also reads evaluation sets and scores T6: the median surprise at a lie against the 95th percentile of
the surprise at actions that landed.

    uv run tools/train/train_world.py datasets/world/train.jsonl models/convoy-world-j.json \\
        --judge datasets/world/eval-j.jsonl datasets/world/eval-deepseek.jsonl
"""

import argparse
import json
import pathlib
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_policy import FORMAT, forward, rounded  # noqa: E402

EPS = 1e-6


def load(path, actions_count):
    rows = [json.loads(line) for line in open(path)]
    before = np.asarray([r["before"] for r in rows], dtype=float)
    after = np.asarray([r["after"] for r in rows], dtype=float)
    action = np.zeros((len(rows), actions_count))
    action[np.arange(len(rows)), [r["action"] for r in rows]] = 1.0
    return rows, np.concatenate([before, action], axis=1), after


def predict(layers, x):
    scores, inputs = forward(layers, x)
    return 1.0 / (1.0 + np.exp(-scores)), inputs


def surprise(p, observed):
    """Cross-entropy of what was observed under what was predicted, summed over the features."""
    p = np.clip(p, EPS, 1 - EPS)
    return -(observed * np.log(p) + (1 - observed) * np.log(1 - p)).sum(axis=1)


def train(x, y, xv, yv, hidden, epochs, patience, rate, batch, seed):
    rng = np.random.default_rng(seed)
    sizes = [x.shape[1], *hidden, y.shape[1]]
    layers = [(rng.normal(0, np.sqrt(2 / n), (m, n)), np.zeros(m)) for n, m in zip(sizes, sizes[1:])]
    moments = [[np.zeros_like(p) for p in layer for _ in (0, 1)] for layer in layers]
    step, best, best_epoch, kept = 0, np.inf, 0, layers
    for epoch in range(1, epochs + 1):
        order = rng.permutation(len(x))
        for start in range(0, len(x), batch):
            idx = order[start:start + batch]
            p, inputs = predict(layers, x[idx])
            grad = (p - y[idx]) / len(idx)  # cross-entropy through the sigmoid
            step += 1
            updated = []
            for i in reversed(range(len(layers))):
                w, b = layers[i]
                gw, gb = grad.T @ inputs[i], grad.sum(axis=0)
                if i > 0:
                    grad = (grad @ w) * (inputs[i] > 0)
                params = []
                for j, (param, g) in enumerate(((w, gw), (b, gb))):
                    m, v = moments[i][2 * j], moments[i][2 * j + 1]
                    m[...] = 0.9 * m + 0.1 * g
                    v[...] = 0.999 * v + 0.001 * g * g
                    params.append(param - rate * (m / (1 - 0.9 ** step)) / (np.sqrt(v / (1 - 0.999 ** step)) + 1e-8))
                updated.append(tuple(params))
            layers = updated[::-1]
        held = float(surprise(predict(layers, xv)[0], yv).mean())
        if held < best:
            best, best_epoch, kept = held, epoch, layers
        if epoch % 5 == 0:
            print(f"epoch {epoch}: held-out surprise {held:.4f} (best {best:.4f} at {best_epoch})", flush=True)
        if epoch - best_epoch >= patience:
            break
    return kept, best_epoch, best


def judge(layers, path, actions):
    rows, x, y = load(path, len(actions))
    s = surprise(predict(layers, x)[0], y)
    lied = np.asarray([r["verdict"] == "Lied" for r in rows])
    landed = np.asarray([r["verdict"] == "Landed" for r in rows])
    p95 = float(np.percentile(s[landed], 95))
    median_lie = float(np.median(s[lied])) if lied.any() else float("nan")
    p = predict(layers, x)[0]
    exact = float((np.abs(p[landed] - y[landed]) < 0.05).all(axis=1).mean())
    kinds = {}
    for kind in sorted({r["actionId"].split(":")[0] for r in rows}):
        k = np.asarray([r["actionId"].split(":")[0] == kind for r in rows])
        kinds[kind] = {"landed": round(float(np.median(s[k & landed])), 3) if (k & landed).any() else None,
                       "lied": round(float(np.median(s[k & lied])), 3) if (k & lied).any() else None,
                       "lies": int((k & lied).sum())}
    return {
        "set": str(path), "transitions": len(rows), "lies": int(lied.sum()), "landed": int(landed.sum()),
        "landedP95": round(p95, 4), "lieMedian": round(median_lie, 4),
        "held": bool(median_lie > p95),
        "liesAboveP95": round(float((s[lied] > p95).mean()), 4) if lied.any() else None,
        "landedPredictedExactly": round(exact, 4),
        "byKind": kinds,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("train", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--actions", type=pathlib.Path, default=pathlib.Path("datasets/convoy-j/card.json"),
                        help="a dataset card, for the feature names and the action vocabulary")
    parser.add_argument("--held-out-from", type=int, default=550)
    parser.add_argument("--held-out-digit", type=int, help="hold out the seeds ending in this digit instead of --held-out-from")
    parser.add_argument("--hidden", type=int, nargs="+", default=[128, 128])
    parser.add_argument("--epochs", type=int, default=200)
    parser.add_argument("--patience", type=int, default=15)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--judge", type=pathlib.Path, nargs="*", default=[])
    parser.add_argument("--results", type=pathlib.Path, default=pathlib.Path("datasets/world/results.json"))
    args = parser.parse_args()

    card = json.loads(args.actions.read_text())
    actions = card["actions"]
    started = time.time()
    rows, x, y = load(args.train, len(actions))
    if any(r["evaluation"] for r in rows):
        raise SystemExit(f"{args.train} is an evaluation set: the battery's seeds are never trained on")
    seeds = np.asarray([r["seed"] for r in rows])
    held = seeds % 10 == args.held_out_digit if args.held_out_digit is not None else seeds >= args.held_out_from
    print(f"{len(rows)} transitions, {x.shape[1]} inputs, {y.shape[1]} outputs; {int((~held).sum())} to train, {int(held.sum())} held out")
    layers, epoch, best = train(x[~held], y[~held], x[held], y[held], args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed)
    layers = rounded(layers)
    print(f"kept epoch {epoch}: held-out surprise {best:.4f}, {time.time() - started:.0f} s")

    verdicts = [judge(layers, path, actions) for path in args.judge]
    for v in verdicts:
        print(json.dumps({k: v[k] for k in ("set", "lies", "landedP95", "lieMedian", "held", "liesAboveP95", "landedPredictedExactly")}))

    trained = sorted({int(s) for s in seeds[~held]})
    out = {
        "format": FORMAT, "kind": "world", "world": card["world"], "map": card["map"], "schemaHash": card["schemaHash"],
        "inputs": "features+action", "outputs": "features after", "features": card["features"], "actions": actions,
        "layers": [{"activation": "relu" if i < len(layers) - 1 else "sigmoid", "weights": w.tolist(), "bias": b.tolist()}
                   for i, (w, b) in enumerate(layers)],
        "training": {"by": "example", "arms": card["arms"], "seeds": [trained[0], trained[-1]],
                     "heldOutSeeds": f"ending in {args.held_out_digit}" if args.held_out_digit is not None else [args.held_out_from, int(seeds.max())],
                     "transitions": int((~held).sum()),
                     "hidden": args.hidden, "epoch": epoch, "randomSeed": args.seed, "heldOutSurprise": round(best, 4)},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    args.results.parent.mkdir(parents=True, exist_ok=True)
    args.results.write_text(json.dumps(verdicts, indent=2) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB) and {args.results}")


if __name__ == "__main__":
    main()
