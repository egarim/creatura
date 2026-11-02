# Design: handing work from one session to the next

**Status:** proposed and built 2026-11-02 (slice H1, below); H2, Codex sessions as cells, waits for the owner's yes.

## The problem, in the owner's words

The owner runs many Claude Code sessions, in the cloud and on his machines, and hands work between them by hand: a
session that cannot go on writes a note, the owner pastes it into a new session, and the new session starts from the
note. That is how the two-creatures runs of 2026-10-31 moved from a cloud session to a local one. The sessions are
already cells ([design-cells.md](design-cells.md), "How host tools join"): a session's first prompt writes a `Claim`,
each turn a `Progress`, and its end a `HandOff` that says only "session ended (prompt_input_exit) after 14 turns".
Nothing in that hand-off is what the next session needs, and the next session claims afresh ("creatura on main"), so
the ledger has the two sessions as two unrelated claims and the note travels outside it. The owner's direction
(2026-11-02): the ledger should be the hand-off, and `turn` should say whose move it is between sessions as it does
between creatures.

## What a hand-off needs

Three things, each one field or one rule:

1. **The notes.** A `HandOff` may carry `Notes`: what the next session needs, in the session's own words, at most
   4000 characters. A session writes them itself before it ends (`creatura cell handoff --reason <why> --notes <text>`
   or `--notes-file <path>`); a session that ends without writing one still gets the automatic hand-off, with no notes,
   as today. A session that wrote its own hand-off is not handed off twice: at `SessionEnd` the hook writes the turns
   whose `Stop` never landed and no second `HandOff` when the claim is already handed off.
2. **The continuation.** A `Claim` may say which claim it `Continues`. From a terminal: `creatura cell claim
   --continues <claim id>` (the goal, repository, branch and folder are carried over unless given). In Claude Code: at
   the start of a new session (not a resumed or compacted one) the hook looks for claims handed off **here**, in this
   folder or this repository, that nobody has continued, and shows them to the person with the hand-off's reason and
   the notes' first line, beside the owner's offers, in the same `systemMessage` field that reaches the person and not
   the model (design-activity-stream.md, C29). The first prompt names one (`continue cell-…#n`), and the session's
   claim cites it and carries its goal. A claim that continues a released claim is refused; one that continues a
   claim someone else already continued stands, and the stream marks the fork, as it marks an offer with two takers.
3. **The path across sessions.** `creatura cell ledger --path <claim>` follows continuations both ways: back to the
   first claim and forward through every claim that continues one in the chain, in ledger order, so the path of the
   work is one path whichever session wrote which part. `--open` says "handed off, continued by …" for a claim that
   was. In the stream, a continuing claim is a `Create` whose summary says it continued the earlier goal, with
   `creatura:continues` naming it; a hand-off with notes carries them as `creatura:notes`.

**Who may continue.** Any cell the owner endorsed, on any vessel: the same trust as taking an offer. A session on the
owner's laptop may continue what a cloud session handed off; the same cell may continue its own earlier claim (two
sessions on one machine are one cell). Turns ([design-turns.md](design-turns.md)) need nothing new: a continuing
session writes under its own claim, and a wait, a proposal or an answer between two sessions reads as between two
cells, or, on one machine, between two claims of one cell.

**What the notes are not.** Not the transcript, not the prompt: the session chooses the words, as it chooses a
progress row. The hook never reads the conversation for them. A note that must hold more than 4000 characters is a
shared file (`cell share --file`, design-shared-files.md) named from the notes.

## Pre-registered tests, written before the code

| id | claim | falsifier | status |
|---|---|---|---|
| C38 | a hand-off written with notes keeps them, from the terminal and from a file; a session that wrote its own hand-off ends with no second one, and its late turns are still recorded | notes lost or cut, a second `HandOff`, or a turn missing | holds |
| C39 | at a new session's start the hook shows the claims handed off in this folder or repository that nobody continued, each with its reason and the notes' first line, to the person only; a resumed session is shown nothing; a continued claim is shown to no one after | a claim from elsewhere shown, a continued one shown again, anything reaching the model's context | holds |
| C40 | a first prompt naming `continue <claim>` writes a claim that cites it and carries its goal; `cell claim --continues` does the same from a terminal, carrying repository, branch and folder unless given; continuing a released claim is refused | a claim without the citation or the goal, or a released claim continued | holds |
| C41 | `cell ledger --path` of any claim in a chain prints the whole chain in ledger order; the stream's continuing claim names what it continues and the hand-off carries its notes | an entry of the chain missing, or the stream without the link or the notes | holds |

## Slices

| slice | what | passes when |
|---|---|---|
| H1 | notes on a hand-off, `--continues`, the hook's "handed off here" message and `continue <claim>`, the path across sessions, the stream's link | C38 to C41 (built) |
| H2 | Codex sessions as cells (issue #7): a `codex` cell endorsed like `claude-code`, Codex's `notify` command calling `creatura cell hook --creatura codex`, the rollout file read for names and files only, `creatura doctor` checking it | the owner's yes first; the hook tests of design-cells.md repeated for Codex |

## Results (H1, 2026-11-02)

Built in `Creatura.Cells` (`HandOffPayload.Notes`, `ClaimPayload.Continues`, `WorkLedger.ChainOf`,
`ContinuedBy`), `creatura cell` (`handoff --notes | --notes-file`, `claim --continues`, `ledger --path` across the
chain, `--open` naming the continuer), the hook (`HandedOffHere`, `continue <claim>` in the first prompt, no second
hand-off at `SessionEnd`) and the stream. C38 to C41 hold: `HandoffTests` (4). Not run: a real pair of sessions on
two machines; the next cloud-to-local hand-off is the test.

## What this note does not cover

Putting the handed-off work into the model's context so an unwatched session continues it alone (open decision 3 of
design-activity-stream.md, the same question); choosing between two claims that continue the same one; Codex (H2).
