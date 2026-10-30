# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 8 of docs/design-trained-models.md: a latent subgoal head over a frozen JEPA, for the guide that calls the seat less.

Reads a world set recorded with the inner seat driving (features before, the action, features after) and a JEPA from
train_jepa.py, and trains a small network G from the encoder's embedding of what a truck sees to the target encoder's
embedding of what it saw after the inner seat's action landed: where the inner seat usually goes next, in the latent
space. The guide then plays the visible action whose predicted next embedding is nearest G(z). Writes plain JSON weights
(format creatura.network/1, kind "goal"), with the thresholds of the guide's surprise trigger: the 99th and 99.9th
percentiles of the JEPA's latent surprise at landed actions on the held-out seeds of the set the JEPA was trained on.

    uv run tools/train/train_goal.py datasets/world/train.jsonl models/convoy-jepa-varied.json \\
        models/convoy-jepa-varied-goal.json --jepa-set datasets/world/train-varied.jsonl
"""

import argparse
import json
import pathlib
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_jepa import Adam, backward, encode, init, labels, latent_surprise, load, network  # noqa: E402
from train_policy import FORMAT, forward, rounded  # noqa: E402


def load_jepa(path):
    doc = json.loads(pathlib.Path(path).read_text())
    if doc["kind"] != "jepa":
        raise SystemExit(f"{path} is a {doc['kind']} model, not a JEPA")
    net = lambda key: [(np.asarray(l["weights"]), np.asarray(l["bias"])) for l in doc[key]]  # noqa: E731
    return doc, (net("encoder"), net("targetEncoder"), net("predictor"))


def relative_error(goal, z, t):
    p = forward(goal, z)[0]
    return float(((p - t) ** 2).sum() / ((t - t.mean(axis=0)) ** 2).sum())


def train(z, t, held, hidden, epochs, patience, rate, batch, seed):
    rng = np.random.default_rng(seed)
    goal = init(rng, [z.shape[1], hidden, t.shape[1]])
    adam = Adam(goal, rate)
    xt, yt = z[~held], t[~held]
    best, best_epoch, kept = np.inf, 0, goal
    for epoch in range(1, epochs + 1):
        order = rng.permutation(len(xt))
        for start in range(0, len(xt), batch):
            idx = order[start:start + batch]
            p, inputs = forward(goal, xt[idx])
            grads, _ = backward(goal, inputs, 2 * (p - yt[idx]) / (len(idx) * t.shape[1]))
            goal = adam.apply(goal, grads)
        error = relative_error(goal, z[held], t[held])
        if error < best:
            best, best_epoch, kept = error, epoch, goal
        if epoch % 5 == 0:
            print(f"epoch {epoch}: held-out relative error {error:.4f} (best {best:.4f} at {best_epoch})", flush=True)
        if epoch - best_epoch >= patience:
            break
    return kept, best_epoch, best


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("train", type=pathlib.Path, help="a world set recorded with the inner seat driving")
    parser.add_argument("jepa", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--jepa-set", type=pathlib.Path, help="the set the JEPA was trained on, for the surprise percentiles (default: the training set)")
    parser.add_argument("--actions", type=pathlib.Path, default=pathlib.Path("datasets/convoy-j/card.json"))
    parser.add_argument("--held-out-digit", type=int, default=9)
    parser.add_argument("--hidden", type=int, default=128)
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--patience", type=int, default=10)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    started = time.time()
    card = json.loads(args.actions.read_text())
    doc, model = load_jepa(args.jepa)
    if doc["schemaHash"] != card["schemaHash"] or doc["actions"] != card["actions"]:
        raise SystemExit(f"{args.jepa} was trained for another schema or action vocabulary than {args.actions}")
    rows, before, action, after = load(args.train, len(card["actions"]))
    if any(r["evaluation"] for r in rows):
        raise SystemExit(f"{args.train} is an evaluation set: the battery's seeds are never trained on")
    _, landed = labels(rows)
    seeds = np.asarray([r["seed"] for r in rows])
    z, t = encode(model[0], before[landed]), encode(model[1], after[landed])
    held = seeds[landed] % 10 == args.held_out_digit
    print(f"{len(rows)} transitions, {int(landed.sum())} landed; {int((~held).sum())} to train, {int(held.sum())} held out")
    goal, epoch, best = train(z, t, held, args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed)
    goal = rounded(goal)

    # The surprise trigger's thresholds, from the JEPA's own held-out training seeds.
    jrows, jb, ja, jn = load(args.jepa_set or args.train, len(card["actions"]))
    _, jlanded = labels(jrows)
    jheld = np.asarray([r["seed"] % 10 == args.held_out_digit for r in jrows]) & jlanded
    s = latent_surprise(model, jb[jheld], ja[jheld], jn[jheld])
    p99, p999 = float(np.percentile(s, 99)), float(np.percentile(s, 99.9))
    print(f"kept epoch {epoch}: held-out relative error {best:.4f}; landed latent surprise p99 {p99:.4f}, p99.9 {p999:.4f}; {time.time() - started:.0f} s")

    trained = sorted({int(x) for x in seeds[landed][~held]})
    out = {
        "format": FORMAT, "kind": "goal", "world": card["world"], "map": card["map"], "schemaHash": card["schemaHash"],
        "jepa": args.jepa.name, "inputs": "embedding", "outputs": "subgoal embedding", "embedding": doc["embedding"],
        "surpriseThresholds": {"p99": round(p99, 6), "p999": round(p999, 6)},
        "layers": network(goal),
        "training": {"by": "example", "set": args.train.name, "arms": sorted({r["arm"] for r in rows}), "seeds": [trained[0], trained[-1]],
                     "heldOutSeeds": f"ending in {args.held_out_digit}", "transitions": int((~held).sum()), "hidden": args.hidden,
                     "rate": args.rate, "batch": args.batch, "epoch": epoch, "randomSeed": args.seed,
                     "heldOutRelativeError": round(best, 4), "surpriseFrom": (args.jepa_set or args.train).name},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
