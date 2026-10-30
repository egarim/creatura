# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 9 of docs/design-trained-models.md: a network that copies DeepSeek, from the runs already recorded.

Reads a recording (`--record`) of a model seat's runs and trains arm N's network shape to choose what the model chose.
The only recorded DeepSeek runs are on the battery's seeds 0 to 4, which the owner allowed as an exception on these
terms: every score is on a seed left out of training (five folds: for the fold that scores seed k, the stopping point
is chosen on seed (k + 1) mod 5 and the weights fit on the other three), the scores are offline only, and the file is
marked so the convoy refuses to run it on seeds 0 to 49.

On each left-out seed it scores the copy, arm N's network (the planner's copy) and step 8's JEPA guide by how often
their best visible action is the model's choice, and by the guide's rule: with two or more visible actions, below a
margin m the guide asks the seat, otherwise it plays its best.

    uv run tools/train/train_copy.py datasets/deepseek-s/record-s-deepseek.jsonl models/convoy-copy-deepseek.json
"""

import argparse
import json
import pathlib
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_jepa import encode, predict_next  # noqa: E402
from train_policy import FORMAT, accuracy, forward, rounded, train  # noqa: E402

GRID = [0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99]


def load(path, card):
    runs, rows = {}, []
    for line in open(path):
        r = json.loads(line)
        if r["type"] == "run":
            runs[f"{r['arm']}/{r['seed']}"] = r
        else:
            rows.append(r)
    actions = card["actions"]
    x, y, visible, seeds, kinds, lied = [], [], [], [], [], []
    for r in rows:
        d = r["decision"]
        if d.get("actionIndex") is None:
            continue
        if len(d["features"]) != len(card["features"]):
            raise SystemExit(f"{path}: {len(d['features'])} features, the card names {len(card['features'])}")
        mask = np.zeros(len(actions))
        mask[d["mask"]] = 1.0
        x.append(np.concatenate([np.asarray(d["features"], dtype=float), mask]))
        y.append(d["actionIndex"])
        visible.append(mask > 0)
        seeds.append(runs[r["run"]]["seed"])
        kinds.append(d["chosen"].split(":")[0])
        lied.append(d.get("verdict") == "Lied")
    models = sorted({runs[k].get("seatModel") or "planner" for k in runs})
    return np.stack(x), np.asarray(y), np.stack(visible), np.asarray(seeds), np.asarray(kinds), np.asarray(lied), models, sorted(runs)


def network_rank(layers, x, visible):
    """Best visible action and the margin 1 - exp(s2 - s1) between the two best scores (1 with a single choice)."""
    scores = np.where(visible, forward(layers, x)[0], -np.inf)
    order = np.argsort(-scores, axis=1)
    s1 = scores[np.arange(len(x)), order[:, 0]]
    s2 = scores[np.arange(len(x)), order[:, 1]]
    margin = np.where(np.isfinite(s2), 1 - np.exp(np.minimum(s2 - s1, 0)), 1.0)
    return order[:, 0], margin


def jepa_rank(jepa, goal, x, visible, features):
    """Step 8's guide: the visible action whose predicted next embedding is nearest the subgoal; margin (d2 - d1) / d2."""
    encoder, _, predictor = jepa
    z = encode(encoder, x[:, :features])
    g = forward(goal, z)[0]
    actions = visible.shape[1]
    d = np.full((len(x), actions), np.inf)
    for a in range(actions):
        rows = visible[:, a]
        if rows.any():
            act = np.zeros((rows.sum(), actions))
            act[:, a] = 1
            d[rows, a] = ((predict_next(predictor, z[rows], act) - g[rows]) ** 2).sum(axis=1)
    order = np.argsort(d, axis=1)
    d1 = d[np.arange(len(x)), order[:, 0]]
    d2 = d[np.arange(len(x)), order[:, 1]]
    margin = np.where(np.isfinite(d2), np.where(d2 > 0, (d2 - d1) / np.where(d2 > 0, d2, 1), 0), 1.0)
    return order[:, 0], margin


def curve(best, margin, y, visible):
    """For each m: the share of decisions on which the guide asks, and how often its unasked choices match."""
    several = visible.sum(axis=1) > 1
    out = {}
    for m in GRID:
        asks = several & (margin < m)
        played = ~asks
        out[str(m)] = {"asks": round(float(asks.mean()), 4),
                       "agreeUnasked": round(float((best[played] == y[played]).mean()), 4) if played.any() else None}
    return out


def score(name, best, margin, y, visible, kinds, lied):
    agree = best == y
    return {
        "guide": name, "decisions": int(len(y)), "agreement": round(float(agree.mean()), 4),
        "curve": curve(best, margin, y, visible),
        "byKind": {k: {"decisions": int((kinds == k).sum()), "agreement": round(float(agree[kinds == k].mean()), 4)} for k in sorted(set(kinds))},
        "liesRepeated": {"lies": int(lied.sum()), "sameChoice": int(agree[lied].sum())},
    }


