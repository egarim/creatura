# Design: turns, so two cells neither wait on each other nor both lead

**Status:** proposed 2026-10-31, from the cloud-session probe of the two-creatures experiment (egarim/two-creatures-a,
branch `spike`). Slice T0 built 2026-10-31 (branch `turns-t0`): C30 to C37 hold, each of the three deliberate breaks
fails a check, and the compatibility question is answered by test: optional fields on Progress, no new kinds, no hub
upgrade needed. Slice T1 built 2026-10-31 on the same branch: the polite and the simultaneous fake pairs ship 5 of 5
on a Docker host, and the X0 pairs score as before. The builder's choices and both results are recorded in "Built"
at the end. T2 is not built.

## The problem, in the owner's words

> I think before doing this we should fix the problem of politely waiting and tie breaking.

In the two-creatures experiment both creatures get the same goal and no roles (docs/design-two-creatures.md). Two
failures are likely before any code is written:

- **Polite waiting.** Each waits for the other to propose a plan. Nothing in the ledger says "I am waiting for
  you", so nothing notices that both are, and the run spends its time syncing.
- **Both lead.** Both propose at about the same moment, each sees only its own proposal, and each starts building
  its own plan. The ledger's order (timestamp, then cell, then sequence: `WorkLedger.Entries`) is the same in every
  home only once every home holds the same entries. A proposal that arrives late can become "first" after the other
  cell has already acted on its own.

Neither was seen yet: the first real pair (2026-10-31) had roles written into its prompts and finished in 17
minutes. This note is preventive. The probe recorded what a goal-only pair would face.

## The rule this note keeps

**Same goal, no roles.** Nothing here says who leads, who builds what, or how to split the work. The ledger only
makes waiting and proposing visible, and computes **whose move it is**, the same in every home. The plan is still
the agents' to invent, which is what the experiment measures.

## The parts

### 1. Waiting is an entry

`creatura cell wait --on <cell id or entry id> --row <why>` writes a Progress entry with a new optional field
`WaitingOn` on the current claim. A wait **stands until the same cell writes any later entry** that is not itself a
wait. There is no separate "stop waiting" entry.

### 2. Proposals and answers

- `creatura cell propose --topic <name> (--row <text> | --file <path>)`: a proposal on a named topic (`plan`, `api`,
  free text). With `--file`, the file is shared as an Artifact to the other parties first, and the proposal names it.
- `creatura cell answer --proposal <entry id> (--accept | --object --row <why>)`.
- Every proposal carries **`Seen`**: the proposal entry ids on the same topic that its cell held when it proposed,
  filled in by the CLI from the local ledger.

**Competing proposals** are proposals on one topic in which **neither saw the other**. Among competitors, **the one
whose entry hash (`SignedEntry.Hash()`) is lowest wins**. That is clock-free, so a cell can't win by backdating its
timestamp, and every home computes it the same way. A proposal that saw an earlier one is a **counter-proposal**, not
a competitor: it replaces the earlier one only once it is accepted.

**Settled:** the winning (or only) proposal on a topic is settled when every other party has answered `--accept`. If
a party objects, the proposal is not settled and the proposer has the move. If a party is silent past the quiet time
(below), `turn` tells the proposer it may proceed, and says that settling was by silence.

**Parties** are the cells with an active claim that takes an offer of the same goal (offers with equal goal text, or
one offer naming several cells). Without offers, they are the cells named with `--with <cell,...>` on `propose`. The
builder may choose a simpler definition if it covers both cases, and records the choice in this note.

*As built:* the parties of a cell are the cells with an active claim whose goal text equals its active claim's, and
the cells a proposal names with `--with`, by this cell or naming it. A claim that takes an offer carries the offer's
goal text, so both cases of the paragraph above are covered, and so is the experiment's plain `cell claim --goal`
with the same goal on both machines. The set is computed from the ledger at every `turn`, not fixed at `propose`:
a peer whose claim arrives after the proposal becomes a party when it arrives.

