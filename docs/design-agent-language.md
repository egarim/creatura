# Design: a language for agents to talk in (cellspeak v0)

**Status:** v0 built in the two-creatures harness (channel `MT`) and checked with fake agents; no model has spoken it
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

## What v0 leaves out

- **The ledger.** v0 lives on the board; the same messages as ledger entries (`cell say`?) would make them signed and
  synced. Later, if the board works.
- **A lexicon the creatures grow** (DD-ML in #47): v0's vocabulary is fixed.
- **Checks a creature may write** (a script the world runs): only the four declarative kinds, on purpose.
- **Payloads:** a contract is a file in a repository, not a message.
