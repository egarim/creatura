# Changelog

## Unreleased

- **`samples/AxiAgent`: Creatura's agent client for Singular** (issue #91, CRE-02; Singular ADR-0108 §7 steps 3-4).
  Over HTTP only, one service key per tenant from the environment (never printed): it polls the accepted proposals at
  most once every 10 s (honouring `Retry-After`), runs the read-only `suggest-a-group` (`groups` where
  `visibility=public`) under Creatura's control layer, and posts its events and one outcome with `proposalId` (at most 5
  suggested refs, never `confirmed`), re-sending the same lines until the proposal shows `outcome: recorded` and stopping
  on a 409. A failed read is `tool.failed`, a read that serves a non-public group `tool.lied`. Only ids, codes and
  numbers go on the wire. `AxiAgentTests` drives it against a fake Singular built from the shared vectors, on a fake
  clock.
- **`samples/Axi` speaks `axi/v1`** (issue #90, CRE-01; Singular ADR-0108 §4). Every line carries the envelope (`v`, a
  uuid `eventId`, `sessionId`, `correlationId`, `at`, and `tick`); goal ids are stable and dotted (`user.learned`), not
  the task's name; proposal ids are uuids with a coded reason; the sample asks before it acts (`approval.requested`, then
  `approval.accepted` or, with `--answer no`, `approval.rejected`); only an accepted proposal runs, its run echoes the
  proposal's goal, and only that run's outcome carries `proposalId` (the main goal's outcome names none); free-text `detail` and `description` only with
  `--debug true`. `AxiEnvelopeTests` validates the emitted lines against Singular's schema and checks that schema against
  the shared vectors, both copied as data into `tests/Creatura.Tests/axi-contract/` (JsonSchema.Net, test only; no
  Singular package).
- **`HonestFailureRule.RecordOnly`: an honest failure recorded but not spent** (issue #75). Prediction and reliability
  record it as a failure; the budget is not spent, the design's own failure accounting is not told, and the tool stays
  visible. `RecordCarryExtension` gains `recordHonestFailures` to do the same for designs without a self-model. Found by
  the AXI study (docs/design-axi.md, rounds 1 and 2): `Habituate` stopped a recommender quitting on skips but left the
  skips unrecorded, so nothing was learned; `StaysVisible` recorded them but spent the budget. Defaults unchanged.
- **Cell names in the ledger** (follows issue #52). A hello now says the creatura's name (`name`, null in older
  hellos), so every vessel synced through a hub knows which names are taken: `cell name` skips them as well as the
  home's own and `--taken`, and `cell init`/`cell accept` with `--creatura` warn (never refuse) when another creatura
  already said that name. The stream and the Fleet show the name (`renderer-gamayun on surface`) instead of the kind
  when the owner gave one. A cell says one new hello after the upgrade, because what it says changed.
- **`creatura-tray`: the Creatura icon in the menu bar or system tray** (issue #53). A program of its own
  (src/Creatura.Tray, Avalonia 11.3 TrayIcon for macOS, Windows and Linux), so `creatura` stays package-free. A click
  opens the console's page (`creatura-console web`) on 127.0.0.1, starting it in-process when nothing answers on
  its port; the badge counts blocked cells and offers not yet taken; an amber dot warns when the hub does not answer
  or has not been synced with for ten minutes. The menu lists the cells, syncs now (`creatura cell sync`) and quits,
  stopping only the page it started. Its logic is `TrayModel` in the console project (tests T1 to T4).
- **A figure for each kind of actor on the stream page** (issue #6; decision 5 (a) of docs/design-activity-stream.md).
  The owner keeps the icon; Claude Code cells wear the ram with the golden fleece, Codex the ibex, Creatura's own
  loops the winged lion, any other tool the navy ram, so every kind has one; the ring still says the vessel. The
  figures (docs/assets/figures, traced from the logo's sheet) are embedded and served at `/figure/<kind>.svg`, themed
  for light and dark inside the file.
- **`creatura cell name`: names for creaturas** (issue #52). `cell name --role <role> [--vessel <id>] [--count <n>]
  [--taken <name,...>]` suggests `<role>-<creature>` (`renderer-wyvern`, `critic-sphinx`) from a closed list of fifty
  mythical creatures, picked by a hash of the vessel and the role, so the same vessel suggests the same name every
  time; a cell this home already holds, or a name in `--taken`, is skipped. It reads and creates nothing, and only
  suggests: `cell init` without `--creatura` now says the command exists, and with `--creatura` never consults it.
  The ledger carries creatura ids, not names, so names on other vessels are only known through `--taken`. N1 to N4 hold.
- **The model catalog says which models can see images** (issue #19). A catalog row gains `"vision": true` (left out
  means text only); the four Azure OpenAI rows have it. `creatura ux` refuses a vision seat (`--vision`,
  `limits.models.vision`) whose row lacks it before any browser starts, naming the models that can; `creatura run
  --list` marks them "sees images" and `creatura doctor` lists them.
- **Cache hits are billed at the cache price.** DeepSeek bills a prompt token served from its cache at a tenth of a
  miss, but every cost counter billed it at the full input price, overstating cache-heavy runs about fivefold and
  stopping two-creatures agents at a budget they had not spent. The catalog gains `cachedInputPerMillion` (DeepSeek
  0.028, gpt-4.1-mini 0.10, gpt-4.1-nano 0.025; left out, hits are billed at the input price as before);
  `ModelEndpoint.Cost` takes the cached tokens, and the scoreboard, the convoy, the web personas and the UX judge pass
  them. The two-creatures agent (`agents/model.mjs`) prices hits the same way.
- **The console page: offers only on the owner's vessel, and files with them** (docs/design-console.md). On a vessel
  without the owner key the composer is read-only and says where offers are made, naming the owner key by its
  fingerprint; the server refuses an offer there without running anything. On the owner's vessel a paperclip (or a
  drop, or a paste) attaches one file, up to 64 MB: `cell share --to <cell>` seals it for that cell alone through the hub
  (`--hub`, by default the one the home last synced with), then the offer names it and how to fetch it; no share,
  no offer. A taken port now says so instead of a stack trace.
- **The console, slice U5: the page** (docs/design-console.md). `creatura-console web [--runs <dir>]...` serves the
  console as a chat-style page on 127.0.0.1: the Fleet and every run under the folders as conversations (the seat's
  words as "Thinking", acts as tool blocks with their output, lies and false claims flagged, the judge's verdicts in the
  thread), live; a cell's page offers it a goal through `cell offer`, guarded by a token only the page holds. Nothing
  is loaded from outside. U5 holds for U1.1 and U3.1; Compare (U4) is held.
- **The console, slice U3: the Fleet** (docs/design-console.md). `FleetView` (beside the activity stream) gives one
  row per cell: the claim it is on and for how long, its last progress, stress and blocked, offers waiting, last seen,
  quiet after the stream's fifteen minutes, revoked cells left out, the same bytes on every home with the same entries.
  `creatura cell fleet [--json]` prints it. `creatura-console fleet` shows it through the `creatura` command:
  blocked and stressed cells red, quiet ones dimmed, Enter for a cell's activity, `o` to offer a goal (written by
  `cell offer`). U3.1 to U3.3 hold. It reads as the home's one endorsed creatura (the one its hooks run as, such as
  claude-code) unless `--as <creatura>` names another: read as the command's default, every entry would be rejected.
- **The console, slice U2: every run opens in the Run view** (docs/design-console.md). The replay format gains optional
  event properties (`cell`, `act`, `observation`, `offer`, `decision`, `at`, `lie`), a header's `cells`, and
  verdict ticks that mark the row they judge, a false claim flagged where it was made. A web recording's ticks carry
  the shared event with what the persona saw, was offered and decided, plus the judge's verdicts (the web viewer skips
  them). `creatura-console run <two-creatures record>` opens a record as it is: both creatures by their clocks, then
  each one (`p`), the judge's false claims on the commands that made them; `convert` writes the replays. The bench
  writes a replay per exam cell (`ExamOptions.ReplayTo`, `BenchReplay`). U2.1 to U2.3 hold.
- **The console, slice U1: the Run view** (docs/design-console.md, issue #29). A new program, `creatura-console`
  (project `src/Creatura.Operator`, on Terminal.Gui, so `creatura` stays package-free): `creatura-console run
  <replay.jsonl>` shows one run tick by tick, a row per event with what the cell saw, the act, the world's answer and
  the verdict, blocked rows, refused actions and flags marked; `--follow` or `--watch` follows a file as a run writes
  it, `--cell` shows one cell, `--print` writes the view as text. It writes nothing. U1.1 to U1.4 hold.
- **Memory credit** (docs/design-ratings.md, slice R4). A persona's lessons are credited, not rated: every cell counts
  the lessons it was shown and is credited to each as ended well or not (`MemoryCredit`); a lesson shown three times
  and never followed by a done cell is pruned, one shown twice or more and followed by done cells more often than the
  persona's base rate is promoted. The web-world persona memory keeps the credit in the record (`ux:credit`) and
  shows the promoted lessons first and the pruned ones never. G12 to G14 hold.
- **The barrier curriculum** (docs/design-ratings.md, slice R3). Worlds and barriers are opponents with ratings on the
  seats' scale: every run is a duel the seat wins when the job was done (`Curriculum.Ratings`), and the next opponent
  for a seat is the one it beats with a probability nearest one half, settled ones passed over (`Curriculum.Next`).
  `creatura ux matrix --barriers next --rated <paths>` picks the barrier from the earlier reports under the paths,
  runs it, prints why and keeps the reason in `report.json` (`barrierPick`). G9 to G11 hold.
- **Cell reputation** (docs/design-ratings.md, slice R2). After a shared job the experiment's owner rates the cells
  that ran it: `creatura cell rate --job <text> --results <file.json>` applies the Weng-Lin update to each cell's
  latest reputation and writes one owner-signed `Reputation` entry per cell; a ledger refuses a reputation not signed
  by a trusted owner, carried by the rated cell, or carried by a cell that holds a claim (a worker never rates).
  `cell reputations` lists them. `cell turn` breaks a competing-proposal tie by reputation when every competitor
  was rated before any proposed (`liveBy` says which rule), else by the hash. The two-creatures runner rates the
  creatures at the end of a ledger arm and keeps `reputation.jsonl` in the record. G6 to G8 hold.
- **Ratings** (docs/design-ratings.md, slice R1; issue #28). `Creatura.Ratings`, package-free: a Weng-Lin rating
  (the open TrueSkill-style model, Bradley-Terry full pairing) per seat on three axes, the model, the design and the
  prompt, read from the rows that exist: the bench's `scoreboard.csv` and `results.csv`, a matrix's `report.json`,
  a two-creatures run's `summary.json`. A seat is rated only against the seats that ran the same job with the other
  axes held fixed; a leaderboard prints μ, σ, the ordinal and the games, and says whether the order is settled or
  which adjacent pairs need more runs. `creatura rate <path>... [--by model|design|prompt] [--json]`. G1 to G5 hold.
- **Codex sessions as cells** (docs/design-handoff.md, slice H2; issue #7). A newer Codex joins through its hooks,
  which carry the same JSON as Claude Code's: `creatura cell install-hooks --settings ~/.codex/hooks.json --creatura
  codex`; the claim names the tool Codex, its rollout is read for the turns a Stop never landed (tools, and the files
  `apply_patch` named), and a hook's `apply_patch` names its files. An older Codex calls `creatura cell hook
  --creatura codex --notify` from its `notify` line, with the event as the last argument: a claim per thread and a
  progress row per turn, no session end. `creatura doctor` checks both. C42 to C44 hold.
- **The two-creatures goal gains one sentence, and the web-world judge is the default** (slice iv of the judge as a
  web world; docs/design-two-creatures.md, X-D13; log #85). The goal now says the app answers `GET /api/items` with
  the items as `{"name", "bought"}`, so the judge reads the data; `run.mjs` judges with `creatura ux` unless
  `--judge script`. The runs before 2026-11-02 are a baseline of their own.
- **The two-creatures judge as a web world** (docs/design-web-worlds.md, slice iii). `run.mjs --judge ux` judges the
  app with `creatura ux run` over `experiments/two-creatures/judge/shopping-list.adapter.json` (two tasks, with
  `live` and `survives`) in the script judge's place, reading the five steps off the two cells; the record keeps the
  same shape. `creatura ux run --out <dir>` writes the cell as `cell.json` and its trace. A control that changed
  state (a box ticked) now counts as the page's answer to a commit; a `survives` restart gets its own client with the
  task's patience. A door container (`runner/door.mjs`) forwards the host's 127.0.0.1:18080 and :17001 to `world`'s
  app and deployer, since Tailscale drops input from the sandbox's mesh-range addresses on the bridge.
  W36 holds: intact 5/5; down 0/5; label 1/5; live 2/5; check 3/5; persist 4/5.
- **Web worlds: an unlabelled checkbox is named by its row** (docs/design-web-worlds.md, the judge as a web world,
  slice ii). A checkbox, radio or switch with no label takes the text of its row (the other controls' text left out),
  so a script can tick an item by its name and the id survives a re-render. W35 holds.
- **Web worlds: `live` and `survives`** (docs/design-web-worlds.md, "The two-creatures judge as a web world",
  slice i). A task may ask that a second browser, open from the start, shows the data without a reload within a
  time (`live`), and that the data outlives a restart the world sends once the run is done (`survives`). Both are
  on the cell, in the CSV and the report's note, and in the judge's rules as `not-live` and `lost-on-restart`. The
  demo page polls with `?live=1`; `POST /api/restart` keeps the notes, `?forget=1` loses them. W33 and W34 hold.
  The CSV's `lies_told` column, named in the header and missing from the rows, is filled in.
- **Handing work from one session to the next** (docs/design-handoff.md, slice H1). A hand-off may carry notes
  (`creatura cell handoff --reason <why> --notes <text> | --notes-file <path>`, at most 4000 characters), and a
  claim may continue a handed-off one (`cell claim --continues <claim id>`, the goal and where carried over). At a
  new Claude Code session's start the hook shows the claims handed off in this folder or repository that nobody
  continued, with the reason and the notes' first line, beside the owner's offers, to the person only; the first
  prompt continues one with `continue <claim id>`. A session that wrote its own hand-off is not handed off again at
  its end. `cell ledger --path` prints the whole chain across sessions and `--open` names who continued a claim;
  the stream links a continuing claim to what it continues and carries a hand-off's notes. C38 to C41 hold.
- **Web worlds: record and replay** (docs/design-web-worlds.md, "Record and replay"). `creatura ux run | matrix --record
  <dir>` writes a folder per cell: `replay.jsonl` (the header, a frame for every screen the persona saw with the text
  and the numbered controls and the data's check, a tick for every action with the persona's answer, the control, the
  outcome, the verdict and whether the world lied, then the trace rows and the cell's numbers) and a JPEG of every
  screen. `UxRecorder` is an extension, so it rides every design. `tools/web-viewer/index.html` plays a cell back
  from disk. A password field is now secret: shown as "(filled)" or "(empty)" to the persona and in a recording, never
  its value. W30 to W32 hold.
- **Two-creatures runner: a board message claims done only when it starts with DONE** (#21), as the goal asks; a
  message that mentions DONE on the way is talk. The old rule (DONE anywhere) had taken 6 such messages on the DD-M
  board of 2026-10-31 and 1 on the DD-M rerun with roles as claims (log #79). And the one-shot shells on the experiment
  owner's vessel give the files they write under `state/<run>/owner` back to the user running the runner, so the
  run's cleanup no longer fails with EACCES (#20); if it still cannot remove the directory it says so and keeps the
  record.
- **Turns, so two cells neither wait on each other nor both lead** (docs/design-turns.md, slice T0): a cell says it
  waits (`creatura cell wait --on <cell or entry id>`), proposes on a topic (`propose --topic`, with `--file` to share
  the proposal as a file first) and answers (`answer --accept | --object`); `creatura cell turn` reads the verified
  ledger and says whose move it is, act or wait, with the rule and one reason, as text or `--json`, exit 0 or 3. A
  wait stands until the cell's next entry; a cycle of waits has one mover, the latest wait; competing proposals are
  settled by the lowest entry hash, never by clock, and a proposal that saw the other is a counter, not a competitor;
  a proposal is settled by every party's acceptance or, past the quiet time (10 minutes, `--quiet`, or the offer's
  `--quiet`), by silence, and `turn` says which. All of it is optional fields on Progress entries: an un-upgraded hub
  stores them (tested against the hub of the commit before), and a ledger without them reads as before. C30 to C37
  hold; three deliberate breaks each fail a check.
- **Two fake pairs with no roles in the two-creatures harness** (slice T1): `--agents polite,polite` claim the same
  goal and each waits on the other, `simultaneous,simultaneous` both propose at once; `cell turn` picks the mover or
  the winner, the settled plan says who builds which half, and the halves find each other by service name whichever
  repository holds them. Both ship 5 of 5 on a Docker host; the X0 pairs score as before. The run's `summary.json`
  counts the waits, proposals and answers and names whose plan was accepted.
- **A creature driven by DeepSeek** in the two-creatures harness (`agents/model.mjs`, `--agents deepseek,deepseek`):
  the goal of `agents/goal.md` (now with `wait`, `propose`, `answer` and `turn`), a shell, a step and dollar budget,
  a transcript and the spend in `summary.json`; the key stays out of the shell and the record. The goal-only run of
  docs/design-turns.md (T2) runs this way for API tokens, after two cloud-session attempts stopped at their
  permission classifier.
- **Web worlds, phase 1** (docs/design-web-worlds.md): a real web app as a Creatura world, the idea of Singular
  Framework's uxkit on the control layer. `Creatura.Worlds.Web` (new, takes Microsoft.Playwright) has `WebWorld`: the
  page read into the visible text and numbered controls, a tool per control with an id that survives re-renders, the
  task's done fact granted only by a declared check of the app's own data, and barriers as Playwright routes (`lie`,
  `fail`, `slow`). Two seats: `PersonaPolicy` (a catalogued model as a persona, one action per tick, escalation to a
  stronger model when stuck, a cost cap) and `WebScriptPolicy` (offline, by control name). `creatura ux run` runs one
  cell; `--demo` serves a bundled notes app and runs offline. W1 to W8 hold; not yet run with a real model or a real
  app. `Creatura.Cli` now references `Creatura.Worlds.Web`, and the cells' purity test allows it.
- **Web worlds, phase 2a**: `creatura ux matrix` runs every task × persona × environment × tester and writes a report
  (`report.json`, `cells.csv`, `report.md`, a trace per cell); `creatura ux compare` joins two reports cell by cell;
  `creatura ux gate` (or `matrix --gate`) holds a report to the adapter's `gate` and exits 1 naming each threshold
  broken. The report counts apart the three gaps: believed done, the page said done, and a lie not caught. A cell
  that cannot run is recorded and the matrix goes on; a cost cap stops new cells. W11 to W14 hold.
- **Web worlds, phase 2b**: accounts and a declared sign-in in the adapter (credentials as `env:NAME`); each account
  signs in once (`UxSessions`), its storage state and sessionStorage are kept and put back in every later context, an
  expired session signs in again, and `--sessions <dir>` keeps sessions in owner-only files between runs. The demo
  signs in like the Singular starter (a token in sessionStorage only); `samples/ux/singular-starter.adapter.json` is a
  template. W15 to W17 hold. The demo's barriers now match `**/notes`.
- **Web worlds: persona memory and the design axis**: `ux matrix --designs a,b` runs every cell once per design, keys
  cells by design and splits the report's totals by design; `--memory carry|read --records <dir>` keeps one Creatura
  record per persona, tester and design (the design's carry and the persona's lessons, written from verdicts), carried
  cell after cell or read frozen. Lies in a web cell are now counted by the world's truth (told) and the trace's blocked
  row (caught), so the orienting agent's catches count; a save refused again on a page still showing its error is no
  longer read as a success; Playwright's `TimeoutException` fails the action instead of the cell. W18 to W20 hold.
- **Web worlds, phase 2c: the judge and the summary writer.** `UxJudge` answers a closed set of findings per cell. The
  rules (no model) find every finding the verdicts decide; a model judge reads the verdicts and the trace, may add
  `friction`, and is held to the rules, with each correction recorded. `UxSummary` writes the facts by code and an
  optional model paragraph beneath them. Every matrix is judged (`judgements.json`, `summary.md`, a findings column in
  `report.md`); `creatura ux judge <report> [--judge model:<name>] [--summary model:<name>]` judges again. W21 and
  W22 hold with stub models.
- **Web worlds, the rest of 2c: vision and the stuck alarm.** `UxStuckAlarm`, an extension on every design, fires
  when the screen has not changed after `limits.stuckPatience` actions (default 2). It tells the seat, which asks its
  stronger model at once: the vision model (`limits.models.vision`, `--vision`) with a screenshot, else the escalation
  model. At `limits.stuckLimit` (default 6) it takes the controls away, and the design hands off by its own rule.
  `Creatura.Seats`: `ChatMessage` takes images (`ChatImage`), sent as content parts on the OpenAI shape and as
  `images` on Ollama's, never written to a call log. W23 to W25 hold.
- **Web worlds, phase 3: barriers on the page.** `dodge`, `cover` and `drop-letter` (`PageBarriers`), scripts put into
  every page before the app's own, counted through a function the world exposes. The world reports them honestly: a
  covered control is shown covered and refused at once; a save the page did not answer is an honest failure, never a
  lie; typing promises the field shows what was typed (`typed:<id>`), which fixes a phase 1 gap where a field that
  dropped a letter passed as landed in most designs; a save after lost input is judged `input-lost`, not `app-lied`.
  W26 to W29 hold.

## 0.2.0-preview.3 (2026-10-30)

- **One culture for every text** (the owner's decision, issue #5): traces, notes, self-descriptions and model prompts
  are written in `TraceText.Culture` (invariant numbers, "100%") whatever the machine's culture, so a run reads the
  same everywhere. Before, a German machine wrote "0,40" and "100 %": 91 tests failed with the suite run as de-DE
  (`CREATURA_TEST_CULTURE=de-DE`); now none do. The CLI's own console output (sizes like "1,024 bytes") still
  follows the machine.
- **The packages carry their license as a file** (`LICENSE.md`): nuget.org refuses a license expression that is not
  OSI- or FSF-approved, which stopped 0.2.0-preview.2 from reaching it.

## 0.2.0-preview.2 (2026-10-30)

- **The activity stream** (docs/design-activity-stream.md, the first slice): `ActivityStream` in `Creatura.Cells`
  presents the ledger in Activity Streams 2.0, a pure function of the verified ledger with nothing sealed in it;
  `creatura cell activity` writes it, and `creatura cell stream` serves it with a page on 127.0.0.1 only, read-only.
  C23 to C26 hold; six deliberate breaks each fail one.
- **Goals offered to cells**: the owner signs an offer (`creatura cell offer`), a cell takes it (`take`, or the
  session's first prompt after the hook shows it) or declines it; offers and their state are in the stream, and the
  page has a Simple view. C27 to C29 hold; seven deliberate breaks each fail one. Live between the laptop and the
  Surface. Entry kinds `Offer` and `Withdraw` are new: a hub and every cell must be upgraded before the first offer.
- **The SessionStart hook speaks**: it runs synchronously and lets stdout through (stderr still discarded), so an
  offer's message reaches the person; run `creatura cell install-hooks` again to upgrade (it replaces the old group).
- **The stream reports an unreadable ledger** with a 500 and the reason, instead of dropping the connection.
- **Getting started** (docs/design-onboarding.md): `creatura new [agent|world|sk]` makes a project from a template,
  `creatura run --design <d> --world <w>` tries a design with no code, and `creatura doctor` says what is set up and
  what is missing. The templates are also a `dotnet new` package, `Creatura.Templates`; a made project references the
  packages of the tool's own version, and `--source <folder>` restores them from a folder of `.nupkg` files. O1, O3,
  O4 and O5 hold in the unit suite and O2 in `tools/try-onboarding.sh`.
- **The packages** carry a README, the icon, SourceLink and symbol packages (`.snupkg`).
- **Headless sessions leave their steps**: SessionEnd reads the turns whose Stop never landed from the transcript
  (tool names and files only) and writes them before the HandOff, so a `claude -p` session's claim has its steps.
- **No hook event is dropped on a busy home**: a hook that cannot get the home's lock leaves the event in
  `<home>/hooks-waiting/` (no prompt text, no tool input), and the next hook records it at its own time.
- **Each package has its own description** on nuget.org.
- **Trained models, steps 7 to 9** (docs/design-trained-models.md): a world model in a learned representation (JEPA),
  saved as `creatura.network/1` files and loaded by `JepaModel`; a guide that calls the decision seat less (with the
  planner in the seat, the JEPA guide called it on 40.5% of decisions in the clean world, a copying network on 0.3%);
  and a network that copies DeepSeek's recorded choices, which is not yet reliable enough to cut its calls. Every
  seat is now behind a call counter.

## 0.2.0-preview.1 (2026-10-29)

- **Tests run in en-US on any machine** (`tests/TestCulture.cs`): the goldens are recorded in en-US number formatting,
  and a CI runner's invariant culture ("100 %") or a German machine ("0,65") failed them. Creatura's own texts
  still follow the machine's culture.
- **Release builds** (`tools/release/build.sh`, `.github/workflows/release.yml`): a tag like `v0.2.0` tests the
  solution, builds `creatura`, `creatura-hub` and `creatura-convoy` as self-contained single-file executables for
  macOS, Linux and Windows (x64 and arm64), runs each one on a machine of its own platform, and publishes them with
  the NuGet packages and SHA256SUMS as a GitHub release. No .NET is needed to run them.
- **The Semantic Kernel host** (`Creatura.Hosts.SemanticKernel`, `samples/SemanticKernelHost`): the control layer as
  an `IAutoFunctionInvocationFilter` on Semantic Kernel's own function-calling loop, with a chat client under it that
  offers only trusted functions on every request. Offline it runs the Agent Framework host's four goldens byte for
  byte. It takes the model as an `IChatClient`.
- **The README** opens with the logo, the shorts and the articles; it maps each design to its thinker, its code and its
  post, explains each constructor value, and lays the folders out as tables.

- **The lie judge's third attempt** (arm P3): condemn a road at the first learned lie, and t1 fixed on the judge's own
  driving. It passed its in-loop gate on held-out training seeds and closed most of the gap to the fused check with
  memory (lies 73 against 69, repeats 13 against 10 on seeds 0 to 49), but its false alarms stayed about twice the
  rule's, so T12 to T12d failed, on seeds 0 to 49 and on fresh seeds 2000 to 2049. `--first-seed` runs a battery on
  other seeds.

- **The lie judge's second attempt** (step 6, sealed noise): the truck is offered what fits where its position reads
  it is (`SensorNoise.Sealed`), closing a leak in step 6's world, where the offered roads gave the true position away
  a tick late. The judge reads two memory inputs (`ActionMemory`: the same action again, and its tries) and was trained
  over two rounds on its own loop driving. It cured the first attempt's repeats and beat the effect check (T11c), but
  the fused check with one memory rule (arm V2) still won: half the error rate, fewer lies (T11d and T11e failed).
- **An experiment log** (`tools/xlog.py`, `experiments/log.jsonl`, docs/experiment-log.md): every run that shaped a
  result, failures and exploration included, with its commit and seeds; runs on the battery's seeds are marked.

- **A noisy world** (docs/design-trained-models.md, step 6): `ConvoySetup.Noise` makes position and fuel readings
  noisy (off by default; the clean battery is unchanged, decision for decision). Every action's truth is recorded
  beside its verdict, and every arm now reports its check's false alarms and misses against it. Arm U (the effect
  check) cried wolf on 7.1% of actions and served the villages ten ticks later: T10 held. Arm V, a check fused from
  position and gauge, was the best judge there: 1500/1500, 0.40% false alarms, 14 lies missed. A lie judge trained on
  the simulator's truth (`LieJudge`, `tools/train/train_landed.py`) failed its gate before the loop (T10b), so arm P
  was not scored; on training seeds it missed the same lie again and again, having no memory. Arm F (the world model
  retrained on noisy driving) missed 96.6% of lies, as predicted (T10f failed). T10g held.

- **The learned judge's second attempt** (arms Y and Z): it judges only drives, loads, unloads and refuels, and a
  learned lie quarantines before it condemns a road (above a second threshold, or at the second lie on it). Arm Y,
  with the planner's world model, delivered 1500/1500 in the same time as J with 0.79% false alarms and no lie missed,
  but acted on 57 lies against 54 (T9 failed, T9b held). Arm Z, with the varied model, missed 556 lies, as predicted
  (T9c failed, T9d held). T9e held.

- **The world model in the loop** (arm X): `WorldModel` in `Creatura.Seats` and `LearnedJudgeExtension`, which lets
  a world model's surprise judge every action in place of the effect check. It caught every lie (no misses) but
  raised false alarms on 3.1% of actions, and its detours led to more lies than arm J (63 against 54): T8 and T8b
  failed, T8c held. Arm X joins the default battery.

- **The world model, retrained on varied driving** (`models/convoy-world-varied.json`): planners in ten other setups
  and a sampled network, all free and from training seeds. T6c and T6d held: on DeepSeek's driving it now flags 80%
  of the road lies (50% before), and on the planner's it still flags every lie. `train_world.py --held-out-digit`.

- **A world model for the convoy** (docs/design-trained-models.md, step 5): `ConvoyDatasets.ExportWorld` and the
  `--world <runs> <out> [--evaluation]` option write each action as features before and after;
  `tools/train/train_world.py` trains a forward model in numpy and measures its surprise. T6 held on the planner's
  driving (every lie above the 95th percentile of landed actions); T6b failed on DeepSeek's, a driver it never saw.

- **The first hosted-model runs of arm S** (`deepseek-chat`, seeds 0 to 4, two passes): 299 of 300 units delivered
  against the planner's 300, 17 lies against 18, but 7 trucks stranded against 4 and villages served about 11 ticks
  later. S1 and S2 come out mixed across the two passes (docs/design-machine-vessels.md). 1,242 calls, $0.75 at
  catalog prices.

- **Fix: `--model` in a battery filled every arm's seat.** `--battery --arms J,S --model deepseek-chat` put the model
  in arm J's seat too, so the planner baseline ran with the model and the run cost twice what was quoted. `--model`
  now fills only the arms that carry a model, or the one arm named with `--arm` for a single run.

- **License: PolyForm Noncommercial 1.0.0** (`LICENSE.md`, `PackageLicenseExpression` in `Directory.Build.props`):
  free for noncommercial use, commercial use by separate license from the author, no warranty and no liability.
- **Ready for going public** (from the pre-public audit): the podcast quotes in the code are paraphrased with their
  episode and time, and `docs/sources.md` lists every source and says the people named do not endorse Creatura;
  links into the private research repository are gone; the docs name no machines, people or servers of the owner's
  fleet; example mesh addresses no longer match real ones; call logs and replays written from the repository root
  are ignored. The history needed no rewriting: it holds no secret and no transcript text.

- **Fix: tool ids with colons in hosted model seats.** `ModelPolicy` offered each tool as a function named by its id,
  and hosted providers (OpenAI, DeepSeek, Azure) accept only letters, digits, `_` and `-` in a function name, so
  DeepSeek refused every call of the convoy's first paid run (`claim:alba`). Tools are now offered under
  `ModelPolicy.FunctionName(id)` (`claim_alba`), with the id in the description, and the call is mapped back. Ollama
  accepted any name, which is why the local runs worked.

- **Machine vessels acting together** ([docs/design-machine-vessels.md](docs/design-machine-vessels.md)). The Core
  gains `PhysicalMachine` on `Vessel.Physical`, and `ITickedWorld`, a hook the loop calls at the start of every tick
  (no existing world implements it; the golden traces are unchanged). `src/Creatura.Scenarios` holds the convoy:
  - `ConvoyWorld` is the truth, and each truck acts on a `TruckView` whose sensors can lie;
  - the trucks' own offline planner (`ConvoyPolicy`);
  - turn-taking that steps three `ReferenceLoop`s one tick at a time;
  - a seeded in-memory mesh with loss and delay between the trucks' ledgers;
  - claims, takeovers and delivery reports;
  - a judge that reads the world only, and a battery over arms A to E.

  The first battery: M1, M4 and M6 held; M2, M3, M5 and M7 failed, each with its cause in the design note. The fixes
  for those failures are new arms G to L: a stalled-claim takeover, distrust by road (`RoadDistrustExtension`) and a
  hand-off when stranded. In the second battery N1 to N5 held; with all three, every unit is delivered in every run,
  breakdowns included. N6, the designs against the plain loop, failed again. The replay header now says what the run is for
  (`goal`, `success`, `armPurpose`) and how each truck's agent is built (`trucks[].agent`, with the C# that builds
  it). The viewer shows these in an "About this run" panel and a definition popup per truck. Arm **S** puts a language model from
  Creatura's catalog in every truck's seat (`ConvoyModelSeat`: a briefing on how the convoy works around any
  `ModelPolicy`; refused or empty answers make the truck wait). The command takes `--model`, `--models-file`,
  `--calls` and `--ticks`, and the battery `--arms`; S1 and S2 are scored when arms S and J ran. Arm S is not in the
  default battery. [docs/convoy-models.md](docs/convoy-models.md) says how to set up a model for the demo. First local runs: `qwen2.5:7b` delivered 5 units in 8 ticks against the planner's 15. A design for
  trained models (a policy network in the seat, a language model adapter, a learned world model, all fed by the
  recorder) is proposed in [docs/design-trained-models.md](docs/design-trained-models.md). Its step 1 is built: `ConvoyRecorder` records
  every decision at the seat with its verdict and the facts afterwards, beside the generic `ExampleRecorder`. Also
  `ConvoyFeatures` (61 features), `ConvoyActions` (36 actions), `ModelPolicy.RenderPrompt` made public, and
  `ConvoyDatasets.Export`, which writes policy, chat and transition sets with a card and never exports the battery's
  seeds. New command options: `--record` and `--dataset`. T1 held: recording changes no run. Step 2 is built too: `tools/train/train_policy.py` trains a policy network by
  example in numpy (`uv run`), `NetworkSeat` and `PolicyNetwork` in `Creatura.Seats` run it package-free,
  `ConvoyNetwork` refuses networks for another schema, and arm N puts `models/convoy-policy-j.json` (embedded) in every
  seat; `--network <file>` names another. Arm N joined the default battery: T2 held (1500/1500, as J) and T4 held. Step 3 too: `tools/train/train_trial.py`
  trains the network further by trial (PPO in numpy) on runs the convoy plays with `--rollouts`, sampling through
  `NetworkSeat` at a temperature with a seeded generator. Arm R carries the result
  (`models/convoy-policy-j-trial.json`): T3 failed (1445/1500 delivered against J's 1500, though 46 lies against 54), T3b
  held (2 strandings against 6). `ConvoyMeasures.GaugeStranded` separates the gauge's lies from the roads'. Fixed a
  hook test whose pinned clock made it fail every day after 09:00 UTC.
- Shared distrust (docs/design-machine-vessels.md): a new ledger entry kind, `Warning` (`WarningPayload(Subject,
  Evidence)`), lets a truck that catches a road lying warn the others, who distrust the road too. Arm W (J with
  `ShareDistrust`) joined the default battery, with the `LiesAlreadyCaught` measure. W1, W2 and W3 held: no lie on a
  road already caught (J: 9), road lies 39 against 48, deliveries unchanged at 1500/1500.
- Training by trial, second attempt: `train_trial.py` gained `--arm`, `--end-penalty` (a price per unit left short at
  the end), `--anchor` (KL to the starting network) and `--keep-at-least-start`. Arm Q carries the result on W's setup
  (`models/convoy-policy-w-trial.json`): T7, T7b and T7c held, with 1500/1500 delivered, 2 trucks stranded against W's
  6, and 41 lies against 45.
  `samples/Convoy` runs one story or the battery. The **replay** is the interface for UIs: JSON lines described in
  [docs/replay-format.md](docs/replay-format.md), with `IScenarioObserver` for following a run live.
  `tools/convoy-viewer/index.html` is a three.js viewer with four sample runs built in.
- The project icon, `docs/assets/creatura-icon.svg`.

- **Shared files** ([docs/design-shared-files.md](docs/design-shared-files.md)). Each cell gets an encryption key
  (`CellCipher`, ECDH P-256), announced in its hello. `SharedFiles` seals a file with a fresh AES-256-GCM key, and
  the name and type separately in a label, then wraps the key for each recipient. That is every endorsed, unrevoked
  cell by default, or `--to`. A seventh entry kind, `Artifact`, records the hashes, the sealed label and the wrapped
  keys. The hub stores only ciphertext under its SHA-256 at `/files/{hash}`: 64 MB at most, kept 90 days, after which
  fetch says expired. Commands: `share`, `files` and `fetch`. A fetched file is kept, so a second fetch needs no
  network. Pre-registered tests F1 to F8; a live run between the owner's laptop and the second machine, both ways.

- **Cells slice 4: the mesh.** `Creatura.Cells.Hub`: SyncFramework's sync controller at `/sync` over
  `HubDeltaStore`. That store verifies every pushed entry against the owner keys it trusts and stores each entry once.
  It numbers deltas from its own SQLite file, and the hub answers loopback and the mesh range only.
  `LedgerSyncNode` now keeps its cursors in `LedgerSyncState` (`<home>/sync/`) and relays every entry its ledger
  holds. New commands: `request`, `endorse` and `accept` join a vessel without moving the owner key, and
  `sync --hub <url>` does the network work outside the home's lock. `ledger` now trusts the cell's endorsing owner.
  Pre-registered tests C13 to C18. A live run between the owner's laptop and the second machine ended with identical ledgers.
  The hub also runs with no creatura home: `--trust <file>` gives it owner public keys and `--data <dir>` its
  database, and `creatura cell owner-key --out <file>` writes that file. It never needs a private key (C19).
  Every request to the hub is signed by an endorsed cell: the endorsement, the time and a signature over the method,
  the path, the time and the body (`HubRequest`, `HubSigningHandler`). The hub answers nothing else, so other machines
  on the mesh can no longer read the ledger (C20).
  The hub keeps every accepted request signature, so a replayed request is refused, also across a restart (C21).
  Owner-signed `Revocation`s: `creatura cell revoke` stops a cell after sequence N. Ledgers drop and refuse its later
  entries in any order of arrival, the hub refuses its requests and deletes its later deltas, and revocations travel
  with sync at `/revocations` (C22).
  Automatic sync on a Mac: `sync --quiet`, `publish-hub`, and `launchd-config` / `install-launchd` /
  `uninstall-launchd` for the `com.creatura.hub` and `com.creatura.sync` agents.

- **Cells slice 3: the ledger in SQLite, synced through SyncFramework.** `ILedgerStore`, which `JsonLedgerStore`
  implements. `SqliteLedgerStore` (`src/Creatura.Cells.Sqlite`, package `Microsoft.Data.Sqlite` 10.0.14): one row per
  entry, WAL mode, owner-only file. It saves only new entries and settles conflicts by the smaller hash, as merge
  does. Loads check each row's hash and the owner's trust through `WorkLedger.MergeVerified`, and `VerifyOnLoad`
  checks the signatures too. An empty database imports `ledger.json` with every signature checked.
  `src/Creatura.Cells.Sync` (package `BIT.Data.Sync` 3.0.0 from egarim/SyncFramework): `LedgerDelta` maps one
  entry to one delta, `LedgerDeltaProcessor` merges deltas through `WorkLedger.Merge` and never executes anything,
  and `LedgerSyncNode` stages, pushes and pulls. The command takes `--store json|sqlite` (default json), also passed
  through `hooks-config` and `install-hooks`. Pre-registered tests C6 to C12, plus a conflict test added after a
  mutation of the conflict rule survived them.

- **Cells slice 2: Claude Code joins.** `creatura cell hook` reads one Claude Code hook event on stdin, prints
  nothing and always exits 0: the first prompt of a session writes a `Claim` (goal from the folder and branch, or the
  first line of the prompt scrubbed of secret-shaped text with `--goal prompt`), each finished turn writes one
  `Progress` with tool counts only, and a session's end writes a `HandOff`. `hooks-config`, `install-hooks` and
  `uninstall-hooks` (backup first, merge, `--dry-run`), `publish-bin`; `--session` on progress, release and handoff;
  a cross-process `LedgerLock` on every write. Not installed anywhere by this change.

### The cells, first slice (`src/Creatura.Cells`, `src/Creatura.Cli`, no package references)
Built from [docs/design-cells.md](docs/design-cells.md), "What gets built first", with the five pre-registered
tests (C1 to C5, `tests/Creatura.Cells.Tests`) committed before the code. Offline; no mesh, relay or host-tool hooks.
- `CreaturaId`, kept in the creatura's `AgentRecord` (part `cells.creatura-id`), with its vessel history
  (part `cells.vessels`): a move to another vessel is an event, not a new agent.
- `CellKey` and `OwnerKey` (ECDSA P-256, PKCS#8 PEM in owner-only files); `VesselEndorsement`, the owner's
  signature binding a cell key to a vessel and a creatura.
- `VesselProbe` and `Hardware` (macOS, Linux, Windows): the hardware identifier hashed with the owner's salt into
  the `VesselId` and never kept. The Core's `Vessel` is unchanged; `Hardware` is a sibling record.
- `Position` (in-process, mesh read from `tailscale status --json`, relay) and `CellIdentity`.
- `SignedEntry` with the six entry kinds (`Hello`, `Claim`, `Progress`, `HandOff`, `Release`, `Heartbeat`), signed
  over canonical JSON; `WorkLedger` (append, merge as a verified union by cell and sequence, who is working on
  what, open claims, the path of a claim, cursor and sync); `JsonLedgerStore`.
- `ICellNetwork` as designed and `InProcessCellNetwork` (channels, at least once, idempotent by cell and sequence).
- The `creatura cell` command: `init`, `whoami`, `claim`, `progress`, `release`, `handoff`, `heartbeat`, `ledger`.
- `CellsPurityTest`: `Creatura.Cells` and `Creatura.Cli` reference nothing but the BCL and Creatura.

## 0.1.0 (2026-10-28)

The first version: the framework extracted from the private research repository where it was first worked out,
following its design notes, with golden traces as the gate for every step. Nothing in this release was
built from scratch; every part has a predecessor in that repository, and every trace the research
designs produced before the extraction is reproduced byte for byte.

### The core (`src/Creatura`, no package references)
- One loop, `ReferenceLoop`, with `IRegulator` as the pluggable design: error signal, what error buys,
  option shaping, distrust policy, decision kind, gate, memory, stop and consolidation, every slot defaulted.
- Five regulators: `ColonyRegulator`, `SelfModelRegulator`, `HybridRegulator`, `MapKeeperRegulator`,
  `OrientingRegulator`; `Regulators.Join` with its fold laws (no shipped consumer).
- `ControlLayer` between the seat and the world: Visibility (with `IDistrust`), Gatekeeper (with the stage
  interlock), EffectCheck (the effect read from the facts, never from the report), Ledger; the
  `ActionObserved` event.
- `Identity` with `Vessel` (the hardware and the model a creatura runs in).
- `IAgentExtension` and the shipped extensions (honest failure, exploration, curiosity, development,
  dream, record carry).
- The world contract: `IWorldView` / `IWorld`, `CatalogueWorld` with `FaultRule` rows (honest failure,
  lie, changed rule, revoked), `Barrier` decorators, `OpaqueWorld` / `LearnedWorld`, `IHabitat` with
  isolation by clone.
- The record: `AgentRecord`, `IRecordStore` (in memory, JSON), snapshot and restore on every stateful type.
- The critic (`Creatura.Hotz`): `HotzAgent`, claims that cite or stay silent, the interrupt budget,
  route record and replay, `CriticExtension`.
- The orienting components (`Creatura.Orienting`): habituation by match, valence tables, affect.

### The worlds (`src/Creatura.Worlds`, no package references, no file IO)
- Six sample worlds: permit office, irrigation, livestock, flood relief, three sources, licence.
- `WorldManifest`, `WorldCatalog`, `AgentCatalog`, `ExtensionCatalog`; the controls (careless, random
  walker, oracle planner, adversary, behaviourist); `Series` for successive blocking.

### The bench (`src/Creatura.Bench`, no package references)
- `IOracle` and outcome classes (real, claimed, handed off, open), `Measures`.
- `Battery`, `Hypothesis`, `Arm`, `Row`, `BatteryReport`; legacy CSV writers byte-compatible with the
  research archive.
- `Curriculum`, `Trainer` (earns a record) and `Exam` (freezes it).
- `ExampleRecorder` and `IExampleSink`, the fine-tune seam (nothing consumes it yet).

### The seats (`src/Creatura.Seats`, no package references)
- `IModelTransport` with Ollama, OpenAI-compatible and Azure Foundry transports; `ModelClient`,
  `ModelPolicy`, `ModelJudge`; `ModelCatalog` from an optional `models.json`; keys by environment-variable
  name only; `SeatResolver` for the bench.

### The host (`src/Creatura.Hosts.AgentFramework`, the only package reference: `Microsoft.Agents.AI.OpenAI` 1.25.0)
- `ControlLayerMiddleware`, `ToolFilteringChatClient`, `ControlledAgent`, `ChatClientPolicy`.
- A package-free `FakeHost` in the tests reproduces this host's goldens byte for byte; `CorePurityTest`
  keeps the other four projects free of packages.

### Tests and samples
- `Creatura.Tests` (513) and `Creatura.Hosts.AgentFramework.Tests` (16), 116 golden traces, all offline.
- `samples/QuickStart` and `samples/AgentFrameworkHost`, both offline; `docs/anatomy.html`.

### Known limits
- Model seats are wired but were not run against a live model in this release.
- `Hosts.SemanticKernel` is reserved as a documented shape, not built.
- Building beside the research repository needs both checkouts side by side; no NuGet package is published.
