# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 7 of docs/design-trained-models.md: a world model that predicts in a representation it learns (JEPA).

Reads a world set written by the convoy's `--world` option (features before, the action, features after) and trains
three networks: an encoder from the features to an embedding, a target encoder kept as an exponential moving average
of it, and a predictor from (embedding, action) to the next embedding. The loss is the squared distance between the
predicted embedding and the target encoder's embedding of the features after, with no gradient into the target, plus
VICReg's variance and covariance terms on the encoder's embeddings so that it cannot map every state to one point.
Writes plain JSON weights (format creatura.network/1, kind "jepa"). The latent surprise of an action that ran is the
squared distance between the predicted and the target-encoded next embedding.

With --judge it also reads evaluation sets and scores step 7's claims: how well the latent surprise separates lies
from landed actions (AUC), against the forward world model named by --against.

    uv run tools/train/train_jepa.py datasets/world/train-varied.jsonl models/convoy-jepa-varied.json --held-out-digit 9 \\
        --against models/convoy-world-varied.json --judge datasets/world/eval-j.jsonl datasets/world/eval-deepseek.jsonl
"""

import argparse
import json
import pathlib
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_policy import FORMAT, forward, rounded  # noqa: E402
from train_world import predict as world_predict, surprise as world_surprise  # noqa: E402

VAR_EPS = 1e-4


def load(path, actions_count):
    rows = [json.loads(line) for line in open(path)]
    before = np.asarray([r["before"] for r in rows], dtype=float)
    after = np.asarray([r["after"] for r in rows], dtype=float)
    action = np.zeros((len(rows), actions_count))
    action[np.arange(len(rows)), [r["action"] for r in rows]] = 1.0
    return rows, before, action, after


def labels(rows):
    """Lies and landed actions by the truth where a set records it (the noisy world), else by the verdict (the clean
    world, where the check is exact). Honest failures are neither."""
    said = [r.get("truth") or r["verdict"] for r in rows]
    return np.asarray([s == "Lied" for s in said]), np.asarray([s == "Landed" for s in said])


def init(rng, sizes):
    return [(rng.normal(0, np.sqrt(2 / n), (m, n)), np.zeros(m)) for n, m in zip(sizes, sizes[1:])]


def backward(layers, inputs, grad):
    """The gradients of each layer's weights and bias, and the gradient at the network's input (relu between, linear out)."""
    grads = [None] * len(layers)
    for i in reversed(range(len(layers))):
        w, _ = layers[i]
        grads[i] = (grad.T @ inputs[i], grad.sum(axis=0))
        grad = grad @ w
        if i > 0:
            grad = grad * (inputs[i] > 0)
    return grads, grad


class Adam:
    def __init__(self, layers, rate):
        self.rate, self.step = rate, 0
        self.moments = [[np.zeros_like(p) for p in layer for _ in (0, 1)] for layer in layers]

    def apply(self, layers, grads):
        self.step += 1
        updated = []
        for i, ((w, b), (gw, gb)) in enumerate(zip(layers, grads)):
            params = []
            for j, (param, g) in enumerate(((w, gw), (b, gb))):
                m, v = self.moments[i][2 * j], self.moments[i][2 * j + 1]
                m[...] = 0.9 * m + 0.1 * g
                v[...] = 0.999 * v + 0.001 * g * g
                params.append(param - self.rate * (m / (1 - 0.9 ** self.step)) / (np.sqrt(v / (1 - 0.999 ** self.step)) + 1e-8))
            updated.append(tuple(params))
        return updated


def encode(layers, x):
    return forward(layers, x)[0]


def predict_next(predictor, z, action):
    return forward(predictor, np.concatenate([z, action], axis=1))[0]


def latent_surprise(model, before, action, after):
    """The squared distance between the predicted next embedding and the target encoder's embedding of what was observed."""
    encoder, target, predictor = model
    p = predict_next(predictor, encode(encoder, before), action)
    return ((p - encode(target, after)) ** 2).sum(axis=1)


