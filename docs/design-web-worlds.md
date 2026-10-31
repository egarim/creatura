# Design: web worlds, where a persona uses a real app and the app's data is the judge

**Status:** design note, with phases 1, 2a, 2b and 2c (the summary and judge seats), persona memory and a design axis built (below). Written 2026-10-31. Nothing here has been run against a real
model or a real app yet; what was run is the bundled demo, offline, in a real Chromium.

**Built:** phase 1, `src/Creatura.Worlds.Web` (`WebWorld`, the observation, the tools, the declarative check, the
barriers, the two seats, the demo), `creatura ux run` and `tests/Creatura.Worlds.Web.Tests`; phase 2a, the matrix, the
report, compare and the gate (`UxMatrix`, `UxReport`, `UxComparison`, `UxGate`, `creatura ux matrix | compare | gate`);
phase 2b, sign-in once per account with the session kept and reused, sessionStorage put back (`UxSessions`), and a
Singular adapter template (`samples/ux/singular-starter.adapter.json`, not run against an app); persona memory and the
design axis (`UxMemory`, `UxDesigns`, `--designs`, `--memory`); phase 2c's judge and summary writer (`UxJudge`,
`UxSummary`, `creatura ux judge`). Vision on the triggers and the stuck triggers as an alarm are still a design.

## The problem

Singular Framework's private repository has **uxkit**, about 1,500 lines of Python in which synthetic testers use an
app through Playwright. Each tester is a *persona* (how a person behaves, never what the app looks like) on an
*environment* (a phone, tablet or desktop viewport), given a *task* in the words of what the person knows and wants
("you must not forget to buy milk"), never the names of screens. It was run on the PhotoSet and Cosmos apps. Its
rules, which this note keeps:

- **The model sees what a person sees.** Each step it gets only the visible text and a numbered list of the controls
  on screen (interactive roles and `cursor:pointer` leaves, those in the viewport first) and answers one action:
  click N, type N "text", choose N option, back, wait, or give up.
- **Done is the app's data, not the page and not the model.** The app supplies a check that reads its own data.
- **Models by role.** A fast model decides each step; a stronger one is asked only when the tester is stuck (the same
  action repeated, no change after two steps, confidence under 0.4, an unexpected screen, a control it cannot find);
  a vision model only on those triggers; a summary writer; and a judge that reads the gap between "the model believed
  it was done" and "the data says done". All over an OpenAI-compatible endpoint.
- **An app supplies an adapter:** personas, environments, tasks (id, account, prompt), limits (steps, cost,
  escalations, the model for each role, a panel of tester models), and hooks: `url(task)`, `sign_in(page, account)`
  once per account with the browser's storage state kept and reused (and sessionStorage put back, because the
  Singular starter keeps its Keycloak refresh token there), `check(task) -> (done, detail)`, `reset(task)`.
- **It reports per cell** (task × persona × environment × tester), compares two runs cell by cell, and gates CI on
  thresholds.

Most of uxkit's engine is already in Creatura, under other names. `IWorld` separates what a tool *reports* from what
the fact registry *says*, and the control layer turns the difference into a verdict (landed, honest failure, lie).
The stuck alarm, the seats over an OpenAI-compatible transport, the escalation to a stronger seat, budgets, traces
and the replay format are the rest. So uxkit does not need porting as a program. It needs one new kind of world: a
**web world**, in which the world is a real app in a browser.

A web world can also do what uxkit cannot: **put a barrier between the browser and the app** with Playwright routing.
A save that returns 200 and drops the write, a slow response, a failure, later a button that moves. Then the
question changes from "can a person do this?" to "does the design notice when the app lies?", which is the question
every other Creatura world asks.

## The mapping

