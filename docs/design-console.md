# Design: the console, where a person watches the creatures work

**Status:** accepted 2026-11-03 (issue #29; the draft was written 2026-11-02 in a read-only session, and the owner
said "go, separate console program, go with your picks"). Slice U1 built the same day and U2 on 2026-11-03, results at the end; U3 to U5
are next. The draft below is kept as it was written, except where the decisions changed it, which is said where.
Replaces the feed metaphor of [design-activity-stream.md](design-activity-stream.md) with an operator's console;
the stream and the offers stay as they are, the console is another reader of the same ledger.

## The problem, in the owner's words

The stream page shows the ledger as a social timeline. It is readable, but it is not what a person does with a fleet
of agents. Claude Code, Codex and the DeepSeek app feel right because they show one agent's work as it happens, step
by step, with every tool call in view and a place to type. Creatura has many agents, in many homes, judged by worlds
that can lie, so it needs that view times N, plus the two things a single-agent chat never needs: who is working on
what across the fleet, and how one run compares with another.

## Three principles

1. **A view, never the record.** Like the stream, the console is a pure function of files that already exist: the
   verified ledger, the replays, the records of runs. It writes nothing except an offer, which is a ledger entry.
2. **The same in every home.** Terminal and page render the same JSON. Two homes with the same entries show the same
   console. The terminal is first, because that is where the owner and the agents already are, and it ships in the
   single-file executables with no browser.