def relative_error(model, before, action, after):
    """The prediction error as a share of the target embedding's variance: the stopping measure, which uses no labels
    and does not fall just because the embedding shrinks."""
    encoder, target, predictor = model
    p = predict_next(predictor, encode(encoder, before), action)
    t = encode(target, after)
    return float(((p - t) ** 2).sum() / ((t - t.mean(axis=0)) ** 2).sum())


def vicreg(z):
    """VICReg's variance and covariance terms on a batch of embeddings, and their gradients."""
    n, d = z.shape
    zc = z - z.mean(axis=0)
    std = np.sqrt((zc ** 2).mean(axis=0) + VAR_EPS)
    short = std < 1
    var = float(np.maximum(1 - std, 0).mean())
    g_var = -(short / std) * zc / (d * n)
    cov = zc.T @ zc / (n - 1)
    off = cov - np.diag(np.diag(cov))
    cov_loss = float((off ** 2).sum() / d)
    g_cov = zc @ (2 * off / d) * 2 / (n - 1)
    return var, cov_loss, g_var, g_cov


def auc(scores, positive, negative):
    """The share of (positive, negative) pairs in which the positive scores higher, ties counting half."""
    s = np.concatenate([scores[positive], scores[negative]])
    _, where, counts = np.unique(s, return_inverse=True, return_counts=True)
    ranks = (np.cumsum(counts) - (counts - 1) / 2)[where]  # tied scores share their average rank
    p, q = positive.sum(), negative.sum()
    return float((ranks[:p].sum() - p * (p + 1) / 2) / (p * q))


def train(data, held, embedding, hidden, epochs, patience, rate, batch, ema, weights, seed, check=None):
    before, action, after = data
    xb, xa, xn = before[~held], action[~held], after[~held]
    rng = np.random.default_rng(seed)
    encoder = init(rng, [before.shape[1], hidden, embedding])
    predictor = init(rng, [embedding + action.shape[1], hidden, embedding])
    target = [(w.copy(), b.copy()) for w, b in encoder]
    adam_e, adam_p = Adam(encoder, rate), Adam(predictor, rate)
    w_pred, w_var, w_cov = weights
    best, best_epoch, kept = np.inf, 0, None
    for epoch in range(1, epochs + 1):
        order = rng.permutation(len(xb))
        for start in range(0, len(xb), batch):
            idx = order[start:start + batch]
            n = len(idx)
            z, enc_inputs = forward(encoder, xb[idx])
            t = encode(target, xn[idx])
            p, pred_inputs = forward(predictor, np.concatenate([z, xa[idx]], axis=1))
            g_p = w_pred * 2 * (p - t) / (n * embedding)
            pred_grads, g_in = backward(predictor, pred_inputs, g_p)
            _, _, g_var, g_cov = vicreg(z)
            g_z = g_in[:, :embedding] + w_var * g_var + w_cov * g_cov
            enc_grads, _ = backward(encoder, enc_inputs, g_z)
            encoder = adam_e.apply(encoder, enc_grads)
            predictor = adam_p.apply(predictor, pred_grads)
            target = [(ema * tw + (1 - ema) * w, ema * tb + (1 - ema) * b) for (tw, tb), (w, b) in zip(target, encoder)]
        model = (encoder, target, predictor)
        held_error = relative_error(model, before[held], action[held], after[held])
        if held_error < best:
            best, best_epoch, kept = held_error, epoch, model
        line = f"epoch {epoch}: held-out relative error {held_error:.4f} (best {best:.4f} at {best_epoch})"
        if check is not None:
            line += f", held-out AUC {check(model):.4f}"
        print(line, flush=True)
        if epoch - best_epoch >= patience:
            break
    return kept, best_epoch, best