| uxkit | Creatura | in a web world |
|---|---|---|
| the page | the fact registry (`Facts`) | `page:<path>`, plus the task's done fact (`done:<task>`) while the app's data says so. The page's words are not facts: they are what the page *reports* |
| the controls on screen | the tools this tick (`AvailableTools`) | one tool per control, rebuilt after every action, plus `back` and `wait` |
| an action | `Act(toolId, payload)` | click, fill or select on the element the persona saw; the payload is the text or the option |
| what the page shows after it | the report (`ActionOutcome`) | succeeded unless the action failed or the page now shows error text the adapter names; the observation is one line of what changed |
| `check(task)` | `EffectsLanded` and the goal | a control the task names as a *commit* (a save, a send) promises the done fact, and only the data grants it. A commit the page took and the data does not show is judged a **lie** |
| the persona and the model | the seat (`IPolicy`) | `PersonaPolicy`: the persona and the task in the system prompt, the screen in the user prompt, one action back |
| escalation when stuck | the stronger seat, and the stuck alarm | phase 1: the persona seat re-asks a stronger model on uxkit's triggers it can read from one answer. Phase 2: the same triggers as a regulator's alarm, so a design can veto, hand off or escalate |
| limits | the tick budget and the seat's cost | `limits.steps` is `maxTicks`; `limits.cost` stops the seat; `limits.escalations` caps calls to the stronger seat |
| "the model believed it was done" | the persona's `done` | recorded with its tick, never acted on: the run ends when the data says done, or when the budget runs out |
| a cell | one run | `UxCell`: the data's verdict, what the page said, what the model believed, ticks, lies caught, escalations, cost, barrier fires |

**Ids and numbers.** The persona reads numbers (1, 2, 3, viewport first, as uxkit). The control layer reads ids. An
id is the control's role and visible name, slugged (`button-save`, `textbox-note`; a second `button-save` is
`button-save-2`), so the same control keeps its id when a banner appears above it and its number changes. That
matters because the control layer distrusts a tool by id: a Save button that lied stays distrusted after a re-render.
Within a tick each control has exactly one id and one number; the world acts on the element it showed (each is stamped
with `data-creatura-ref` when the page is read), not on whatever matches a selector afterwards.

**Why only commits promise anything.** A click on a link, a typed letter, a chosen option have effects only the page
can show, and the page is what is under test. So they promise nothing (they have landed when they went through), and
the one promise in a web world is the one the app's data can keep. This is uxkit's rule, said in Creatura's terms.
It also makes a commit's verdict exact: the page took the click and said nothing went wrong, and the data does not
show it.

**What the verdict cannot tell apart, yet.** A commit judged a lie can be a dropped write (a lie) or a write of the
wrong thing (the persona typed "milk" where the check wants "buy milk"). Both are "reported success, the promise did
not land". Phase 2 adds a second check per task, `wrote` (did any write arrive?), so the trace can say which.

## The adapter

One JSON file, read the way `models.json` is (camelCase, comments and trailing commas allowed). The demo's
(`creatura ux adapter` prints it):

```jsonc
{
  "app": "notes-demo",
  "baseUrl": "http://127.0.0.1/",
  "personas": [ { "id": "hurried", "description": "You are in a hurry. You read little and press the first thing that looks right." } ],
  "environments": [ { "id": "phone", "width": 390, "height": 844, "mobile": true },
                    { "id": "desktop", "width": 1280, "height": 800 } ],
  "tasks": [ {
    "id": "remember-milk",
    "prompt": "You must not forget to buy milk. Write \"buy milk\" down in this app so it is kept.",
    "start": "/",
    "commits": [ "Save" ],
    "check": { "method": "GET", "url": "/api/notes", "path": "items", "contains": { "text": "buy milk" }, "ignoreCase": true },
    "reset": { "method": "POST", "url": "/api/reset" }
  } ],
  "limits": { "steps": 12, "escalations": 2, "models": { "decide": "deepseek-chat", "escalate": "deepseek-reasoner" } },
  "barriers": [ { "id": "save-lies", "kind": "lie", "method": "POST", "url": "**/api/notes" } ],
  "errorText": [ "Error:", "Write something first" ]
}
```

