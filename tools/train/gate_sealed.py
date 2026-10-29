# /// script
# requires-python = ">=3.12,<3.14"
# dependencies = ["numpy>=2"]
# ///
"""Gate T11b of docs/design-trained-models.md (step 6's second attempt), on training seeds only.

On the held-out seeds (ending in 9) of the last round's driving, the lie judge at t1 must make fewer errors (false
alarms plus misses, against the truth) than the fused check with memory makes on the same actions, computed from the
recorded readings.

    uv run tools/train/gate_sealed.py models/convoy-judge-sealed.json datasets/sealed/r2-p2-world.jsonl datasets/sealed/r2-p2.jsonl
"""

import json
import pathlib
import sys

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import train_landed as tl  # noqa: E402


def main():
    model_path, world_path, runs_path = sys.argv[1:4]
    tl.ROADS.update(json.loads(pathlib.Path("datasets/noisy/roads.json").read_text()))
    card = json.loads(pathlib.Path("datasets/convoy-j/card.json").read_text())
    model = json.loads(pathlib.Path(model_path).read_text())
    layers = [(np.asarray(l["weights"]), np.asarray(l["bias"])) for l in model["layers"]]
    rows, x, y = tl.load([world_path], len(card["actions"]), memory=True)
    held = np.asarray([r["seed"] % 10 == 9 for r in rows])
    rows = [r for r, h in zip(rows, held) if h]
    x, y = x[held], y[held]
    t1 = model["training"]["thresholds"]["t1"]
    ours = tl.score(layers, x) > t1
    check = tl.fused(runs_path, memory=True)
    theirs = np.asarray([check.get((r["run"], r["truck"], r["tick"], r["actionId"]), r["truth"] == "Lied")
                         if r["actionId"].split(":")[0] in ("drive", "refuel") else r["truth"] == "Lied" for r in rows])
    lied, landed = y == 1, y == 0
    result = {"actions": len(rows), "lies": int(lied.sum()), "t1": t1,
              "judgeFalseAlarms": int((ours & landed).sum()), "judgeMisses": int((~ours & lied).sum()),
              "fusedFalseAlarms": int((theirs & landed).sum()), "fusedMisses": int((~theirs & lied).sum())}
    result["judgeErrors"] = result["judgeFalseAlarms"] + result["judgeMisses"]
    result["fusedErrors"] = result["fusedFalseAlarms"] + result["fusedMisses"]
    result["held"] = result["judgeErrors"] < result["fusedErrors"]
    print(json.dumps(result, indent=1))


if __name__ == "__main__":
    main()
