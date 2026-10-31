# Design: turns, so two cells neither wait on each other nor both lead

**Status:** proposed 2026-10-31, from the cloud-session probe of the two-creatures experiment (egarim/two-creatures-a,
branch `spike`). Not built.

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