- **The check is declarative first:** one HTTP request (method, url relative to `baseUrl`, optional JSON body and
  headers; a header value `env:NAME` is read from the environment at call time, so a token never sits in the file) and
  exactly one assertion at a dot path: `exists`, `equals`, `contains` (an array holds an element whose fields include
  these, or a string holds this text), `count` or `minCount`. The request is made by the harness with its own
  `HttpClient`, never through the browser, so it never passes through a barrier.
- **The code hook is the escape hatch:** `IUxHooks` (`CheckAsync`, `ResetAsync`, `SignInAsync`), passed in
  `WebWorldOptions.Hooks`, for a check a request cannot say (two calls, a computed answer, a database read).
- **`commits`** names the controls that write, by visible name. It is the adapter's knowledge of its screens, and it
  is never shown to the persona. The task's `prompt` never names a screen or a button; a test checks the demo keeps
  to that.
- **`errorText`** is the page's own way of saying "that failed". New text that contains one of these after an action
  makes the report a failure, so a save the page refuses is an honest failure, not a lie.
- **`limits.models`** names the seats by role from the model catalog (`decide`, `escalate`; phase 2 adds `vision`,
  `summary`, `judge`) and **`limits.testers`** the panel a matrix runs when none is named. **`gate`** holds the CI
  thresholds (below).

## Sign-in and session reuse (phase 2b, built)

uxkit signs each account in once and reuses the browser's storage state. A web world does the same per account
(`UxSessions`):

