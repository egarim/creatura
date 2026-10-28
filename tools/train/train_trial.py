# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Step 3 of docs/design-trained-models.md: the policy network trained further by trial in the simulator.

Starts from a network trained by example (train_policy.py) and improves it with PPO, in numpy. Each round the C#
convoy plays 64 runs with the network's choices sampled at a temperature (`--rollouts`), the trainer reads what each
choice came to from the recording, and updates the network. Every 10 rounds the network plays the selection seeds
without sampling, and the one with the best total reward there is kept; the starting network is scored first.

The reward, per truck and decision, is the judge's own reading: +1 per unit delivered, -5 for an action the control
layer judged a lie, -20 when the truck becomes stranded, -0.02 per tick it is still working.

    uv run tools/train/train_trial.py models/convoy-policy-j.json models/convoy-policy-j-trial.json
"""

import argparse
import json
import pathlib
import subprocess
import sys
import time

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from train_policy import FORMAT, forward, rounded  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
CONVOY = ROOT / "samples/Convoy/bin/Release/net10.0/Convoy.dll"


def read_network(path):
    data = json.loads(pathlib.Path(path).read_text())
    if data["format"] != FORMAT:
        raise SystemExit(f"{path}: format {data['format']}")
    layers = [(np.asarray(l["weights"], dtype=float), np.asarray(l["bias"], dtype=float)) for l in data["layers"]]
    return data, layers


def write_network(path, data, layers, training):
    out = dict(data)
    out["layers"] = [{"activation": "relu" if i < len(layers) - 1 else "none", "weights": w.tolist(), "bias": b.tolist()}
                     for i, (w, b) in enumerate(layers)]
    out["training"] = training
    pathlib.Path(path).write_text(json.dumps(out, separators=(",", ":")) + "\n")


def load_of(facts):
    return next((int(f[5:]) for f in facts if f.startswith("load:")), 0)


def reward(d):
    before, after = d["seat"]["facts"], d["factsAfter"]
    r = -0.02
    if d.get("verdict") == "Lied":
        r -= 5.0
    if "stranded" in after and "stranded" not in before:
        r -= 20.0
    if d["chosen"] and d["chosen"].startswith("unload:") and d.get("verdict") == "Landed":
        r += load_of(before) - load_of(after)
    return r


def episodes(path, actions):
    """Each truck's decisions in each run, in tick order: inputs, visible actions, the action taken and its reward."""
    runs, measures, current = {}, [], None
    for line in open(path):
        row = json.loads(line)
        if row["type"] == "run":
            current = f"{row['arm']}/{row['seed']}"
            measures.append(row["measures"])
            continue
        d = row["decision"]
        if d.get("actionIndex") is None:
            continue
        visible = np.zeros(len(actions))
        visible[d["mask"]] = 1.0
        runs.setdefault((current, d["truck"]), []).append(
            (d["tick"], np.concatenate([np.asarray(d["features"], dtype=float), visible]), visible > 0, d["actionIndex"], reward(d)))
    return [sorted(e, key=lambda s: s[0]) for e in runs.values()], measures


def rollouts(network_path, seeds, out, temperature=None, sample_seed=0):
    args = ["dotnet", str(CONVOY), "--rollouts", str(out), "--seeds", f"{seeds[0]}-{seeds[1]}", "--network", str(network_path),
            "--sample-seed", str(sample_seed)]
    if temperature is not None:
        args += ["--temperature", str(temperature)]
    done = subprocess.run(args, capture_output=True, text=True, cwd=ROOT)
    if done.returncode != 0:
        raise SystemExit(f"rollouts failed: {done.stderr[-2000:]}")
    return done.stdout.strip()


def policy(layers, x, visible, temperature):
    scores, inputs = forward(layers, x)
    z = np.where(visible, scores / temperature, -1e9)
    z = z - z.max(axis=1, keepdims=True)
    e = np.exp(z) * visible
    p = e / e.sum(axis=1, keepdims=True)
    return p, inputs


