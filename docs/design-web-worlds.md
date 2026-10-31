# Design: web worlds, where a persona uses a real app and the app's data is the judge

**Status:** design note, with phases 1, 2a and 2b built (below). Written 2026-10-31. Nothing here has been run against a real
model or a real app yet; what was run is the bundled demo, offline, in a real Chromium.

**Built:** phase 1, `src/Creatura.Worlds.Web` (`WebWorld`, the observation, the tools, the declarative check, the
barriers, the two seats, the demo), `creatura ux run` and `tests/Creatura.Worlds.Web.Tests`; phase 2a, the matrix, the
report, compare and the gate (`UxMatrix`, `UxReport`, `UxComparison`, `UxGate`, `creatura ux matrix | compare | gate`);
phase 2b, sign-in once per account with the session kept and reused, sessionStorage put back (`UxSessions`), and a
Singular adapter template (`samples/ux/singular-starter.adapter.json`, not run against an app). The rest is a design.

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

The summary writer and the judge (two more seats, reading only the traces and the three verdicts) are phase 2c.

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
| 2c | the summary writer and the judge as seats; vision on the triggers; the stuck triggers as a regulator alarm | W10; the judge's findings agree with W9's person |
| 3 | barriers on the page (a moving button, a covered control), `scroll`, record and replay of web runs | a design's catch rate per barrier, per viewport |

## What this note does not cover

- **Load and performance testing.** One persona at a time, at a person's pace.
- **Visual and accessibility audits.** The observation reads roles and names, so a control with no name shows as
  "(no label)", which is a finding, but no audit is run.
- **Apps behind a login the harness cannot script** (captchas, one-time codes sent to a phone).
- **Writing the tasks.** A person writes them, as in uxkit. Generating tasks from an app is its own question.
- **Production.** A web world runs against a test tenant with a reset; never against people's data.
