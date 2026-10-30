"""What model calls cost, from call logs (`--calls`), at a model's listed prices per million tokens.

The price is the catalog's list price for prompt and completion tokens, without the cache discount, so the figure is
an upper bound on the bill. Step 10 of docs/design-trained-models.md reads its spend, and checks its cap, with this.

    python3 tools/spend.py datasets/deepseek-50/calls-*.jsonl [--input 0.28] [--output 0.42]
"""

import argparse
import json
import pathlib


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("logs", type=pathlib.Path, nargs="+")
    parser.add_argument("--input", type=float, default=0.28, help="dollars per million prompt tokens (deepseek-chat)")
    parser.add_argument("--output", type=float, default=0.42, help="dollars per million completion tokens (deepseek-chat)")
    args = parser.parse_args()

    total = {"calls": 0, "prompt": 0, "completion": 0, "cost": 0.0}
    for path in args.logs:
        calls = prompt = completion = 0
        for line in path.read_text().splitlines():
            row = json.loads(line)
            calls += 1
            prompt += row.get("PromptTokens") or 0
            completion += row.get("CompletionTokens") or 0
        cost = prompt * args.input / 1e6 + completion * args.output / 1e6
        print(f"{path}: {calls} calls, {prompt:,} prompt and {completion:,} completion tokens, ${cost:.4f}")
        for key, value in (("calls", calls), ("prompt", prompt), ("completion", completion), ("cost", cost)):
            total[key] += value
    print(f"total: {total['calls']} calls, {total['prompt']:,} prompt and {total['completion']:,} completion tokens, ${total['cost']:.4f}")


if __name__ == "__main__":
    main()
