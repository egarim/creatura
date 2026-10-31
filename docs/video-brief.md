# Video brief: two creatures build one app

For the session that makes joche's videos. Everything named here is in this repository on branch `turns-t0`;
nothing needs the laptop the runs happened on.

## What the video is about

Two AI agents (DeepSeek, `deepseek-chat`), each in its own Linux sandbox, are given the same one-paragraph goal
("a shared shopping list"), a git remote with push-to-deploy, and one or two channels to reach each other. They
cannot see each other's screen or files. A judge checks the deployed app every 60 s against 5 steps (page loads,
add an item, item appears, tick it, survives a redeploy). The question: can two agents that only share a goal split
the work and ship one app?

Two kinds of runs on 2026-10-31:

| Run | Roles | Channel | Shipped | First ship | Cost (pair) | Record |
|---|---|---|---|---|---|---|
| DD-L | no | ledger | 5/5 | 708 s | USD 0.68 | `experiments/two-creatures/records/2026-10-31/2026-10-31T11-53-33-DD-L` |
| DD-M | no | board | 5/5 | 300 s | USD 0.50 | `.../2026-10-31T12-19-38-DD-M` |
| DD-LM | no | both | 5/5 | 510 s | USD 0.54 | `.../2026-10-31T12-40-20-DD-LM` |
| DD-L-R | yes | ledger | 5/5 | 798 s | USD 0.81 | `.../2026-10-31T13-06-02-DD-L-R` |
| DD-M-R | yes | board | 0/5 | never | USD 0.50 | `.../2026-10-31T13-21-03-DD-M-R` (b killed its own driver) |
| DD-M-R | yes | board | 5/5 | 1522 s | USD 1.15 | `.../2026-10-31T13-53-10-DD-M-R` |
| DD-LM-R | yes | both | 5/5 | 488 s | USD 0.33 | `.../2026-10-31T14-38-20-DD-LM-R` (hero run, has both repos) |

"Roles": creature-a was told to build only the server (`api`, no published port), creature-b only the web UI
(port 8080, calling `api`). Without roles both were told the whole goal and left to sort it out.

Lessons, in the order the video should land them:

1. Same goal, no roles: they race. Whoever deploys first owns port 8080; the other ends up testing. Only the ledger
   produced an explicit agreement.
2. Roles force a contract. With a split, every channel produced an API contract before code was written, and no
   agent ever claimed something false.
3. Contracts drift. In DD-L-R, a's contract said port 8080 but its server listened on 8000; b's proxy failed; one
   ledger entry from a and a one-line fix from b settled it.
4. Agents can hurt themselves. In the first DD-M-R, b cleaned up its test servers with `pkill -x node` and killed
   the process that was driving it. Fix: the driver now runs under its own process name.
5. The best run was the one with both channels and roles: contract agreed in the first minutes, server live at
   8 min, app complete at 16 min, USD 0.33 for the pair.

## Material in the repository

- `experiments/two-creatures/records/2026-10-31/<run>/`
  - `summary.json`: score, first ship, claims (who, via which channel, at what second, true or false), turns, usage.
  - `agent-a-transcript.jsonl`, `agent-b-transcript.jsonl`: every step of each agent (model text, the command it
    ran, the output it saw), with timestamps. This is the "console recording": replay it as a terminal.
  - `agent-a.log`, `agent-b.log`: the same, plain text.
  - `ledger-a.json`, `ledger-b.json` (L and LM runs): the ledger cells, proposals, answers and claims.
  - `messages.jsonl` (M and LM runs): the board, one line per message.
  - `checks.jsonl`: the judge's 5-step check every 60 s. Good for a progress bar across the timeline.
  - `deploy-logs/`: what the world's deployer did with each push.
  - `repo-a.bundle`, `repo-b.bundle` (DD-LM-R only): the two repositories as git bundles.
- `experiments/two-creatures/demo/`: the DD-LM-R app ready to run (`docker compose up --build`, then
  http://localhost:8080). `repo-a` is what creature-a wrote, `repo-b` what creature-b wrote, untouched.
- `experiments/two-creatures/agents/goal.md`: the exact words both agents were given.
- `docs/design-turns.md`, sections "Moved to DeepSeek", "Outcome", "With roles": the written analysis.
- `docs/experiment-log.md`, entries #71 to #77: one paragraph per run with the numbers.

There is no screen recording. All visuals are rebuilt from the files above plus a screen capture of the demo app.

## Short video (60 to 90 s, vertical or square)

One run, DD-LM-R. Title: "Two AIs, one app, they never met."

1. 0 to 8 s. Two terminals side by side, labelled creature-a and creature-b. The goal paragraph types in on both.
2. 8 to 25 s. A split timeline across the bottom (0 to 16 min). The board messages appear as chat bubbles between
   the two terminals, in real order, each stamped with its minute: a's hello, b's "I build the UI, you the API?",
   the two contract proposals, b's "accepting yours".
3. 25 to 45 s. a's terminal scrolls its commits and the push; the deployer log says repo-a running. The judge bar
   stays red (no page yet). b's terminal: mock API, page, proxy to `api`, push. Judge bar goes 2/5, then 5/5 at
   minute 8.
4. 45 to 60 s. Both DONE claims as bubbles. Caption: "5/5, 16 minutes, 33 cents."
5. 60 to 80 s. Real screen capture: the app on two phones or two browser windows, an item added on one appears on
   the other, ticked on one, struck through on the other.
6. End card: "7 runs, 1 failure, every roles run agreed on an API first." Repo name.

## Long video (8 to 12 min, landscape, narrated)

1. Setup (1.5 min). Diagram: world (git remote, deployer, judge), two sandboxes, the two channels. What a ledger
   turn is (claim, propose, answer, wait) and what the board is (say, listen). The goal text on screen.
2. The race (2 min). DD-M without roles. Show both agents writing the same app; the first push wins 8080; the
   other agent's transcript where it decides to test instead. The false claims on the board and why the judge
   counts every message as a claim.
3. Roles (2 min). The new goal paragraphs. DD-L-R: both contract files side by side, the hash tie-break, b
   adapting its UI. Then the port drift and the one ledger entry that fixed it.
4. The failure (1 min). First DD-M-R: b's `pkill` step, the exit 143, a waiting 15 min with keepalive pushes.
   What changed in the harness.
5. The hero run (2 min). DD-LM-R replayed: board plus ledger, the timeline from the short video but slower, with
   the judge's checks every minute as a bar.
6. The app (1.5 min). Screen capture, then the two READMEs the agents wrote, then `docker compose up` on a clean
   machine.
7. Numbers and what is next (1 min). The table above as a chart: first ship and cost per run. What roles cost
   (UI waits on the server) and what they bought (contracts, zero false claims). Next: three creatures, a
   non-trivial app, a human in the loop.

Narration: write it from `docs/design-turns.md` and the log entries; keep the agents' own words for the bubbles,
quoted exactly from `messages.jsonl` and the ledgers. Voice: any; joche has ElevenLabs on the Air if a generated
voice is wanted.

## Rendering notes

- Transcript replay: each `agent-*-transcript.jsonl` line has a step with the model's text and the tool call;
  render the command in a terminal font, then the first lines of its output, 2 to 4 s per step, skipping the
  long waits (collapse any gap over 60 s into a "waiting 4 min" caption).
- Timestamps in `summary.json` claims and `checks.jsonl` are seconds since the run started.
- Colours: give creature-a and creature-b two fixed colours and keep them for terminals, bubbles and commits.
- The demo app needs only Docker; run it with `docker compose up --build` inside `experiments/two-creatures/demo`
  and capture at http://localhost:8080 in two windows.