3. **Private where the ledger is private.** The console binds nothing but `127.0.0.1`, loads nothing from outside,
   and shows nothing the ledger seals. The hub gets no console until a browser can sign (the stream note's rule).

## The three views

### Fleet: who is working on what, now

One row per cell: creatura and vessel, the claim it holds, how long, the last progress line, stress and blocked in
red, last seen; quiet vessels dimmed, revoked cells gone. Sorted by activity. A key opens the cell's Run view, another
offers it a goal (`cell offer`, already in the ledger). This is the stream page's side panel, promoted to the whole
screen, with the timeline demoted to a key.

### Run: one agent, tick by tick

The view that does not exist today and the one people would watch. One cell, one run, as a transcript:

| column | what | where it comes from |
|---|---|---|
| observation | what the world showed | the replay tick, the web observation, the shell output |
| offer | what the control layer allowed, and what it withheld, with the rule | the control layer's offer |
| decision | what the seat chose and why, in its words | the seat's reply |
| act | the tool call, with its arguments | the tick |
| answer | what the world said back, honest or not | the tick |
| verdict | the judge's view, when there is one: the score, a lie caught, a claim held false | the judge's record |

Blocked rows, refused actions and false claims in red. A run can be watched live (the same `IScenarioObserver` the
convoy viewer uses) or read back from its file. A key jumps between the two creatures of a pair, so the contract
negotiation reads as one conversation.

### Compare: runs side by side

The matrix report, the bench and the two-creatures summaries as one sortable table: arm, seat, design, score,
first ship, cost, false claims, with the ratings and their uncertainty when [design-ratings.md](design-ratings.md)
exists. Enter on a row opens its Run. Today these are markdown files written by three different tools.

## One record for the Run view

The convoy already writes `creatura.replay/1` ([replay-format.md](replay-format.md)): header, ticks, summary, read
whole or followed live. The Run view reads that and nothing else. So the other runs learn to write it:

- the web world's recording (`UxRecorder`) writes replay ticks with the observation, the offer, the action and the
  verdict;
- the two-creatures driver (`agents/model.mjs`) writes its transcript as replay ticks: `kind: shell`, the command, the
  exit and the output are the act and the answer, the model's text is the decision; the judge's checks become
  verdict ticks; *(built otherwise in U2: the driver is left as it is, and the console reads a record's transcripts
  into replay ticks as it opens the folder, so every record, the 2026-10-31 ones included, opens the same way)*;
- the bench writes a header and a summary per cell.

The format gains three optional properties on a tick, `offer`, `decision` and `verdict`, and a `kind` per tick, all
ignored by a reader that does not know them (the format's own rule). Version stays 1.

## Where it runs

- **`creatura console`**: the terminal interface, three views on three keys, in the `creatura` program. Terminal.Gui
  (decided below) draws it; *(decided otherwise, see decision 2: a program of its own, `creatura-console`, so that
  `creatura` stays package-free)* it reads the ledger through the same code as `cell stream` and replays through
  `IScenarioObserver`. `--watch <replay>` opens a live run; `--records <dir>` opens Compare on a folder.
- **`creatura cell stream`** gains the same three views as tabs on its local page, rendered from the same JSON
  (`/fleet`, `/run/<id>`, `/compare`). The page stays plain HTML and script with nothing from outside.
- The convoy viewer stays as it is: a map, not a transcript.

## What gets built first

### Slice U1: the Run view over the convoy replays

The terminal Run view reading `creatura.replay/1`, live and from file; the convoy already writes it. Checks:

- U1.1 A replay file read whole and the same run followed live show the same ticks, in the same order.
- U1.2 Every tick kind the format defines today renders; an unknown property or kind is ignored, not an error.
- U1.3 A blocked row, a refused action and a flag are marked; a test counts them against the replay.
- U1.4 Nothing is written to disk by opening a replay.

### Slice U2: the other runs write the record

`UxRecorder`, the two-creatures driver and the bench write replays with `offer`, `decision` and `verdict`. Checks:

- U2.1 A web-world cell's recording opens in the Run view with observation, offer, act, answer and verdict filled.
- U2.2 A two-creatures record converted from its transcripts (a tool for the 2026-10-31 records) opens as two runs,
  and the pair key switches between them.
- U2.3 A verdict tick from the judge marks a false claim on the claim's own row.

### Slice U3: Fleet and offers

The Fleet view over the ledger, and offering a goal from it. Checks:

- U3.1 Two homes with the same entries show the same Fleet, byte for byte, as the stream does (its test reused).
- U3.2 An offer made from the console is the same entry `cell offer` writes; the stream shows it.
- U3.3 A revoked cell is absent; a quiet vessel is dimmed after the stream's quiet time.

### Slice U4: Compare

One table over matrix reports, bench results and two-creatures summaries; ratings join when they exist. Checks:

- U4.1 The three report kinds load into one table with the same columns; a missing value is blank, not zero.
- U4.2 Sorting by any column is stable and Enter opens the row's Run.

### Slice U5: the page

The stream page gains the three views from the same JSON. Checks: U1.1, U3.1 and U4.1 pass against the page's
rendered text with the network off.

## Decisions to take (recommended answers)

1. Terminal first, page second. **Yes**: the users are in terminals, and the executables need no browser.
2. Library: Terminal.Gui or Spectre.Console. **Terminal.Gui**: it has views, keys and scrolling; Spectre is for
   printing, not for a screen that updates. *Taken, with one change the draft missed: `creatura` must stay
   package-free (CellsPurityTest, and the Core's rule), and Terminal.Gui is a package. The owner chose a separate
   program over a hand-drawn screen in `creatura` or the page first: `creatura-console`, project
   `src/Creatura.Operator` (its namespace never `Creatura.Console`, which would shadow `System.Console`), on
   Terminal.Gui 2.5.0. It ships beside `creatura`, `creatura-hub` and `creatura-convoy` in the release.*
3. One record format for every run, `creatura.replay/1` extended, rather than a reader per run kind. **Yes**: one
   reader, one test, and the convoy viewer keeps working.
4. The Run view shows the model's own words for the decision, as they are. **Yes**, scrubbed as the ledger scrubs
   goals; it is the point of the view.
5. Offers from the console. **Yes**, it is the only write, and it is the ledger's own entry.

All five taken as recommended on 2026-11-03, the second with the change above.

## Open

- A pair's two runs as one conversation: interleave by timestamp (clocks are only as good as the vessels') or by the
  ledger's order where both wrote to it. Recommended: the ledger's order when there is one, timestamps otherwise,
  and say which. *Taken (2026-11-03) and built in U2 by the clocks alone, which the view says: the transcripts'
  rows are not ledger entries, so there is no ledger order to read for them.*
- Whether the console may start a run (`creatura ux run`, `run.mjs`). Not in these slices: starting a run costs
  money and needs keys, and the console shows what is, it does not act. Revisit after U4.

## Results (U1, 2026-11-03)

**The program.** `creatura-console run <replay.jsonl> [--follow] [--cell <name>] [--print]`, and `--watch
<replay.jsonl>` for `run --follow`. One run as a transcript: a row per event, with the tick, the cell, the mark,
the act, the verdict, what the cell saw before the tick (its gauge, never its tank: what the world showed it) and the
world's answer in its sentence. Rows of the world itself (a closure, done) are the cell `world`. Blocked rows show
yellow, refused actions and flags (a lie told or caught, or any act whose verdict is a lie) red. Keys: `c` walks
every cell and then each one, `f` stops or resumes following, `q` or Esc leaves. `--print`, or an output that is
not a terminal, writes the same view as text, for an agent or a pipe. The title says the scenario, the arm and what
it is, the seed and the tick; the second line counts the rows, blocked, refused and flagged, and the outcome's first
sentence once the run has ended.

**One model, three ways in.** Everything shown comes from `RunModel`, which reads one JSON line at a time; it has no
screen and is what the tests drive. A file read whole, a file followed as a run writes it (`ReplayFollower`: reads
what was added since the last poll, waits for a line's end, opens the file read-only and shared and closes it again,
starts over when the file gets shorter) and a run in this process (`LiveRun`, an `IScenarioObserver` that writes
each line exactly as `JsonlReplayWriter` does) all go through the same `Apply`. Unknown properties, line types and
event kinds are ignored or shown by their name; a tick event's `offer` and `decision` are read when present, so U2's
records need no new reader.

**Checks.** U1.1 to U1.4 hold: `RunViewTests` (5, in `tests/Creatura.Operator.Tests`). U1.1: arm E seed 0 watched
live through the observer, the same run read back from its file, and the file grown in pieces of 997 bytes (lines
and characters split between polls) all give the same rows, outcome and cells, the ticks only going forward. U1.2:
every event kind the format defines renders as its own act, and an unknown kind, property and line type, and a line
that is not JSON, are skipped or shown, never an error. U1.3: arm E seed 0's refused actions and lies, plus a blocked
road and a lying unload added (no arm writes a blocked road today), counted from the replay's own JSON match the
model's marks and the text view's. U1.4: opening a replay whole, followed, and through the program as text leaves the
folder byte for byte as it was, also while a writer holds the file open. The screen itself was driven in a
pseudo-terminal on the laptop, from the build and from the single-file linux-x64 executable: `c` stepped from every
cell to Truck 1 and Truck 2 with their own counts, `q` left with exit 0, and `--watch` on a file appended a line
every 0.15 s went from "waiting for the run's first line" through the ticks to the end.

## Results (U2, 2026-11-03)

**One record, read by one reader.** The format grows only optional properties, all ignored by a reader that does not
know them (docs/replay-format.md): an event may name its `cell` (any run, not only a truck), its `act` in words, the
`observation` it was decided on, the `offer` and the `decision`, its time (`at`) and whether the world lied
(`lie`); a header may list its `cells`; a tick may be of `kind: verdict`, whose events point at the row they judge
(`on: {tick, contains}`) and say whether its claim was false (`falseClaim`). The Run view marks the judged row where
it stands, and the judge's own words are a row of the cell `judge`.

**The web worlds.** `UxRecorder` keeps its file and its own lines for the web viewer (`format: creatura.web-replay/1`)
and adds, on every action's tick, one event in the shared shape: the persona as the cell, the screen it decided on in a
line, the offer (the screen's controls it could use out of those shown, then its other moves, and what the control
layer withheld with its reason), the persona's words, the act (`click 2 "Save"`), the app's answer and the verdict.
Before the end, a verdict tick holds the judge's findings (the rules), each on its row: the tick the persona said it was
done when the data says not, the tick the app said it saved what it did not keep. The end line gains `outcome`. The web
viewer skips the verdict tick. The header gains the scenario, arm, cells and the tick limit.

**The two creatures.** `creatura-console run <record>` opens a record folder as it is; nothing in it changes.
`PairRecord` reads each creature's transcript (every shell command a tick: the command, its output and exit, the
model's words before it, the step budget), its board messages, the judge's checks of the app (the world's rows, from
`checks.jsonl`), and every claim the judge scored (`summary.json`), placed on the creature's row by the claim's time,
the row whose words are the claim's when there is one. The screen opens three views, both creatures interleaved by
their clocks, then each creature; `p` steps through them. `creatura-console convert <record> [--out <dir>]` writes
the two replays as files.

**The bench.** `BenchReplay` writes an exam cell as a replay: a header (battery, test, arm, world, seat), a tick per
trace tick (each row an event, the control layer's verdict on it, a blocked row marked), the oracle's verdict (a goal
claimed and not held is a false claim on the last row the agent acted on), and an end with the cell's row.
`ExamOptions.ReplayTo` writes every cell's replay into a folder. A series cell keeps no trace: header and end.

**Checks.** U2.1 to U2.3 hold: `RecordViewTests` (4, no browser). U2.1: a web cell recorded with a fake screen (type
"milk", press Save, the app lies, the persona says done, Delete withheld) opens with the observation, offer, decision,
act, answer, verdict and time filled on both rows, and the viewer's own lines still in order. U2.2: the 2026-10-31
DD-M record opens as three views; every shell command of creature a's transcript is a row with its step budget, every
board message of creature b's is a row; both together hold each creature's rows and the world's once, in clock order;
`convert` writes replays that read back as the same runs and leaves the record untouched. U2.3: that record's two
false claims, both creature b's, are flagged on the two `say` commands that made them ("I have a complete app ready"
at 234 s, "port test" at 938 s), its four held claims are not; the web cell's Save row is flagged as a false claim; a
bench cell claimed and not held is flagged on its last act. `AnExamWritesEveryCellsReplay`: an exam with
`ReplayTo` writes one replay per cell that opens with its trace, its blocked rows and its outcome. On the laptop, a
real demo run under `save-lies` (`creatura ux run --demo --barriers save-lies --record`) opens with the Save row
flagged and "the app lied" on it; the pair screen in a pseudo-terminal stepped `p` through the three views and `c`
to creature b alone (92 rows, 2 flagged), and `q` left with exit 0. The whole solution: 961 pass, the browser tests
and the web viewer's included.

**Not yet.** Fleet (U3), Compare (U4) and the page (U5).