### 3. `creatura cell turn`

Read-only. It prints, for this cell, **act** or **wait** with one reason, from the verified ledger alone. With
`--json` it prints the same as an object (`{ "move": "act"|"wait", "reason": ..., "topics": [...] }`). Its exit code
is 0 for act and 3 for wait, so a script can loop on it. Rules, in order, the first that applies wins:

| # | situation | this cell gets |
|---|---|---|
| T1 | a proposal from another party is waiting for this cell's answer | **act**: answer it |
| T2 | this cell's proposal was objected to | **act**: revise or counter |
| T3 | two or more cells wait on each other (a cycle in the wait-for graph) | the cell whose wait entry is **latest** in ledger order (ties: higher entry hash) gets **act: break the deadlock**; the others get **wait** |
| T4 | the cell this one waits on has written nothing for longer than the quiet time | **act**: the peer is quiet |
| T5 | this cell waits on a peer that is moving | **wait**, with how long ago the peer last wrote |
| T6 | competing proposals on a topic | the losers get **act: answer the winner**; the winner gets **wait** until it's settled |
| T7 | otherwise | **act** |

**The quiet time** defaults to 10 minutes. The owner can set it in an offer (`--quiet <minutes>`), or a cell can pass
`--quiet` to `turn`. A cell's own clock decides "how long ago". That is the only place a local clock matters, and it
only changes when a cell may act, not who wins.

## Compatibility

`Offer` and `Withdraw` needed every hub and cell upgraded (CHANGELOG 0.2.0-preview.2). Prefer **optional fields on
existing kinds**: `WaitingOn` on Progress, and proposals and answers as Progress entries with `Topic`, `Seen`,
`Answers` and `Verdict`. Use them **if an un-upgraded hub still verifies and stores such entries**: test it against
the hub built from the commit before this change. If it doesn't, add entry kinds `Proposal` and `Answer`, and note in
the changelog that the hub must be upgraded first. Either way, a ledger with none of the new fields reads exactly as
before.

