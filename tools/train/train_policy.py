# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 2 of docs/design-trained-models.md: a policy network trained by example from a convoy dataset.

Reads policy.jsonl and card.json from a dataset made with `--dataset`, trains a small network to choose what the
planner chose, and writes plain JSON weights that Creatura's NetworkSeat reads (format creatura.network/1).

The network's input is the dataset's features plus one bit per action saying whether it is visible this tick. Two
hidden layers with ReLU, one score per action, a softmax over the visible actions only. Seeds are split, not
decisions: the last seeds are held out to pick the stopping point and report accuracy.

    uv run tools/train/train_policy.py datasets/convoy-j models/convoy-policy-j.json --held-out-from 550
"""

import argparse
import json
import math
import pathlib
import time

import numpy as np

FORMAT = "creatura.network/1"


def load(dataset: pathlib.Path):
    card = json.loads((dataset / "card.json").read_text())
    actions = card["actions"]
    xs, ys, seeds = [], [], []
    with open(dataset / "policy.jsonl") as lines:
        for line in lines:
            row = json.loads(line)
            visible = np.zeros(len(actions))
            visible[row["mask"]] = 1.0
            xs.append(np.concatenate([np.asarray(row["features"], dtype=float), visible]))
            ys.append(row["action"])
            seeds.append(row["seed"])
    x = np.stack(xs)
    return card, x, np.asarray(ys), np.asarray(seeds), x[:, len(card["features"]):] > 0


def forward(layers, x):
    """The scores and each layer's input, the arithmetic NetworkSeat repeats in C#."""
    inputs = []
    for i, (w, b) in enumerate(layers):
        inputs.append(x)
        x = x @ w.T + b
        if i < len(layers) - 1:
            x = np.maximum(x, 0.0)
    return x, inputs


def masked_softmax(scores, visible):
    scores = np.where(visible, scores, -1e9)
    scores = scores - scores.max(axis=1, keepdims=True)
    e = np.exp(scores) * visible
    return e / e.sum(axis=1, keepdims=True)


def accuracy(layers, x, y, visible):
    scores, _ = forward(layers, x)
    return float((np.where(visible, scores, -np.inf).argmax(axis=1) == y).mean())


def train(x, y, visible, xv, yv, vv, hidden, epochs, patience, rate, batch, seed):
    rng = np.random.default_rng(seed)
    sizes = [x.shape[1], *hidden, visible.shape[1]]
    layers = [(rng.normal(0, math.sqrt(2 / n), (m, n)), np.zeros(m)) for n, m in zip(sizes, sizes[1:])]
    moments = [[np.zeros_like(p) for p in layer for _ in (0, 1)] for layer in layers]
    step, best, best_epoch, kept = 0, -1.0, 0, layers
    for epoch in range(1, epochs + 1):
        order = rng.permutation(len(x))
        for start in range(0, len(x), batch):
            idx = order[start:start + batch]
            scores, inputs = forward(layers, x[idx])
            grad = masked_softmax(scores, visible[idx])
            grad[np.arange(len(idx)), y[idx]] -= 1.0
            grad /= len(idx)
            step += 1
            updated = []
            for i in reversed(range(len(layers))):
                w, b = layers[i]
                gw, gb = grad.T @ inputs[i], grad.sum(axis=0)
                if i > 0:
                    grad = (grad @ w) * (inputs[i] > 0)
                params = []
                for j, (p, g) in enumerate(((w, gw), (b, gb))):
                    m, v = moments[i][2 * j], moments[i][2 * j + 1]
                    m[...] = 0.9 * m + 0.1 * g
                    v[...] = 0.999 * v + 0.001 * g * g
                    params.append(p - rate * (m / (1 - 0.9 ** step)) / (np.sqrt(v / (1 - 0.999 ** step)) + 1e-8))
                updated.append(tuple(params))
            layers = updated[::-1]
        held = accuracy(layers, xv, yv, vv)
        if held > best:
            best, best_epoch, kept = held, epoch, layers
        if epoch % 10 == 0:
            print(f"epoch {epoch}: train {accuracy(layers, x, y, visible):.4f}, held out {held:.4f} (best {best:.4f} at {best_epoch})")
        if epoch - best_epoch >= patience:
            break
    return kept, best_epoch


def rounded(layers):
    """Weights as they are written: six significant digits, so the accuracy reported is the accuracy of the file."""
    return [(np.vectorize(lambda v: float(f"{v:.6g}"))(w), np.vectorize(lambda v: float(f"{v:.6g}"))(b)) for w, b in layers]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dataset", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--held-out-from", type=int, default=550, help="seeds from this one up are held out")
    parser.add_argument("--hidden", type=int, nargs="+", default=[64, 64])
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--patience", type=int, default=30)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    started = time.time()
    card, x, y, seeds, visible = load(args.dataset)
    held = seeds >= args.held_out_from
    if held.all() or not held.any():
        raise SystemExit(f"--held-out-from {args.held_out_from} leaves nothing to train on or nothing to hold out")
    print(f"{len(x)} decisions, {x.shape[1]} inputs, {visible.shape[1]} actions; {int((~held).sum())} to train, {int(held.sum())} held out")

    layers, epoch = train(x[~held], y[~held], visible[~held], x[held], y[held], visible[held],
                          args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed)
    layers = rounded(layers)
    train_accuracy = accuracy(layers, x[~held], y[~held], visible[~held])
    held_accuracy = accuracy(layers, x[held], y[held], visible[held])
    print(f"kept epoch {epoch}: train {train_accuracy:.4f}, held out {held_accuracy:.4f}, {time.time() - started:.0f} s")

    trained = sorted({int(s) for s in seeds[~held]})
    out = {
        "format": FORMAT,
        "kind": "policy",
        "world": card["world"],
        "map": card["map"],
        "schemaHash": card["schemaHash"],
        "inputs": "features+visible",
        "features": card["features"],
        "actions": card["actions"],
        "layers": [{"activation": "relu" if i < len(layers) - 1 else "none",
                    "weights": w.tolist(), "bias": b.tolist()} for i, (w, b) in enumerate(layers)],
        "training": {
            "by": "example",
            "dataset": card["format"],
            "arms": card["arms"],
            "seeds": [trained[0], trained[-1]],
            "heldOutSeeds": [args.held_out_from, int(seeds.max())],
            "decisions": int((~held).sum()),
            "hidden": args.hidden,
            "epoch": epoch,
            "randomSeed": args.seed,
            "trainAccuracy": round(train_accuracy, 4),
            "heldOutAccuracy": round(held_accuracy, 4),
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