def layers_of(doc, key="layers"):
    return [(np.asarray(l["weights"]), np.asarray(l["bias"])) for l in doc[key]]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("recording", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--actions", type=pathlib.Path, default=pathlib.Path("datasets/convoy-j/card.json"))
    parser.add_argument("--planner-copy", type=pathlib.Path, default=pathlib.Path("models/convoy-policy-j.json"))
    parser.add_argument("--jepa", type=pathlib.Path, default=pathlib.Path("models/convoy-jepa-varied.json"))
    parser.add_argument("--goal", type=pathlib.Path, default=pathlib.Path("models/convoy-jepa-varied-goal.json"))
    parser.add_argument("--margin", type=float, default=0.75)
    parser.add_argument("--hidden", type=int, nargs="+", default=[64, 64])
    parser.add_argument("--epochs", type=int, default=300)
    parser.add_argument("--patience", type=int, default=30)
    parser.add_argument("--rate", type=float, default=1e-3)
    parser.add_argument("--batch", type=int, default=64)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--results", type=pathlib.Path, default=pathlib.Path("datasets/deepseek-s/results-copy.json"))
    args = parser.parse_args()

    started = time.time()
    card = json.loads(args.actions.read_text())
    x, y, visible, seeds, kinds, lied, models, run_ids = load(args.recording, card)
    folds = sorted(set(int(s) for s in seeds))
    if len(folds) < 3:
        raise SystemExit("leaving one seed out needs at least three seeds")
    print(f"{len(y)} decisions by {', '.join(models)} in {len(run_ids)} runs on seeds {folds}")

    planner = layers_of(json.loads(args.planner_copy.read_text()))
    jepa_doc = json.loads(args.jepa.read_text())
    jepa = (layers_of(jepa_doc, "encoder"), layers_of(jepa_doc, "targetEncoder"), layers_of(jepa_doc, "predictor"))
    goal = layers_of(json.loads(args.goal.read_text()))
    features = len(card["features"])

    pooled = {"copy": ([], [], []), "planner-copy": ([], [], []), "jepa-guide": ([], [], [])}
    held_index = []
    fold_notes = []
    for k, test in enumerate(folds):
        stop = folds[(k + 1) % len(folds)]
        fit = (seeds != test) & (seeds != stop)
        stp, tst = seeds == stop, seeds == test
        layers, epoch = train(x[fit], y[fit], visible[fit], x[stp], y[stp], visible[stp],
                              args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed)
        layers = rounded(layers)
        held_index.append(np.flatnonzero(tst))
        for name, (best, margin) in (("copy", network_rank(layers, x[tst], visible[tst])),
                                     ("planner-copy", network_rank(planner, x[tst], visible[tst])),
                                     ("jepa-guide", jepa_rank(jepa, goal, x[tst], visible[tst], features))):
            pooled[name][0].append(best)
            pooled[name][1].append(margin)
        acc = accuracy(layers, x[tst], y[tst], visible[tst])
        fold_notes.append({"test": test, "stop": stop, "fit": int(fit.sum()), "epoch": epoch, "agreement": round(acc, 4)})
        print(f"fold {k}: score seed {test}, stop on {stop}, fit {int(fit.sum())} decisions, kept epoch {epoch}, agreement {acc:.4f}")

    order = np.concatenate(held_index)
    ys, vs, ks, ls = y[order], visible[order], kinds[order], lied[order]
    results = {name: score(name, np.concatenate(b), np.concatenate(m), ys, vs, ks, ls) for name, (b, m, _) in pooled.items()}
    copy, planner_copy = results["copy"], results["planner-copy"]
    at = copy["curve"][str(args.margin)]
    verdicts = {
        "T15": {"held": copy["agreement"] > planner_copy["agreement"],
                "evidence": f"copy {copy['agreement']:.4f}, planner copy {planner_copy['agreement']:.4f}, JEPA guide {results['jepa-guide']['agreement']:.4f}"},
        "T15b": {"held": at["asks"] <= 0.5 and (at["agreeUnasked"] or 0) >= 0.95,
                 "evidence": f"at m {args.margin}: asks on {at['asks']:.4f}, unasked agreement {at['agreeUnasked']}"},
    }
    for name, r in results.items():
        print(f"{name}: agreement {r['agreement']:.4f}; at m {args.margin}: {r['curve'][str(args.margin)]}; lies repeated {r['liesRepeated']}")
    for t, v in verdicts.items():
        print(f"{t} {'held' if v['held'] else 'failed'}: {v['evidence']}")

    # The file that ships: fit on all but the last seed, stopped on the last.
    stop = folds[-1]
    fit, stp = seeds != stop, seeds == stop
    layers, epoch = train(x[fit], y[fit], visible[fit], x[stp], y[stp], visible[stp],
                          args.hidden, args.epochs, args.patience, args.rate, args.batch, args.seed)
    layers = rounded(layers)
    out = {
        "format": FORMAT, "kind": "policy", "world": card["world"], "map": card["map"], "schemaHash": card["schemaHash"],
        "inputs": "features+visible", "features": card["features"], "actions": card["actions"],
        "layers": [{"activation": "relu" if i < len(layers) - 1 else "none", "weights": w.tolist(), "bias": b.tolist()}
                   for i, (w, b) in enumerate(layers)],
        "training": {
            "by": "example", "copies": models, "recording": args.recording.name, "runs": run_ids,
            "seeds": [folds[0], folds[-2]], "stopSeed": stop, "decisions": int(fit.sum()),
            "batteryException": True,
            "note": "trained on battery seeds by the owner's exception (docs/design-trained-models.md, step 9): never run on seeds 0 to 49",
            "hidden": args.hidden, "batch": args.batch, "epoch": epoch, "randomSeed": args.seed,
            "stopAccuracy": round(accuracy(layers, x[stp], y[stp], visible[stp]), 4),
            "leaveOneSeedOut": {"folds": fold_notes, "agreement": copy["agreement"], "atMargin": {str(args.margin): at}},
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(out, separators=(",", ":")) + "\n")
    args.results.parent.mkdir(parents=True, exist_ok=True)
    args.results.write_text(json.dumps({"verdicts": verdicts, "results": results, "folds": fold_notes}, indent=2) + "\n")
    print(f"wrote {args.output} ({args.output.stat().st_size // 1024} KB) and {args.results}; {time.time() - started:.0f} s")


if __name__ == "__main__":
    main()
