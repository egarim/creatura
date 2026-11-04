# Design: Latin, a language for agents to talk in

**The name: Latin 1** (the owner, 2026-11-04; first called cellspeak), the new version of Latin. For a thousand years
scholars with different mother tongues wrote to each other in Latin and each read it in their own, and Latin stopped
changing, so a text means today what it meant when it was written. Agents with one fixed language between them, which
each owner reads in their own language, are the same idea. The name stays Latin 1; its revisions are counted apart,
with one number in the file, the code and the text: r0 is `lang/v0.mjs`, `latin1/r0` (log #95, #96, whose records say
`cellspeak/0`, its name when they ran); r1 is `lang/v1.mjs`, `latin1/r1`, with the message tool (log #97, #98); the next
is r2, `lang/v2.mjs`, `latin1/r2`.

**Status:** Latin 1 r0 and Latin 1 r1 built in the two-creatures harness (channel `MT`, now on Latin 1 r1 with its message
tool). Paid runs: Latin 1 r0 shipped (log #96); Latin 1 r1 did not, 3/5 with two false claims whose checks the creatures
chose (log #98), which points to v2: a claim that the app is done carries the goal's own test. Written 2026-11-04
from issue #47, "Should agents talk to each other in human language?".
yet. Written 2026-11-04 from issue #47, "Should agents talk to each other in human language?".

## The question

Our creatures talk to each other in human language, the language of the prompt that started them. Human language is
how they talk to *us*. Between themselves, a language that keeps only what the other side can use might be shorter,
faster and harder to lie in. Two things must not be lost on the way: **the owner can audit every message**, and **the
person in the loop is not drowned**: agents write far more than a person can read, and that is where psychophysics
comes in (issue #47 and the article "Should Agents Talk to Each Other in Human Language?").

## The idea in one line

A small typed vocabulary in which **every message either moves a decision or carries something the world can check**.

| psychophysics | what it becomes for an agent | in v0 |
|---|---|---|
| threshold (the just-noticeable difference) | a message that changes nothing the receiver does is noise | a message the same as the sender's last one is refused; `change` must change something |
| Weber's law | the same change matters more near a decision | `change`, `block` and `need` are their own types, never buried in progress talk |
| masking | a strong signal hides weak ones around it | in the digest, a `done` hides the `block`s and `need`s it resolved; claims and notes are counted, not shown |
| perceptual coding (MP3, JPEG) | keep what the receiver perceives, drop the rest | ten types, no free prose except a 120-character `note`, which is counted |
| Frank Krueger: the code is the best specification | the most compact shared language is executable | a `done` and an `offer` carry checks the world runs before the message lands |

## The language

One message per line; the board takes one per `say`. The module is `experiments/two-creatures/lang/v0.mjs`.

```
claim   <task> [until <n>m]                       what I am working on
propose <topic> <key>=<value>...                  a plan, a split of the work, an API choice
accept  #<n>                                      I agree to proposal #n
object  #<n> because <text>                       I don't, and why (120 characters)
offer   <name> at <repo>:<path>                   a contract: a file in my own repository   the world checks the file exists
done    <task> check <check> [and <check>]...     finished, with the proof                  the world runs every check
need    <thing> from <a|b> [because <check>]      something I need from my peer             the check must still fail
block   <task> on <thing>                         I cannot go on until <thing>
change  <field> <old> -> <new>                    a field of a plan or contract changes
note    <text>                                    anything else, 120 characters at most     counted, never translated

<check>: deployed <repo> | file <repo>:<path> | http <url> <status> | array <url>
```

**The checks are declarative,** never a command: the world (the board on `world`) reads the deployer's status, its
own git repositories and the published app. Nothing a creature writes runs on `world`. URLs are limited to
`world:8080` and `world:7001`, paths to plain relative paths.

**The board's rules** (`admit`): an `accept` or `object` names an existing proposal by the *other* creature; an
`offer` comes from the sender's own repository; a `need` is from the peer; nothing repeats the sender's last message.
A refused message gets the reason (and, when it did not parse, the grammar), never reaches the peer, and is kept in
`refused.jsonl` for the record.

## Auditable by construction

- **A1, round trip.** Every message has one canonical text, and parsing it gives the same message back.
- **A2, decoding.** Every message decodes, deterministically, into a sentence in the owner's language: the language
  the owner reads, by default the language of the original prompt. v0 has English, Spanish and Russian. Two
  different messages never decode to the same sentence.
- **A3, prose is marked.** A `note` is shown as prose the decoder did not translate, and counted in the summary, so a
  creature drifting back into prose shows.
- **A4, the record keeps both.** `messages.<lang>.md` in the run's record has each message decoded, with the original
  under it; `refused.jsonl` has what the world refused and why.

## The person in the loop

The digest (`digest()`, `digest.<lang>.md` in the record) is the state the messages leave, not the messages: what was
agreed, what is done and checked, what is blocked or needed, what changed, and **first, the claims the world
refused**, the one thing a person must not miss. At most 8 lines, whatever the number of messages, in the owner's
language, with the rest one request away.

- **H1, state, not talk:** the digest shows plans, dones, open blocks and needs, changes.
- **H2, masking:** a block or need resolved by a later `done` is not shown.
- **H3, a bound:** 8 lines for 8 messages or 200.
- **H4, the owner's language:** the digest reads in every decoder's language.

The console's own digest (the Run view and the web page) is the next step; v0 keeps it in the run's record.

## The arm: `MT`, typed messages

`run.mjs --arm FF-MT --agents typed,typed` (or `typed,typed-liar`, or `deepseek,deepseek` once the owner says go).
`--lang en|es|ru` picks the owner's language for the decoded log and the digest (default English, the goal's
language). The goal (`agents/goal.md`) gains the channel text for `MT`, with the grammar; the other arms' words do not
change. The summary gains `typed`: the refusals by reason (language, world, repeat), every refused claim with the
judge's score at that moment, the number of notes and the digest; and `talk`, the characters on the board, so `MT`
compares with `M`.

**Which dones the judge scores.** Only a `done` whose checks look at the published app (`http://world:8080`) claims the
app is done (`claimsApp`); the goal for `MT` asks for exactly that. A done of one part, such as `done server check
deployed repo-a`, is settled by its own checks and is not the judge's to score: the first liar run scored it 0 of 5,
since the UI was not up yet, and counted it as a false claim, which it was not.

**What a refused claim tells us.** Each refused `done` is judged too (at most one per creature every two minutes). A
refused done the judge scores 5 of 5 would mean the language is too strict; a landed done the judge scores below 5
would mean its checks are too weak (the checks are a creature's choice: `done app check deployed repo-b` lands
while the app is broken). The pre-registered checks of #47 are about exactly that.

## Pre-registered checks for the paid runs (from #47)

- **L3, a checked claim is never false:** in `MT`, every landed `done app` is judged 5 of 5. Falsifier: one below.
  v0 lets a creature choose weak checks, so this can fail; that would say the goal must name the checks.
- **L6, shorter:** `MT` puts fewer characters on the board per shipped run than `M`. Falsifier: as many or more.
- **L7, no worse:** `MT` ships at least as often as `M`, with the same score. Falsifier: fewer ships.
- **A5, little prose:** notes are under a fifth of the landed messages. Falsifier: more.

## The first paid runs (2026-11-04, log #96)

One DD-MT pair and one fresh DD-M pair, the same day, goal and model (deepseek-chat), no roles. One run each, so
these are anecdotes:

| | DD-MT, Latin 1 r0 | DD-M, prose |
|---|---|---|
| shipped | 5/5 | 5/5 |
| first seen working | 322 s | 615 s |
| characters on the board | 932 | 6,351 |
| messages | 13 landed, 4 refused (all grammar) | 9 |
| false claims | 0 | 0 |
| cost, both creatures | USD 0.24 | USD 0.16 |

L3, L6 and L7 held; **A5 failed**: 5 of 13 messages were notes (38%). The notes carried what v0 cannot say: a
creature checking its peer's work ("2nd client sees new item in 51ms") and asking for something ("please restart
container to test durability"). Every refusal was grammar (a task name of several words, a `need` without `from`), and
each was corrected at once. Nobody answered a proposal with `accept`. a's `done` checked an app b had built: the checks
prove the world is in the claimed state, not who brought it there. Shorter talk did not make the run cheaper: the
models' own shell work and thinking cost more than the board saved.

**For v1:** quoted task names, a `verified` type for checking the peer's work, an `ask` type for requests, and a
`done`'s checks bound to the sender's own repository.

## Revision 1 (r1): the missing words, and a tool to speak with (2026-11-04)

`lang/v1.mjs`; v0 stays as it was, so the runs of log #95 and #96 decode as they did. The arm `MT` now runs v1.

**The words log #96 wrote as prose:**

| v1 | what the models wrote in a note on v0 |
|---|---|
| `verified <task> of <a\|b> check <check>...`: I checked my peer's work; the world runs the checks | "a verified b app: 8080 ok, api ok, 2nd client sees new item in 51ms" |
| `ask <action> of <a\|b> [because <text>]`: please do this; answered by the peer's later `done` or `verified` | "please restart container to test durability" |
| names of several words, in double quotes: `claim "shared shopping list"` | "claim shared shopping list end-to-end on repo-b until 40m" (refused) |
| a `done` must carry a check on the sender's own repository; the peer's work is reported with `verified` | a's done listed an app that b had built |

**The tool.** A creature need not write the text at all. The `message` tool (`TOOL`, an OpenAI-style function schema)
takes the message as typed fields: `{"type": "done", "task": "app", "checks": [{"kind": "deployed", "repo":
"repo-b"}]}`. `encode()` writes its one canonical text and parses it again before it is sent. So:

- **The same fields always give the same text,** and that text always decodes to the same sentence in each of the
  owner's languages. What was sent, what the peer reads and what the owner reads are three views of one value.
- **A grammar mistake cannot reach the board:** a bad field is refused before sending, with the grammar's own reason.
  The only free text left is a note's words and a reason's words, each at most 120 characters.
- **The record says how each message came,** `via: "tool"` or `"say"`. `summary.json` counts them (`typed.viaTool`).

A model in an `MT` arm gets `message` beside `shell` and `stop`, with the goal's channel text rewritten for it.
`speak '<fields as JSON>'` is the same tool as a shell command on each creature, used by the fake pairs. `say` still
takes the text, so a creature can still try prose, and the liar does.

**Checked with the fake agents (log #97):** the honest pair (owner in Spanish) and the liar pair (in English) both
shipped 5/5, with all 11 messages through the tool and no grammar refusal. The ask was answered by a done, so it left
the digest. The liar's `done` before anything was published was refused and judged 0/5.

**The cost of log #96 is not the language's.** The typed pair cost USD 0.24 against 0.16, but the board saved about
1,500 tokens of a run that sent 835,000. A model re-sends its whole conversation on every call, so the cost is the
number of calls and the goal's length. The typed goal was 1,400 characters longer, and agent a made 43 calls
against 34. Latin saves what the peer and the owner must read. A cheaper run needs fewer calls, for example a
`listen` that waits for news instead of the models' polling.

## Revision 2 (r2): a claim that the app works carries the goal's own test (2026-11-04)

`lang/v2.mjs`, `latin1/r2`; the arm `MT` now runs it. From log #98: both creatures said the app was done with checks
the world agreed with (deployed, the page answers 200, `/api/items` is a list), while ticking an item did not save, 3
of 5. A creature that picks its own proof picks proof that is easy to pass.

- **`check goal`, the goal's own test.** These are the judge's five steps: two browsers, an item added, seen live,
  ticked, and kept through a restart. The world runs them before the message lands, so a `done` carrying `goal` lands
  only on a 5 of 5. It takes about half a minute.
- **A `done` that looks at the published app must carry `goal`.** A done of one part (`done server check deployed
  repo-a`) needs none.
- **The sender's own work is its running deploy:** a `done` must include `deployed repo-<me>`. A file in the repository
  no longer counts (log #98: b's done carried "file repo-b:compose.yaml", true and beside the point).

**How the world runs a test it cannot run itself.** The judge needs a browser and the creatura CLI, which live beside the
runner on the host, not on `world`. So the board leaves a request in `/srv/board/goal/` and waits up to five minutes.
The runner, which looks at world every two seconds, runs the judge and writes the score back with the first step that
failed. The verdict is also in `checks.jsonl` as "goal test for <who>", and in `summary.json` as `typed.goalTests`. A
message waiting for its test is held outside the board's queue, so the peer's messages are not stuck behind it; the
board's rules are asked again when it lands.

**The scripted agents:** `typed-sloppy` ships log #98's bug (the reference UI's `BREAK=check`) and says the app is done
with `goal`, which must be refused. It then fixes the bug and says done again. The honest pair's `done app` and its
answer to the restart ask carry `goal` too.

**What it does not cover.** A part's done (the server alone) is still settled by the checks its sender picks. The goal's
test proves the app works, not who made it work: two creatures on one app both pass it. That is what `verified` and the
`deployed` rule are for.

## What v0 leaves out

- **The ledger.** v0 lives on the board; the same messages as ledger entries (`cell say`?) would make them signed and
  synced. Later, if the board works.
- **A lexicon the creatures grow** (DD-ML in #47): v0's vocabulary is fixed.
- **Checks a creature may write** (a script the world runs): only the four declarative kinds, on purpose.
- **Payloads:** a contract is a file in a repository, not a message.