**Tested 2026-10-31, answer: the un-upgraded hub takes them.** The hub and the CLI were published from `e694f8b`, the
commit before this change, and the new CLI drove two homes through that hub: a wait, a proposal (with `topic` and
`seen`) and an accepting answer, all Progress entries. The old hub verified and stored all three (7 deltas: 2 hellos,
2 claims, 3 Progress with the new fields; no refusal in its log) and served them to the other home, where `turn` read
the proposal as settled. The old CLI read that home's ledger, the unknown fields ignored, and synced it with nothing
sent or refused. The reason: a signature covers the payload's canonical JSON whatever it holds, and a payload is
checked by deserializing it into its kind's record, which ignores fields it does not know. So the fields are optional
fields on Progress (`WaitingOn`; `Topic`, `Seen`, `With`, `Artifact`; `Answers`, `Verdict`), there are no new entry
kinds, and the hub on `world` need not be upgraded for T1. The experiment log has the commands (#66).

## Checks

| id | claim | how it is shown |
|---|---|---|
| C30 | **polite pair:** two cells that each wait on the other get exactly one act between them from `turn`, the same in both homes once synced | two in-process cells, both `wait --on` the other, sync, `turn` in each home |
| C31 | **wait before sync:** a cell that hasn't synced the other's wait still gets act (T7), and after syncing the cycle rule picks the same single mover | the same, with one sync delayed |
| C32 | **simultaneous proposals:** two proposals on one topic, neither seeing the other, pick the same winner (lowest hash) in both homes, and the loser gets act: answer the winner | two cells propose before syncing |
| C33 | **late arrival:** a proposal with an earlier (even backdated) timestamp that arrives after the other cell acted doesn't win by timestamp; the hash decides, and a proposal that saw the other is a counter, not a competitor | backdate one entry's clock in the test |
| C34 | **settled:** accept settles; object leaves the proposer with act; silence past the quiet time lets the proposer proceed and says so | answer, object, and a fake clock past the quiet time |
| C35 | **quiet peer:** waiting on a cell that has written nothing past the quiet time gives act | a fake clock |
| C36 | **determinism:** for random interleavings and sync orders of the same entries, `turn` gives the same result in every home once all hold the same entries | a property test over shuffled delivery orders |
| C37 | **nothing else changes:** the existing suite passes, an old ledger verifies and reads as before, and the hub stores the new entries (or, with new kinds, the changelog says to upgrade the hub first) | the full suite plus a hub test |

**Deliberate breaks** (as for C23–C29): make `turn` pick the mover by timestamp instead of hash, drop `Seen`, and
let a wait outlive the waiter's next entry. Each must fail at least one of C30–C36.

## What gets built first

| slice | what | passes when |
|---|---|---|
| T0 | the entries (or fields), `wait`, `propose`, `answer` and `turn` in `Creatura.Cells` and `Creatura.Cli`, with C30–C37 in `tests/Creatura.Cells.Tests` | `dotnet test` passes, and each deliberate break fails a check |
| T1 | the two-creatures harness: a **polite** and a **simultaneous** fake pair in `experiments/two-creatures/agents/fake.mjs`, each using `turn` | on a Docker host, both pairs ship 5 of 5 and the honest and liar pairs score as before |
| T2 | the probe's goal-only run with two Claude Code cloud sessions, `turn` in their loop | the owner's call |

## What this note does not cover

- More than two cells. The rules are written for any number of parties, but C30–C37 test two.
- Assigning roles, splitting work or judging a plan's quality. That stays the agents' job.
- Upgrading the hub on `world`. Deploying is the owner's call.

## Built (2026-10-31): slice T0

`TurnLedger` in `Creatura.Cells` (`Turns.cs`), the fields on `ProgressPayload`, and `creatura cell wait`, `propose`,
`answer` and `turn` (`--json`, `--quiet <minutes>`; exit 0 to act, 3 to wait); `offer` takes `--quiet <minutes>`,
signed into the offer only when set, so earlier offers verify as before. C30 to C37 are
`tests/Creatura.Cells.Tests/TurnTests.cs`, with one more test for the commands themselves (nine tests). The full
suite went from 771 to 780 tests, all passing.

**The three deliberate breaks**, each on its own and reverted: the mover picked by timestamp instead of hash fails
C32 and C33; `Seen` dropped fails C33 and C34; a wait that outlives the waiter's next entry fails C30, C35 and the
commands test.

**What the builder decided where the note left room, or read differently:**

- *A heartbeat is not a move.* A wait stands until the cell's next entry that is not a wait, as the note says, but a
  heartbeat does not end it and does not count as writing for the quiet time: a timer writes heartbeats, and a peer
  whose only entries are heartbeats is not moving the work.
- *The proposer of the only proposal waits too.* The table gives the winner of competing proposals "wait until it's
  settled" (T6) and says nothing of a lone proposal, whose proposer would fall to T7 and act, which is the "both lead"
  failure in another form. As built, T6 covers the live proposal of a topic whether it won a competition or stood
  alone: the proposer waits for the answers, proceeds when the unanswered parties are silent past the quiet time
  (saying it was by silence), and a party that objected waits while the proposer has the move, unless the proposer
  goes quiet. The losers of a competition get act: answer the winner, reported as T6; a party that was simply asked
  gets T1.
- *The live proposal and the plan.* On a topic, the proposals no other proposal has seen are the frontier; among them
  the lowest hash is live, the one the parties answer. A counter-proposal (one that saw the earlier one) takes the
  earlier one out of the frontier at once, so it is what the other parties answer now, but the plan, the latest
  proposal every party accepted, changes only when the counter is accepted, as the note says. `turn --json` reports
  both (`proposal` and `plan`).
- *Ties in the wait cycle.* Ledger order is total, so the rule's tie-break never fires; as built, the mover is the
  wait with the latest timestamp, ties by the higher hash, which is the note's rule word for word and the same in
  every home.
- *Waiting on an entry* waits on the cell that wrote it (an entry id names its cell). A cell cannot wait on itself;
  `turn` already waits for the answers to its proposals. A peer this ledger holds nothing from is quiet from the
  moment of the wait.
- *Topics are scoped to the parties:* `turn` reads proposals on a topic from this cell and its parties, so two pairs
  on different goals in one ledger can both have a `plan`. `Seen` is filled from every proposal on the topic the
  home holds, which can include a stranger's; that only makes a counter of what would have been one anyway.
- *`propose --file`* needs `--hub` (the file is uploaded as `share` does), seals the file for the parties when they
  are known and for every endorsed cell otherwise, and the proposal's row defaults to "proposal in <name>".

## Built (2026-10-31): slice T1

`experiments/two-creatures/agents/fake.mjs` takes `KIND`: `fake` and `liar` as in X0, with their roles written in, and
two pairs with no roles, `polite` and `simultaneous`, which the runner takes as `--agents polite,polite` and
`simultaneous,simultaneous` on an arm with the ledger (L or LM). Both claim the goal word for word (`cell claim
--goal`, the same text on both machines, which makes them parties). The polite pair then each `wait --on` the other
for a plan; the simultaneous pair each `propose --topic plan` at once, before any sync, so neither proposal has seen
the other. Both then loop on `cell sync` and `cell turn --json`: wait means sleep two seconds and look again; act with
a live proposal of the peer's waiting for an answer means read it (its file fetched from the hub when it names one)
and `answer --accept` (`--object` if it does not say who builds what); act with no proposal, the polite mover of T3,
means propose, as a file through `propose --file`. A settled topic ends the loop, and the plan's text says who builds
which half: the proposer takes the server. The halves are the reference app's, pushed to whichever repository the
plan says, since they find each other by service name. The runner records the waits, proposals and answers and whose
plan was accepted in `summary.json`.

On a Docker host (this session's, an x64 VM with 4 CPUs; `--check-every 60`), every run shipped 5 of 5:

| arm | agents | first ship | waits | proposals | answers | plan by |
|---|---|---|---|---|---|---|
| CC-L | polite, polite | 51 s | 3 | 1 | 1 | b |
| CC-L | simultaneous, simultaneous | 47 s | 1 | 2 | 1 | b |
| CC-LM | polite, polite | 51 s | 3 | 1 | 1 | b |
| CC-LM | simultaneous, simultaneous | 50 s | 1 | 2 | 1 | a |

The polite runs: both waited, `turn` gave the cell whose wait was the latest "act: break the deadlock" (T3) and the
other "wait"; the mover proposed, the other was told "act: answer" (T1), accepted, and both read the topic as settled
(T7). The simultaneous runs: each saw its own proposal first ("wait: waits for the peer to answer", T6), then both,
and the loser was told "your proposal lost to ... (the lower hash): answer it" (T6); the hash fell on b in three runs
and on a in the fourth. In every run the settled plan put the server in the proposer's repository, so three runs
built the halves the other way round from X0's written-in roles, and shipped the same.

The X0 pairs score as before: CC-L fake 5 of 5 (46 s), CC-M fake 5 of 5 (40 s), C-0 5 of 5 (40 s), the reference 5
of 5 (39 s); the liars' early "done" judged 0 of 5 and counted false (CC-L 1, CC-LM 2), their later true claim 5 of 5
(138 s and 139 s). Experiment log #67.

Found on the way: a hello in `cell ledger` names the creatura by its id, not by the creature's name, so the first
polite run waited ten minutes for "the peer's hello" and shipped nothing. A pair has two cells, so the peer is the
other hello; a run with more cells would need names, which the ledger does not carry.

**Left for T2:** `agents/goal.md` does not yet tell a real agent about `wait`, `propose`, `answer` and `turn`.