def backward(layers, inputs, grad):
    """Gradients of every layer's weights and bias, given the gradient at the output."""
    grads = [None] * len(layers)
    for i in reversed(range(len(layers))):
        w, _ = layers[i]
        grads[i] = (grad.T @ inputs[i], grad.sum(axis=0))
        if i > 0:
            grad = (grad @ w) * (inputs[i] > 0)
    return grads


class Adam:
    def __init__(self, layers, rate):
        self.rate, self.step = rate, 0
        self.m = [(np.zeros_like(w), np.zeros_like(b)) for w, b in layers]
        self.v = [(np.zeros_like(w), np.zeros_like(b)) for w, b in layers]

    def apply(self, layers, grads):
        self.step += 1
        out = []
        for i, ((w, b), (gw, gb)) in enumerate(zip(layers, grads)):
            new = []
            for j, (p, g) in enumerate(((w, gw), (b, gb))):
                m, v = self.m[i][j], self.v[i][j]
                m[...] = 0.9 * m + 0.1 * g
                v[...] = 0.999 * v + 0.001 * g * g
                new.append(p - self.rate * (m / (1 - 0.9 ** self.step)) / (np.sqrt(v / (1 - 0.999 ** self.step)) + 1e-8))
            out.append(tuple(new))
        return out


def total_reward(path, actions):
    eps, measures = episodes(path, actions)
    return sum(s[4] for e in eps for s in e), measures