def collapse(model, before):
    """The spread of each embedding number, and how many numbers carry 99% of the variance."""
    z = encode(model[0], before)
    eig = np.sort(np.linalg.eigvalsh(np.cov(z.T)))[::-1]
    share = np.cumsum(eig) / eig.sum()
    std = z.std(axis=0)
    return {"stdMin": round(float(std.min()), 4), "stdMedian": round(float(np.median(std)), 4),
            "dimsFor99": int(np.searchsorted(share, 0.99) + 1), "dims": int(z.shape[1])}


def above_p95(scores, lied, landed):
    p95 = np.percentile(scores[landed], 95)
    return round(float((scores[lied] > p95).mean()), 4)


def judge(model, world, path, actions, resamples, seed):
    rows, before, action, after = load(path, len(actions))
    lied, landed = labels(rows)
    latent = latent_surprise(model, before, action, after)
    x = np.concatenate([before, action], axis=1)
    forward_s = world_surprise(world_predict(world, x)[0], after)
    a_latent, a_forward = auc(latent, lied, landed), auc(forward_s, lied, landed)
    rng = np.random.default_rng(seed)
    li, la = np.flatnonzero(lied), np.flatnonzero(landed)
    diffs = []
    for _ in range(resamples):
        idx = np.concatenate([rng.choice(li, len(li)), rng.choice(la, len(la))])
        pos = np.zeros(len(idx), bool)
        pos[:len(li)] = True
        diffs.append(auc(latent[idx], pos, ~pos) - auc(forward_s[idx], pos, ~pos))
    kinds = {}
    for kind in sorted({r["actionId"].split(":")[0] for r in rows}):
        k = np.asarray([r["actionId"].split(":")[0] == kind for r in rows])
        if (k & lied).any() and (k & landed).any():
            kinds[kind] = {"lies": int((k & lied).sum()), "jepa": round(auc(latent, k & lied, k & landed), 4),
                           "world": round(auc(forward_s, k & lied, k & landed), 4)}
    return {
        "set": str(path), "transitions": len(rows), "lies": int(lied.sum()), "landed": int(landed.sum()),
        "aucJepa": round(a_latent, 4), "aucWorld": round(a_forward, 4), "held": bool(a_latent >= a_forward),
        "aucDifference95": [round(float(np.percentile(diffs, 2.5)), 4), round(float(np.percentile(diffs, 97.5)), 4)],
        "liesAboveP95Jepa": above_p95(latent, lied, landed), "liesAboveP95World": above_p95(forward_s, lied, landed),
        "collapse": collapse(model, before), "byKind": kinds,
    }


def load_world(path):
    doc = json.loads(pathlib.Path(path).read_text())
    if doc["kind"] != "world":
        raise SystemExit(f"{path} is a {doc['kind']} model, not a world model")
    return doc, [(np.asarray(l["weights"]), np.asarray(l["bias"])) for l in doc["layers"]]


