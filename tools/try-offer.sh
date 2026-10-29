#!/usr/bin/env bash
# Plays a goal offer end to end with headless Claude Code sessions (docs/design-activity-stream.md):
#   1. a new session starts: what its SessionStart hook showed the person
#   2. that session's model is asked whether it knows of any offer: it should not
#   3. a second session takes the offer by naming it in its first prompt, and does the goal with read-only tools
#   4. where the offer stands in the ledger, and the taking claim's path
# Each session uses a little model quota and writes its own claim, as every Claude Code session here does.
#
#   tools/try-offer.sh of-bb5eddfb10 [folder]        (creatura cell offers --creatura claude-code lists the waiting ones)
set -euo pipefail

offer="${1:?which offer? e.g. of-bb5eddfb10 (creatura cell offers --creatura claude-code lists them)}"
folder="${2:-$HOME/Projects/creatura}"
creatura="${CREATURA_BIN:-$HOME/.creatura/bin/creatura}"
cd "$folder"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

goal="$("$creatura" cell offers --creatura claude-code | awk -v id="$offer" '$1 == id { $1=""; $2=""; $3=""; sub(/^ +/, ""); print }')"
if [ -z "$goal" ]; then
  echo "$offer is not waiting for this laptop's cell; these are:" >&2
  "$creatura" cell offers --creatura claude-code >&2
  exit 1
fi

# Prints what the SessionStart hook said (the message for the person) and the model's final reply, from stream-json.
read_session() {
  python3 - "$1" <<'EOF'
import json, sys
hook, reply = None, None
for line in open(sys.argv[1]):
    try:
        o = json.loads(line)
    except ValueError:
        continue
    if o.get("type") == "system" and o.get("subtype") == "hook_response" and o.get("hook_event") == "SessionStart":
        out = (o.get("output") or "").strip()
        try:
            hook = json.loads(out).get("systemMessage") if out else None
        except ValueError:
            hook = "(not JSON, so Claude Code would add it to the model's context:) " + out
    if o.get("type") == "result":
        reply = o.get("result")
print("   the hook showed the person:")
print("     " + (hook or "(nothing)").replace("\n", "\n     "))
print("   the model replied:")
print("     " + (reply or "(no reply)").replace("\n", "\n     "))
EOF
}

echo "== 1 and 2. A new session starts; then its model is asked whether it knows of an offer"
claude -p "Answer only from what is already in your context, without using any tools: has anyone offered you a goal, or mentioned an offer with an id that starts with 'of-'? Reply 'no', or quote what you saw." \
  --output-format stream-json --verbose --max-turns 1 < /dev/null > "$work/first.jsonl"
read_session "$work/first.jsonl"

echo
echo "== 3. A second session takes $offer by naming it, and does it: $goal"
claude -p "offer $offer. The goal: $goal. Use only read-only commands (git log, git show, git diff, reading files). Answer in at most 12 lines." \
  --allowedTools "Bash(git log:*)" "Bash(git show:*)" "Bash(git diff:*)" "Bash(git status:*)" "Read" "Grep" "Glob" \
  --output-format stream-json --verbose --max-turns 15 < /dev/null > "$work/second.jsonl"
read_session "$work/second.jsonl"

echo
echo "== 4. Where the offer stands"
line="$("$creatura" cell offers --creatura claude-code --all | grep "^$offer " || true)"
echo "   $line"
claim="$(printf '%s' "$line" | sed -n 's/.*(claim \([^)]*\)).*/\1/p' | tr -d ' ')"
if [ -n "$claim" ]; then
  echo "   the claim that took it:"
  "$creatura" cell ledger --creatura claude-code --path "$claim" | sed 's/^/     /'
fi
echo
echo "The stream shows it too: creatura cell stream --creatura claude-code"
