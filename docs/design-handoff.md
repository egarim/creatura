# Design: handing work from one session to the next

**Status:** proposed and built 2026-11-02: slice H1 (the hand-off with notes and the claim that continues it) and
slice H2 (Codex sessions as cells, after the owner's yes the same day).

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

## Codex sessions as cells (H2)

Codex was the other host tool named in design-cells.md ("How host tools join"; issue #7); only Claude Code was
built. Two Codex generations are in use here: the laptop's 0.111 knows only the `notify` command of its
`config.toml` (one program, run after each finished turn with one JSON argument, `agent-turn-complete`, carrying
`thread-id`, `turn-id`, `cwd`, the turn's prompts and the last answer); the current builds (0.163 and later) have
a hooks system whose events and stdin JSON are Claude Code's, key for key: `SessionStart` (`session_id`, `cwd`,
`transcript_path`, `source`), `UserPromptSubmit` (`prompt`), `PostToolUse` (`tool_name`, `tool_input`),
`Stop` (`stop_hook_active`), `SessionEnd` (`reason`), declared in `~/.codex/hooks.json` in the shape of Claude
Code's `hooks` block (verified in the generated schemas of openai/codex, `codex-rs/hooks/schema`). So:

- **The newer Codex joins through the same hook**, as a `codex` cell: `creatura cell install-hooks --settings
  ~/.codex/hooks.json --creatura codex`, endorsed like `claude-code`. The claim names the tool `Codex`. What
  differs is read, not said: the transcript is a Codex rollout, and the files come from `apply_patch`.
- **A Codex rollout** (`~/.codex/sessions/<y>/<m>/<d>/rollout-<time>-<thread id>.jsonl`) is read the way a Claude
  Code transcript is, for the turns whose `Stop` never landed: a `turn_context` line starts a turn, a
  `task_complete` event ends it, a `function_call` (`exec_command`) or `custom_tool_call` (`apply_patch`) is a
  tool, and the patch's headers (`*** Update File: path`) name its files. Never a prompt, an answer or a command.
- **The older Codex's `notify`** calls `creatura cell hook --creatura codex --notify`, and Codex appends the
  event as the last argument. The thread is the session: its first turn writes the Claim (the folder's goal, where
  from the event's `cwd`), and every turn writes one Progress per turn the rollout shows since the last (found by
  the thread id in the rollout's name). No event says the session ended, so no HandOff is written: `creatura cell
  handoff --session <thread id>` does that, or the next session continues the claim as it stands (H1).
- **The doctor** says whether Codex reaches the ledger: through `hooks.json`, through the `notify` line (turns
  only), or not at all, with the command that would fix it.

Codex's hooks run only once the person has trusted them in Codex's own interface; the doctor cannot see that.
`SessionStart`'s offer and hand-off messages reach the person the same way as in Claude Code when Codex shows a
hook's `systemMessage`; whether it does is not verified here.

## Pre-registered tests, written before the code

| id | claim | falsifier | status |
|---|---|---|---|
| C38 | a hand-off written with notes keeps them, from the terminal and from a file; a session that wrote its own hand-off ends with no second one, and its late turns are still recorded | notes lost or cut, a second `HandOff`, or a turn missing | holds |
| C39 | at a new session's start the hook shows the claims handed off in this folder or repository that nobody continued, each with its reason and the notes' first line, to the person only; a resumed session is shown nothing; a continued claim is shown to no one after | a claim from elsewhere shown, a continued one shown again, anything reaching the model's context | holds |
| C40 | a first prompt naming `continue <claim>` writes a claim that cites it and carries its goal; `cell claim --continues` does the same from a terminal, carrying repository, branch and folder unless given; continuing a released claim is refused | a claim without the citation or the goal, or a released claim continued | holds |
| C41 | `cell ledger --path` of any claim in a chain prints the whole chain in ledger order; the stream's continuing claim names what it continues and the hand-off carries its notes | an entry of the chain missing, or the stream without the link or the notes | holds |
| C42 | a Codex session through its hooks is a claim of the Codex tool with a progress row per turn and a hand-off at its end; the turns whose Stop never landed are read from its rollout with the tools and the files its patches named; a hook's `apply_patch` names its files; never a word of the conversation | another tool name, a turn or a file missing, or a word of a prompt, an answer or a command in the ledger | holds |
| C43 | the older Codex's `notify`, one JSON argument per finished turn: the first writes the claim (the Codex tool, the thread as the session, the folder from the event), each writes a progress row per turn the rollout shows since the last, nothing ends the session; another kind of event writes nothing | anything else | holds |
| C44 | the doctor says whether Codex reaches the ledger: through its hooks file, through its notify line, or not at all, naming the command that would fix it | a wrong or missing line | holds |

## Slices

| slice | what | passes when |
|---|---|---|
| H1 | notes on a hand-off, `--continues`, the hook's "handed off here" message and `continue <claim>`, the path across sessions, the stream's link | C38 to C41 (built) |
| H2 | Codex sessions as cells (issue #7): a `codex` cell through Codex's hooks (the same JSON as Claude Code's) or, on an older Codex, its `notify` command; the rollout read for names and files only; `creatura doctor` checking both | C42 to C44 (built) |

## Results (H1, 2026-11-02)

Built in `Creatura.Cells` (`HandOffPayload.Notes`, `ClaimPayload.Continues`, `WorkLedger.ChainOf`,
`ContinuedBy`), `creatura cell` (`handoff --notes | --notes-file`, `claim --continues`, `ledger --path` across the
chain, `--open` naming the continuer), the hook (`HandedOffHere`, `continue <claim>` in the first prompt, no second
hand-off at `SessionEnd`) and the stream. C38 to C41 hold: `HandoffTests` (4). Not run: a real pair of sessions on
two machines; the next cloud-to-local hand-off is the test.

## Results (H2, 2026-11-02)

Built in the hook command (`ToolNameOf`, `CodexRollout`, `Notify`, files from `apply_patch` in a hook's
`tool_input`), the doctor (`CodexHooks`, `CodexConfig`) and the cell command's usage. C42 to C44 hold: `CodexTests`
(3), with rollouts written by the tests in the shape read from a real one on the laptop (Codex 0.111.0: `session_meta`,
`turn_context`, `event_msg`, `response_item` with `function_call` and `custom_tool_call`). Not run: a live Codex
session on either machine. The laptop's Codex (0.111.0) has no hooks, so its line is `notify = ["creatura", "cell",
"hook", "--creatura", "codex", "--notify"]` in `~/.codex/config.toml`; the next live Codex session there is the test.

## What this note does not cover

Putting the handed-off work into the model's context so an unwatched session continues it alone (open decision 3 of
design-activity-stream.md, the same question); choosing between two claims that continue the same one; a Codex
session seen live.