def network(layers):
    return [{"activation": "relu" if i < len(layers) - 1 else "none", "weights": w.tolist(), "bias": b.tolist()}
            for i, (w, b) in enumerate(layers)]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("train", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--actions", type=pathlib.Path, default=pathlib.Path("datasets/convoy-j/card.json"),
                        help="a dataset card, for the feature names and the action vocabulary")
    parser.add_argument("--held-out-digit", type=int, default=9, help="hold out the seeds ending in this digit")
    parser.add_argument("--embedding", type=int, default=32)
    parser.add_argument("--hidden", type=int, default=128)
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--patience", type=int, default=8)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--ema", type=float, default=0.99)
    parser.add_argument("--weights", type=float, nargs=3, default=[25.0, 25.0, 1.0], metavar=("PREDICT", "VARIANCE", "COVARIANCE"))
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--against", type=pathlib.Path, help="the forward world model to compare with (kind world)")
    parser.add_argument("--judge", type=pathlib.Path, nargs="*", default=[])
    parser.add_argument("--resamples", type=int, default=2000)
    parser.add_argument("--results", type=pathlib.Path, default=pathlib.Path("datasets/world/results-jepa.json"))
    args = parser.parse_args()
    if args.judge and not args.against:
        parser.error("--judge needs --against, the forward world model to compare with")

    card = json.loads(args.actions.read_text())
    actions = card["actions"]
    started = time.time()
    rows, before, action, after = load(args.train, len(actions))
    if any(r["evaluation"] for r in rows):
        raise SystemExit(f"{args.train} is an evaluation set: the battery's seeds are never trained on")
    seeds = np.asarray([r["seed"] for r in rows])
    held = seeds % 10 == args.held_out_digit
    print(f"{len(rows)} transitions, {before.shape[1]} features, {len(actions)} actions; "
          f"{int((~held).sum())} to train, {int(held.sum())} held out")

    world = load_world(args.against)[1] if args.against else None
    check = None
    if world is not None:
        # Lie separation on the held-out training seeds, printed each epoch and for the one tuned setting; never the battery's.
        lied, landed = labels([r for r, h in zip(rows, held) if h])
        hb, ha, hn = before[held], action[held], after[held]
        held_world_auc = auc(world_surprise(world_predict(world, np.concatenate([hb, ha], axis=1))[0], hn), lied, landed)
        print(f"held-out seeds: {int(lied.sum())} lies; the forward model's AUC there {held_world_auc:.4f}")
        check = lambda m: auc(latent_surprise(m, hb, ha, hn), lied, landed)  # noqa: E731

    model, epoch, best = train((before, action, after), held, args.embedding, args.hidden, args.epochs, args.patience,
                               args.rate, args.batch, args.ema, args.weights, args.seed, check)
    model = tuple(rounded(part) for part in model)
    held_auc = check(model) if check else None
    print(f"kept epoch {epoch}: held-out relative error {best:.4f}"
          + (f", held-out AUC {held_auc:.4f} (forward model {held_world_auc:.4f})" if check else "")
          + f", {time.time() - started:.0f} s")
    print("collapse check on held-out seeds:", json.dumps(collapse(model, before[held])))

    verdicts = [judge(model, world, path, actions, args.resamples, args.seed) for path in args.judge]
    for v in verdicts:
        print(json.dumps({k: v[k] for k in ("set", "lies", "aucJepa", "aucWorld", "held", "aucDifference95",
                                            "liesAboveP95Jepa", "liesAboveP95World")}))

    trained = sorted({int(s) for s in seeds[~held]})
    encoder, target, predictor = model
    out = {
        "format": FORMAT, "kind": "jepa", "world": card["world"], "map": card["map"], "schemaHash": card["schemaHash"],
        "inputs": "features+action", "outputs": "embedding", "embedding": args.embedding,
        "surprise": "squared distance between the predicted and the target-encoded next embedding",
        "features": card["features"], "actions": actions,
        "encoder": network(encoder), "targetEncoder": network(target), "predictor": network(predictor),
        "training": {"by": "example", "arms": sorted({r["arm"] for r in rows}), "set": args.train.name,
                     "seeds": [trained[0], trained[-1]], "heldOutSeeds": f"ending in {args.held_out_digit}",
                     "transitions": int((~held).sum()), "hidden": args.hidden, "ema": args.ema,
                     "lossWeights": {"predict": args.weights[0], "variance": args.weights[1], "covariance": args.weights[2]},
                     "rate": args.rate, "batch": args.batch, "epoch": epoch, "randomSeed": args.seed,
                     "heldOutRelativeError": round(best, 4),
                     **({"heldOutAuc": round(held_auc, 4), "heldOutAucWorld": round(held_world_auc, 4),
                         "comparedWith": args.against.name} if check else {})},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    if verdicts:
        args.results.parent.mkdir(parents=True, exist_ok=True)
        args.results.write_text(json.dumps(verdicts, indent=2) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB)" + (f" and {args.results}" if verdicts else ""))


if __name__ == "__main__":
    main()