def summary(measures):
    return {k: sum(m[k] for m in measures) for k in ("delivered", "demand", "liesBelieved", "stranded", "wastedTrips", "ticksToServe")}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("start", type=pathlib.Path, help="the network trained by example")
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--work", type=pathlib.Path, default=ROOT / "datasets/trial")
    parser.add_argument("--rounds", type=int, default=150)
    parser.add_argument("--runs", type=int, default=64)
    parser.add_argument("--temperature", type=float, default=2.0)
    parser.add_argument("--select-every", type=int, default=10)
    parser.add_argument("--selection-seeds", type=int, nargs=2, default=[9000, 9199])
    parser.add_argument("--rate", type=float, default=3e-4)
    parser.add_argument("--value-rate", type=float, default=1e-3)
    parser.add_argument("--epochs", type=int, default=4)
    parser.add_argument("--batch", type=int, default=256)
    parser.add_argument("--clip", type=float, default=0.2)
    parser.add_argument("--entropy", type=float, default=0.01)
    parser.add_argument("--gamma", type=float, default=0.99)
    parser.add_argument("--lam", type=float, default=0.95)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    if not CONVOY.exists():
        raise SystemExit(f"build the convoy first: dotnet build samples/Convoy -c Release ({CONVOY} is missing)")
    args.work.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(args.seed)
    data, layers = read_network(args.start)
    actions = data["actions"]
    n_in = len(data["features"]) + len(actions)
    value = [(rng.normal(0, np.sqrt(2 / n_in), (64, n_in)), np.zeros(64)), (rng.normal(0, np.sqrt(2 / 64), (1, 64)), np.zeros(1))]
    policy_adam, value_adam = Adam(layers, args.rate), Adam(value, args.value_rate)
    current = args.work / "current.json"
    log = open(args.work / "progress.jsonl", "w")
    started = time.time()

    def select(round_, layers):
        write_network(current, data, layers, data.get("training", {}))
        out = args.work / "selection.jsonl"
        rollouts(current, args.selection_seeds, out)
        score, measures = total_reward(out, actions)
        return score, summary(measures)

    best_score, best_summary = select(0, layers)
    best_round, best_layers = 0, layers
    print(f"round 0 (the start): selection reward {best_score:.1f} {best_summary}", flush=True)
    log.write(json.dumps({"round": 0, "selection": best_score, **best_summary}) + "\n")

    for round_ in range(1, args.rounds + 1):
        write_network(current, data, layers, data.get("training", {}))
        first = 1000 + ((round_ - 1) * args.runs) % 8000
        out = args.work / "round.jsonl"
        line = rollouts(current, (first, first + args.runs - 1), out, args.temperature, sample_seed=round_)
        eps, _ = episodes(out, actions)

        x = np.concatenate([np.stack([s[1] for s in e]) for e in eps])
        visible = np.concatenate([np.stack([s[2] for s in e]) for e in eps])
        taken = np.concatenate([np.asarray([s[3] for s in e]) for e in eps])
        v = forward(value, x)[0][:, 0]
        advantages, returns, at = np.zeros(len(x)), np.zeros(len(x)), 0
        for e in eps:
            rewards = np.asarray([s[4] for s in e])
            vals = v[at:at + len(e)]
            gae = 0.0
            for t in reversed(range(len(e))):
                following = vals[t + 1] if t + 1 < len(e) else 0.0
                delta = rewards[t] + args.gamma * following - vals[t]
                gae = delta + args.gamma * args.lam * gae
                advantages[at + t] = gae
            returns[at:at + len(e)] = advantages[at:at + len(e)] + vals
            at += len(e)
        adv = (advantages - advantages.mean()) / (advantages.std() + 1e-8)
        old = np.log(policy(layers, x, visible, args.temperature)[0][np.arange(len(x)), taken] + 1e-12)

        clipped = 0
        for _ in range(args.epochs):
            order = rng.permutation(len(x))
            for start in range(0, len(x), args.batch):
                idx = order[start:start + args.batch]
                p, inputs = policy(layers, x[idx], visible[idx], args.temperature)
                logp = np.log(p[np.arange(len(idx)), taken[idx]] + 1e-12)
                ratio = np.exp(logp - old[idx])
                a = adv[idx]
                active = ((a >= 0) & (ratio < 1 + args.clip)) | ((a < 0) & (ratio > 1 - args.clip))
                clipped += int((~active).sum())
                g = np.where(active, -a * ratio, 0.0)
                onehot = np.zeros_like(p)
                onehot[np.arange(len(idx)), taken[idx]] = 1.0
                grad = g[:, None] * (onehot - p)
                logs = np.where(visible[idx], np.log(p + 1e-12), 0.0)
                entropy = -(p * logs).sum(axis=1, keepdims=True)
                grad += args.entropy * p * (logs + entropy)
                grad = grad / args.temperature / len(idx)
                layers = policy_adam.apply(layers, backward(layers, inputs, grad))

                vp, vin = forward(value, x[idx])
                value = value_adam.apply(value, backward(value, vin, (vp[:, 0] - returns[idx])[:, None] / len(idx)))
        layers = rounded(layers)

        entry = {"round": round_, "seeds": [first, first + args.runs - 1], "rollouts": line,
                 "reward": float(sum(s[4] for e in eps for s in e)), "decisions": len(x), "clipped": clipped}
        if round_ % args.select_every == 0:
            score, summ = select(round_, layers)
            entry.update({"selection": score, **summ})
            if score > best_score:
                best_score, best_summary, best_round, best_layers = score, summ, round_, layers
            print(f"round {round_}: {line}; selection reward {score:.1f} {summ} (best: round {best_round}), {time.time() - started:.0f} s", flush=True)
        log.write(json.dumps(entry) + "\n")
        log.flush()

    training = {
        "by": "trial",
        "method": "PPO",
        "startedFrom": data.get("training", {}),
        "reward": "+1 per unit delivered, -5 per action judged a lie, -20 when stranded, -0.02 per tick working",
        "rounds": args.rounds,
        "runsPerRound": args.runs,
        "temperature": args.temperature,
        "trialSeeds": [1000, 8999],
        "selectionSeeds": args.selection_seeds,
        "keptRound": best_round,
        "selectionReward": round(best_score, 2),
        "selection": best_summary,
        "randomSeed": args.seed,
    }
    write_network(args.output, data, best_layers, training)
    print(f"kept round {best_round} (selection reward {best_score:.1f}); wrote {args.output}, {time.time() - started:.0f} s")


if __name__ == "__main__":
    main()
