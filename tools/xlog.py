"""The experiment log: every run that shaped a result, kept with what it was for.

Pre-registration protects the tests written down before the code. Everything else (a training setting tried and
dropped, a probe run, a trace) shapes the result too, so it is logged here, failures and exploration included, with
the commit it ran on and the seeds it touched. Any run on the battery's seeds (0 to 49) says so, so how often they
were looked at can be counted.

    python3 tools/xlog.py add --step 6b --kind tune --seeds 50-599 --title "..." --cmd "..." --result "..." [--used "..."]
    python3 tools/xlog.py render          # writes docs/experiment-log.md from experiments/log.jsonl

Kinds: test (a registered hypothesis or gate), tune (a setting tried on training seeds), probe (a look to understand,
not a test), data (recording or export), check (a determinism or no-change check), build (a deliberate-break run).
"""

import argparse
import datetime
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parent.parent
LOG = ROOT / "experiments" / "log.jsonl"
PAGE = ROOT / "docs" / "experiment-log.md"
KINDS = ("test", "tune", "probe", "data", "check", "build")


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True).stdout.strip()


def battery(seeds):
    """Whether a seed range like '0-49' or '50-599' touches the battery's seeds 0 to 49."""
    if not seeds:
        return False
    for part in seeds.split(","):
        low = int(part.split("-")[0])
        if low < 50:
            return True
    return False


def add(args):
    entries = [json.loads(line) for line in LOG.read_text().splitlines()] if LOG.exists() else []
    entry = {
        "id": len(entries) + 1,
        "when": args.when or datetime.datetime.now().astimezone().isoformat(timespec="minutes"),
        "commit": args.commit or git("rev-parse", "--short", "HEAD"),
        "dirty": bool(git("status", "--porcelain", "--untracked-files=no")) if not args.commit else args.dirty,
        "step": args.step, "kind": args.kind, "title": args.title, "seeds": args.seeds,
        "battery": battery(args.seeds), "cmd": args.cmd, "result": args.result, "used": args.used,
    }
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open("a") as out:
        out.write(json.dumps(entry, ensure_ascii=False) + "\n")
    print(f"logged #{entry['id']}: {entry['title']}")
    render(None)


def render(_):
    entries = [json.loads(line) for line in LOG.read_text().splitlines()] if LOG.exists() else []
    lines = ["# Experiment log", "",
             "Every run that shaped a result, failures and exploration included, written by `tools/xlog.py` from",
             "`experiments/log.jsonl`. Pre-registered hypotheses live in docs/design-trained-models.md; this log is",
             "everything around them: what was tried, on which seeds, on which commit, and what it was used for.",
             "A commit marked * ran with uncommitted changes on top of it. The log starts with step 6; earlier steps'",
             "runs are recorded only in the design note.", ""]
    steps = {}
    for e in entries:
        steps.setdefault(e["step"], []).append(e)
    for step, group in steps.items():
        looks = sum(1 for e in group if e["battery"])
        lines += [f"## Step {step}", "", f"{len(group)} entries; {looks} touched the battery's seeds 0 to 49.", "",
                  "| # | when | commit | kind | seeds | what | result | used for |", "|---|---|---|---|---|---|---|---|"]
        for e in group:
            seeds = (e["seeds"] or "—") + (" **battery**" if e["battery"] else "")
            cell = lambda s: (s or "").replace("|", "\\|").replace("\n", " ")
            lines.append(f"| {e['id']} | {e['when'][:16].replace('T', ' ')} | `{e['commit']}`{'*' if e['dirty'] else ''} | {e['kind']} | {seeds} "
                         f"| {cell(e['title'])} | {cell(e['result'])} | {cell(e['used'])} |")
        lines.append("")
        lines += ["<details><summary>Commands</summary>", ""]
        lines += [f"- #{e['id']}: `{cell(e['cmd'])}`" for e in group if e["cmd"]]
        lines += ["", "</details>", ""]
    PAGE.write_text("\n".join(lines))
    print(f"wrote {PAGE.relative_to(ROOT)} ({len(entries)} entries)")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    a = sub.add_parser("add")
    a.add_argument("--step", required=True)
    a.add_argument("--kind", required=True, choices=KINDS)
    a.add_argument("--title", required=True)
    a.add_argument("--seeds", default="")
    a.add_argument("--cmd", default="")
    a.add_argument("--result", default="")
    a.add_argument("--used", default="")
    a.add_argument("--when", help="for entries written after the fact")
    a.add_argument("--commit", help="for entries written after the fact: the commit the run was on")
    a.add_argument("--dirty", action="store_true", help="with --commit: the run had uncommitted changes on top of it")
    a.set_defaults(func=add)
    r = sub.add_parser("render")
    r.set_defaults(func=render)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