- **Declared sign-in.** The adapter lists `accounts` (`user` and `password` as `env:NAME`, read at sign-in; a test
  tenant may write literals) and one `signIn`: a `start` page, `steps` in the offline script's words (`type Username or
  email: {user}`, `type Password: {password}`, `click Sign In`), and `signedInText`, the text only a signed-in page
  shows. Each step waits for its control to appear, so a redirect to an identity provider and back is followed. The
  hook `IUxHooks.SignInAsync` replaces the steps when an app needs code. An error names the step as written, with
  `{password}` unfilled; the password is in no message, trace or file.
- **Once per account.** The first task that names the account signs it in on a context of its own. Playwright's storage
  state (cookies, localStorage) is kept, with the signed-in origin's sessionStorage. Every later context for the account
  opens with that state. A matrix shares one store across its cells, and its report counts sign-ins and reuses.
- **sessionStorage put back.** Playwright's storage state leaves sessionStorage out, and the Singular starter keeps its
  Keycloak refresh token there. So an init script puts the kept items back before the app's own scripts run, on the
  kept origin only, once per tab (a marker key stops it from undoing a later sign-out). `signIn.sessionStorage: false`
  turns this off. The demo shows why it matters: without it the session does not carry over, and the cell says so.
- **Expiry.** A world opened with a kept session must show `signedInText` (read again for up to 5 s as the page loads).
  If it does not, the session is dropped, the account signs in again, and the world opens again, once. A second miss is
  an error that says the session did not carry over.
- **Kept between runs.** `--sessions <dir>` keeps each account's session in an owner-only file
  (`<account>.session.json`, mode 600 on Unix), which the next run reuses until the app stops accepting it. The files
  hold tokens: keep the folder out of the repository (`runs/` is ignored).

The bundled demo signs in the way the starter does. `/login` keeps the token from `/api/login` in sessionStorage only,
and `/private` is a list read and written with it that shows "Please sign in" without it. The demo's task
`private-bread` signs in as `ana`.

## Persona memory and the design axis (built)

The owner asked whether a persona can run under different control mechanisms and keep what it felt and thought
between tests. Both are built, on what the Core already has.

**The design axis.** A matrix takes `--designs plain,bach,levin,peterson,orienting,hybrid` (or `UxMatrixSpec.Designs`),
and every task × persona × environment × tester cell runs once per design. The design is part of the cell's key, so
compare never joins a Bach cell to a plain one. The report's totals are split by design (`ByDesign`, a table in
`report.md`), so the control mechanisms are compared on exactly the same cells. The persona is the seat and the design
is the loop around it: the same hurried persona under Bach and under the map-keeper is two different runs.

**Persona memory.** The Core's record store (`IRecordStore`, `JsonRecordStore` under `--records <dir>`) keeps one
record per persona, tester and design (`UxMemory.Key`: the same persona played by another model, or under another
design, is another mind). What it holds:

- **The design's own carry,** as the bench's trainer keeps it: Bach's self-model, skill beliefs, prediction record and
  stage (through `RecordCarryExtension`); the orienting agent's resting inhibition (the nearest thing in the code to a
  mood); the map-keeper's territory; the hybrid's cell. The plain loop keeps only its count of cases.
- **The persona's lessons:** one line per cell, written by the harness from the cell's verdicts, never by the model
  ("`remember-milk` on your phone: the app said it was saved, but it was not kept; you noticed"). The newest 8 are
  shown to a model persona before the screen, as "what you remember from using this app before".
- **Not kept:** distrust (the Core's rule: a new case starts trusting) and the model's own reasoning text.

Two modes. `--memory carry` loads each cell's record and saves it after, so a persona's later cells know what its
earlier ones learned, in the matrix's order. `--memory read` starts every cell from the records as they are on disk
and saves nothing: a fixed, experienced persona. A tester who remembers the app is no longer a first-time user, so a
report says which mode it ran in, and two reports compare only when they started from the same records. The usual way
to use it: train a persona with `carry` on one matrix, then run `read` matrices against it.

**Lies told and lies caught.** Building this showed the bench's lie counter reads the designs' words ("contradiction",
"effect missing"). The orienting agent writes "punishment … orienting", so its catches went uncounted, and the report
said it missed lies it had caught. A web cell now counts by the world's own truth. A lie is **told** when a commit the
page took is not in the data (`WebWorld.Lies`, placed on the loop's tick through `ITickedWorld`). It is **caught** when
the loop blocked that tool on that tick, whatever the design's words (`WebLie.CaughtIn`). "A lie not caught" is a lie
told and not blocked. The same work found a world bug from phase 1: a save pressed again on a page still showing
"Write something first" was read as a success, because the error text was not new. After a commit, any error text on
screen now means the page refused it.

## Viewports

An environment is a viewport (`width`, `height`) and `mobile` (Playwright's `IsMobile` and `HasTouch`), so the same
task is run on a phone layout and a desktop one. The observation is read inside the viewport first: controls on
screen come before controls below it, which are marked "below, scroll to reach". Phase 2: a `scroll` action, a
device scale factor and a user agent per environment, and vision on the triggers.

## Barriers

A barrier is a Playwright route on the world's browser context: requests whose method and url glob match are answered
by the barrier instead of the app.

| kind | what the browser gets | what the app gets | what a design should see |
|---|---|---|---|
| `lie` | the status and body the barrier wrote (200, `{"ok":true}`) | nothing | the page says saved; the data does not show it: a lie |
| `fail` | an error status | nothing | the page says it failed (its error text): an honest failure |
| `slow` | the app's own answer, `delayMs` late | the request, late | a commit that looks like a lie when the world reads the data, and lands later: does the design wait, retry, or condemn? |

`times` makes a barrier fire only so many times (a lie once, then the truth). Phase 2 adds barriers on the page
rather than the network: a button that moves between ticks, a control that is covered, a field that drops a letter.
The demo app can also lie by itself (`DemoApp.DropWrites`), with no barrier: the world must catch the app's own lies,
not only the ones it injects, and the tests check both.

## Cost limits

`limits.cost` is a cap in the catalog's currency for one cell; the persona seat stops (no action, "cost limit
reached") once its calls have cost that much by the catalog's prices, and a seat whose price is unknown counts as
unknown, not free. `limits.steps` caps the ticks and `limits.escalations` the calls to the stronger seat. The command
never picks a model on its own: an adapter run needs `--seat model:<name>`, and it says the price is per step.

A matrix has its own cap (`--max-cost`, else the gate's `maxCost`): no cell starts once the cells before it have spent
that much, and the report's total cost is what the gate's `maxCost` checks. A total with an unpriced model in it is
unknown, and breaks a `maxCost`.

## Report, compare, gate (phase 2a, built)

uxkit's three outputs. Each cell carries three verdicts side by side, and the report counts the gaps between them
apart, because they mean different things:

| gap | the cell | what it is about |
|---|---|---|
| believed done, not done | the persona said `done` and the data says not | the model (uxkit's judge reads this) |
| the page said done, not done | a commit went through on the page and the data says not | the app, or a barrier |
| a lie not caught | the page said done, the data says not, and the run caught no lie | the design: it did not notice |

With a lie barrier on, "the page said done, not done" is every cell where the persona saved, by construction; the one
a design is judged on is the third.

- **The matrix** (`UxMatrix`, `creatura ux matrix`) runs every task × persona × environment × tester, one cell at a
  time, each in a fresh browser context, in that order. Testers are named as for a run (`script:<path>`, `script:demo`,
  `model:<name>`), on the command line or in the adapter's `limits.testers`; every tester is checked (the script
  exists, the model is in the catalog, its key is set) before the browser starts. A cell that cannot run (the app does
  not open) is recorded with its error and the matrix goes on. A cost cap (`--max-cost`, else the gate's `maxCost`)
  stops the matrix starting cells once that much is spent; the rest are recorded as not run.
- **The report** (`UxReport.Write`) is a folder: `report.json` (the cells, the totals, the design, the barriers and the
  adapter's gate at the time: what compare and gate read), `cells.csv` (one row per cell, through the bench's
  `AppendOnlyCsv`), `report.md` (a table for a person) and `traces/`, each cell's run as JSON lines in the goldens'
  format. The bench's `Row` was not used: its columns are the batteries' and its legacy CSV shapes are pinned byte for
  byte, and the web verdicts have no column there.
- **Compare** (`UxComparison`, `creatura ux compare <before> <after>`) joins two reports on the cell's key (task,
  persona, environment, tester; the same cell on another tester is another cell) and lists what regressed (done before,
  not after), what was fixed, new gaps, cells done in more ticks, and cells only in one of them.
- **The gate** (`UxGate`, the adapter's `gate`, `creatura ux gate <report>` or `ux matrix --gate`) holds a report to
  thresholds and exits 1 when one breaks, naming each: `minDone` (a share of the cells that ran), `maxBelievedNotDone`,
  `maxPageSaidNotDone`, `maxUncaughtLies`, `maxCost` (an unknown cost breaks it), `maxErrors` (0 unless set: a cell that
  did not run is no pass) and `maxRegressions` (0 unless set, checked only against a `--baseline`). A report where no
  cell ran never passes. Flags override the report's gate (`--min-done 0` for a run with a lie barrier on).

The demo's gate is `minDone 1.0, maxBelievedNotDone 0, maxUncaughtLies 0, maxCost 0.5`. In CI:

```bash
creatura ux matrix --demo --out runs/ux/main --gate                       # 4 cells, all done: exit 0
creatura ux matrix --demo --barriers save-lies --design bach --out runs/ux/lie
creatura ux gate runs/ux/lie --min-done 0 --max-regressions 99 --baseline runs/ux/main   # every lie caught: exit 0
```

The summary writer and the judge are phase 2c, below.

## The judge and the summary writer (phase 2c, built)

uxkit's last two model roles. Both read the report, never the app.

**The judge** (`UxJudge`) reads one cell and answers from a closed set of findings, so judgements count and compare:

| finding | when |
|---|---|
| `app-lied` | the page said a save went through and the data does not show it |
| `persona-overclaimed` | the persona said it was done and the data says not |
| `design-missed-lie` | the world told a lie the run did not block |
| `not-done` | the data does not show the task done |
| `not-run` | the cell could not run |
| `friction` | wrong turns, repeated actions, escalations, a control the persona could not find |
| `ok` | done, and nothing above |

The first five follow from the cell's verdicts, and **the rules** (`UxJudge.Rules`, no model) always find them. A
**model judge** (`--judge model:<name>`) is shown the verdicts and the cell's trace (at most 60 rows), never the
screen. It answers `{findings, why, ticks}`: it can add `friction`, which only a reading of the trace can find, and it
explains with tick numbers. It is **held to the rules** (`UxJudge.Hold`): a finding the verdicts require and it left
out is put back; one they deny (`ok` on a cell not done, `app-lied` where the page said nothing) is struck; a name
outside the set is dropped. Each is recorded as a correction on the judgement, and the summary lists them. So a model
judge can explain a cell but cannot change its verdict, and how often the rules correct it is a measure of the judge.
A judge that fails or answers nothing readable falls back to the rules, and its judgement says so. A cell that did not
run is never sent.

**The summary writer** (`UxSummary`) writes the facts first, always, by code: the totals, the totals by design, the
findings counted, each cell with findings once (its findings, why, ticks), how many cells were ok, and the rules'
corrections. A model (`--summary model:<name>`) may add one paragraph for the app's owner beneath them, under a heading
that names it and says the facts above are the record. It never replaces a fact, and a failed call leaves the facts
alone with a line saying it failed.

A matrix is always judged, by the rules unless a model is named, and leaves `judgements.json` and `summary.md` beside
the report. `report.json` keeps the judgements, the summary and what judging cost, and `report.md` gains a findings
column. `creatura ux judge <report> [--judge model:<name>] [--summary model:<name>]` judges a report again: with
another judge, or after the fact, reading each cell's trace back from `traces/`.

## Pre-registered tests, written before the code

| id | claim | falsifier | phase 1 |
|---|---|---|---|
| W1 | the observation is the visible text and the controls on screen: labelled fields by their label, `cursor:pointer` leaves included, hidden text left out, no ids or facts in what the persona reads | a hidden text shown, a control missing, an id in the persona's prompt | holds (`ObservationTests`, `BrowserTests`) |
| W2 | a control keeps its id across ticks when its number changes | a different id for the same role and name | holds |
| W3 | the check is the only judge: the goal is reached only when the app's data says so | a goal reached with the data not done | holds |
| W4 | a save the lie barrier answers is judged a lie, the run's goal is not reached, and the page's "Saved." is recorded beside the data's "not done" | landed, or the goal reached, or the gap not reported | holds, with the plain loop and with Bach |
| W5 | a save the page shows as failed is an honest failure, not a lie | judged a lie | holds |
| W6 | the app's own dropped write is caught the same way as the injected one | missed | holds |
| W7 | the offline demo runs to the goal in two ticks, on the phone and the desktop viewport, with the bundled script, and with a stub model that reads only the screen | anything else | holds |
| W8 | the persona escalates only on a trigger and within the budget; a cost cap stops the seat | an escalation without a trigger or over the budget, a call after the cap | holds (stub transport) |
| W9 | on a real app (a Singular starter) with a real model, the matrix's verdicts agree with a person checking the same cells | disagreement on more than one cell in twenty | phase 2b |
| W10 | with the lie barrier on, every design catches the lie at the commit in every cell where the persona reaches it, and the plain persona with no control layer would have reported done | a missed lie | phase 2c |
| W11 | the matrix runs every task × persona × environment × tester named, in order; a cell that cannot run is recorded and the rest still run; the cost cap stops new cells | a cell missing, out of order, or the matrix stopped by one cell | holds (the cap: by its code only, with no paid seat) |
| W12 | a report written and read back gives the same cells and totals; the CSV has one row per cell | anything else | holds |
| W13 | compare joins on task, persona, environment and tester, and names every regression, fix, new gap and slower cell | a cell joined to another tester's, or a change not listed | holds |
| W15 | a matrix signs each account in once and reuses the session in every other cell for it | a second sign-in with the session still good | holds |
| W16 | sessionStorage is put back: with it the private task opens signed in; without it the session does not carry over, and the cell says so | anything else | holds |
| W17 | an expired session is dropped and the account signs in again, once; a session kept on disk is owner-only, holds no password, and the next run signs in zero times | anything else | holds |
| W18 | the design is an axis: every cell runs once per design named, the key holds the design, the report's totals are split by design | anything else | holds |
| W19 | with memory carried, a persona's cells start from the record its earlier cells left (cases, the design's carry, lessons); read starts every cell from the same record and saves nothing | anything else | holds |
| W20 | lies are counted by the world: told when a commit the page took is not in the data, caught when the loop blocked that tool on that tick; under `save-lies` plain, Bach, the map-keeper and the orienting agent each catch the one lie told | a lie miscounted, or a design's catch missed for its words | holds |
| W21 | the rules find every finding the verdicts decide; a model judge is held to them (required put back, denied struck, unknown dropped, each a correction); a failed judge falls back to the rules and says so | a model judgement that contradicts the data, or a correction not recorded | holds (stub models) |
| W22 | the summary is the facts by code, each cell once; a model's paragraph comes after them, named, and a failed call leaves the facts | a fact missing or changed by a model | holds (stub models) |
| W14 | the gate exits 1 naming every threshold broken, 0 when none is; with the lie barrier the demo breaks `minDone`, and holds `maxUncaughtLies 0` under Bach | anything else | holds |

## Results (phase 1, 2026-10-31)

All of it run in this repository's test suite, offline, with Chromium (Playwright 1.63) in a cloud container:
`Creatura.Worlds.Web.Tests` has 38 tests (26 need no browser, 12 do), and `UxCommandTests` in the cells' suite has
13 for the command's usage paths. W1 to W8 hold. Without a Chromium the 12 browser tests are skipped with the reason
and the fix, so CI without a browser still runs the rest.

`creatura ux run --demo` (the bundled script, the plain loop, the phone viewport) reaches the goal in 2 ticks with
no lies. With `--barriers save-lies --design bach` the Bach agent marks the save a contradiction at tick 2, hands off
at tick 5 when the script has nothing left, and the cell reads: the data says NOT DONE, 1 lie caught, the page said
it was saved, `save-lies` fired once.

Not verified: any real model in the persona's seat (the prompt and the parsing were tested with a stub transport
only), any real app (no Singular starter was run), sign-in and storage-state reuse, the `slow` barrier's timing
(built, not tested: it depends on the settle time). The release's single-file `creatura` leaves Playwright's node
driver out (it cannot run from inside the file and would add about 120 MB; the file grows from 74 MB to 78 MB), so
there `creatura ux` says no browser could be started and points to the .NET tool or `dotnet run`, where it works.

## Results (phase 2a, 2026-10-31)

Run the same way, offline, in a real Chromium. W11 to W14 hold: `ReportTests` (12, no browser), `MatrixBrowserTests`
(3) and 11 more command tests in `UxCommandTests`. The demo's matrix is 4 cells (1 task × 2 personas × 2 viewports ×
the bundled script), all done in 2 ticks, and holds its gate. With `save-lies` and Bach it is 0 of 4 done, 4 lies
caught, none missed; the gate breaks on `minDone` and, against the clean run as baseline, on 4 regressions, and holds
with `--min-done 0 --max-regressions 99`. Not verified: a matrix with a model as tester, so the cost cap has stopped
nothing real yet.

## Results (phase 2b, 2026-10-31)

Run the same way, offline, in a real Chromium. W15 to W17 hold: `SessionTests` (6, browser) and `SignInAdapterTests`
(7, no browser, one of which parses the Singular template). The demo's matrix is now 8 cells (the anonymous
`remember-milk` and the signed-in `private-bread`), all done, with 1 sign-in and 3 sessions reused, and holds its gate.
A lie on the private list is caught like the public one. One test failed once when the whole solution ran on a busy
machine: the signed-in check read the page before its first request had answered. It now reads the page again for up
to 5 s, and two full runs after that passed. Not verified: the Singular template against a Singular app, a Keycloak
redirect, and a token that refreshes itself during a cell.

## Results (persona memory and the design axis, 2026-10-31)

Run the same way, offline, in a real Chromium. W18 to W20 hold: `MemoryTests` (5, no browser), `MemoryBrowserTests`
(3) and command tests for the new flags. Under `save-lies`, with plain, Bach, the map-keeper and the orienting agent
on the same 8 cells, each design catches the 2 lies told and misses none. Before the count moved to the world's
truth, the same run said the orienting agent missed both, and that the map-keeper caught 4. The 4 were two real
lies plus two refused saves the world had misread (the phase 1 bug above). With `carry`, a hurried persona under Bach
went into its second cell with one earlier case and one lesson, and its record held Bach's skill beliefs about the
controls. With `read`, two cells started from the same record and the record was unchanged after. A timeout from
Playwright (`System.TimeoutException`, not a `PlaywrightException`) crashed a cell instead of failing the action; it is
now caught everywhere, and a test fails without the fix. Not verified: a model persona reading its lessons (the prompt
is tested, a model's use of it is not), and whether carried records make any design better at a real app.

## Results (phase 2c, 2026-10-31)

Run the same way, offline. W21 and W22 hold: `JudgeTests` (7) with stub models, and command tests for `ux judge`. On
the demo under `save-lies` with plain and Bach, the rules judged all 4 cells `app-lied` and `not-done`, with every lie
caught, and wrote the summary beside the report. In the tests, a stub judge that called a lied-to cell `ok` with
`friction` was corrected to `app-lied`, `not-done`, `friction`, with three corrections recorded. Not verified: any
real model as judge or summary writer (how often the rules must correct one is the number to watch), and whether a
model finds `friction` where a person would.

## Decisions for the owner

| | decision | options | recommendation |
|---|---|---|---|
| U1 | where web worlds live | (a) a project here; (b) the framework's repository | **(a)**, done: the world is generic, and a Singular adapter is a JSON file and maybe a hooks class in the app's own repository |
| U2 | the CLI | `creatura ux` in the `creatura` tool; a separate `creatura-ux` program | **`creatura ux`**, done; if the release's single file must run it, ship Playwright's driver folder with it, or split it out then |
| U3 | escalation | in the seat (phase 1); as the stuck alarm in a regulator | **both**: the seat's triggers stay (they are about the model's answer), and the "no change after two steps" trigger moves to a regulator's alarm, where the designs differ |
| U4 | uxkit itself | keep it; retire it once phase 2 runs the PhotoSet and Cosmos tasks | **retire after W9 holds** on both apps |

## Phases

| phase | what | passes when |
|---|---|---|
| 1 | `WebWorld`, the observation, tools per control, the declarative check, the lie / fail / slow barriers, the persona and script seats, `creatura ux run`, the offline demo | W1 to W8 (done) |
| 2a | the full matrix (tasks × personas × environments × testers), the report (json, csv, markdown, traces), compare, gate | W11 to W14 (done) |
| 2b | sign-in once per account, storage state kept and reused, sessionStorage put back; a Singular template | W15 to W17 (done); W9 on PhotoSet or Cosmos is still open, with the template filled in |
| 2b+ | persona memory (records and lessons, carry or read) and the design axis | W18 to W20 (done) |
| 2c | the summary writer and the judge as seats (built: W21, W22); vision on the triggers and the stuck triggers as an alarm (not built); vision on the triggers; the stuck triggers as a regulator alarm | W10; the judge's findings agree with W9's person |
| 3 | barriers on the page (a moving button, a covered control), `scroll`, record and replay of web runs | a design's catch rate per barrier, per viewport |

## What this note does not cover

- **Load and performance testing.** One persona at a time, at a person's pace.
- **Visual and accessibility audits.** The observation reads roles and names, so a control with no name shows as
  "(no label)", which is a finding, but no audit is run.
- **Apps behind a login the harness cannot script** (captchas, one-time codes sent to a phone).
- **Writing the tasks.** A person writes them, as in uxkit. Generating tasks from an app is its own question.
- **Production.** A web world runs against a test tenant with a reset; never against people's data.
