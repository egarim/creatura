"""Sums convoy battery reports from batches run in parallel (each its own seeds) into one line per arm.

Step 10 of docs/design-trained-models.md runs its paid arms as several batches at once, since a model seat takes
minutes a run. Each report's arm line gives delivered, wasted, lies, repeats, stranded and served-by (the average over
served runs); its calls table, when there is one, gives calls and decisions.

    python3 tools/battery_sum.py datasets/deepseek-50/test-*.txt
"""

import argparse
import json
import pathlib


def parse(text):
    arms, in_calls = {}, False
    for line in text.splitlines():
        parts = [p for p in line.split() if p != "%"]  # the share reads "40.5 %" or "40.5%", by the culture of the build
        if not parts:
            continue
        if parts[0] == "arm":
            in_calls = "seat-calls" in parts
            continue
        if len(parts) >= 8 and "/" in parts[1] and not in_calls:
            got, demand = (int(v) for v in parts[1].split("/"))
            identical = int(parts[7].split("/")[0])
            arms.setdefault(parts[0], {}).update(
                runs=int(parts[7].split("/")[1]), delivered=got, demand=demand, wasted=int(parts[2]), lies=int(parts[3]),
                repeats=int(parts[4]), stranded=int(parts[5]), servedBy=None if parts[6] == "NaN" else float(parts[6]), ledgers=identical)
        elif in_calls and len(parts) >= 6 and parts[1].isdigit():
            arms.setdefault(parts[0], {}).update(calls=int(parts[1]), decisions=int(parts[2]), surpriseCalls=int(parts[4]), disagreed=int(parts[5]))
    return arms


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("reports", type=pathlib.Path, nargs="+")
    args = parser.parse_args()
    total = {}
    for path in args.reports:
        text = path.read_text()
        for line in text.splitlines():
            if line.startswith("error:"):
                print(f"{path}: {line}")
        for arm, m in parse(text).items():
            t = total.setdefault(arm, {"batches": 0})
            t["batches"] += 1
            for key, value in m.items():
                if key == "servedBy":
                    continue
                t[key] = t.get(key, 0) + value
            # served-by is an average over the runs that served everything; weight it by those runs as far as the report allows
            if m.get("servedBy") is not None:
                t["servedWeighted"] = t.get("servedWeighted", 0) + m["servedBy"] * m["runs"]
    for arm, t in total.items():
        if "servedWeighted" in t:
            t["servedBy"] = round(t.pop("servedWeighted") / t["runs"], 2)
        print(arm, json.dumps(t))


if __name__ == "__main__":
    main()
