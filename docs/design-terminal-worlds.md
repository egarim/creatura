# Design: terminal worlds, driven through Sentiora and judged outside the PTY

**Status:** implementation note, v1 built (2026-11-27). `src/Creatura.Worlds.Terminal` drives a terminal application
through Sentiora's JSON-RPC/PTY runtime, with a real driver and a scripted `SimulatedTerminal`. The offline suite
(`Creatura.Worlds.Terminal.Tests`, 18 tests) and one env-gated live test against the real runtime
(`C:\Users\joche\source\repos\st-failspawn`, node v24.18.1, win32) pass on this machine. There is **no
`WorldManifest`** in v1 (the `Creatura.Worlds.Web` precedent): a host is handed the world instance, and
`creatura run --world terminal` is not reachable. The tests below were written with the implementation, not
pre-registered before it; nothing here is benchmark evidence.

## The problem

The terminal is the last place where an agent has to assume that output describes reality. A TUI says "Saved", a
prompt echoes a command, a spinner stops: none of that is proof that the application did the thing. Sentiora Terminal
exists to replace that assumption with perception and action over a real PTY — *"Playwright for terminals"* — and it is
deliberate that it supplies **perception and action**, while the caller supplies goals and **independent
verification** (`st-agent/README.md:51-54`, `docs/integrations/agent-client.md`). This world is that caller.

A world is not a program that drives a terminal. It is the contract a persona acts through and a judge reads: tools in,
an honest `ActionOutcome` out, a fact registry that holds only what can be proved, and a verdict per action. The
terminal's screen is what a person would see; it is not evidence. The evidence is the application's own outcome, read
**outside** the terminal by a check the host declares.

## The mapping

| A person at a terminal | Creatura | In this world |
|---|---|---|
| what is on screen | the observation | `TerminalObservation.Screen` — the report, cut to `MaxText` |
| the cursor, size, whether the child lives | objective state | `TerminalObservation` (`State`, `Running`, `ExitCode`, `Cols`/`Rows`, `Revision`, `Cursor`, `Pid`) |
| reading the screen | a tool | `read-screen` (kind `observe`, promises nothing) |
| typing text / a line | tools | `type`, `line` (payload; `line` presses Enter) |
| pressing a key | a tool | `key` (payload is one runtime key name) |
| watching for a prompt | a tool | `wait-for-text` (bounded; succeeds only after a fresh read shows the text) |
| quitting | a tool | `close` (promises `session-closed`) |
| "it is done" | the task's independent check | `commit` (promises `done:<task>` only while the check agrees) |
| a person's approval prompt | a handoff | `TerminalHandoff` names it; input tools refuse and say so |
| the goal | a predicate over facts | the host's `AgentTask.Goal` over `Facts`; the world computes none |
| distrust | the control layer's | the world has none and persists none |

## The report and the registry

The runtime's own `Observation` doc says a consumer that wants to treat the state and the screen as different kinds
of evidence "must split them itself" (`st-agent/clients/dotnet/Sentiora.Client/Protocol.cs:40-45`). This world splits
them at its boundary: `TerminalObservation` keeps both, and `Facts` takes only the objective half.

| fact | granted by | why it is provable |
|---|---|---|
| `session:<id>` | a read | the runtime reports the session and answers for it |
| `state:<state>` (`state:running`, `state:exited`, …) | a read | the runtime's objective state |
| `revision:<n>` | a read | the runtime's own counter, re-read each action |
| `size:<cols>x<rows>` | a read | the runtime's geometry |
| `exited:<code>` | a read | the child's exit code, not a screen scrape |
| `session-closed` | `close` | the runtime confirmed the close (`terminal.close` returns `true`) |
| `done:<task>` | `commit` | **only** the injected `ITerminalCheck` said so |

The screen has no fact name. There is deliberately no `screen:contains:` or `text-seen:` entry: a screen-derived
fact would be the runtime's word laundered into the registry, and the terminal could tell it. `wait-for-text` therefore
promises nothing; it succeeds only when the runtime's wait returns **and** a fresh `observe` still shows the text, so
a driver that claims the text while the screen lacks it makes the wait fail (T5). A tool that promises nothing has
landed, by the house rule, which is why `EffectsLanded` on it is true only because its report was checked first.

`commit` is the one action that does not need a live session: the check is read outside the terminal, so a task whose
application writes its result and exits can still be certified. `commit` with no check fails honestly; it never
guesses.

An input's success is a weaker claim than a write's, and the world says only the weaker one: the runtime accepted the
input and the screen was re-read. A terminal cannot prove that a keystroke changed the application (input may be
masked, ignored, or consumed by a menu), so no input tool promises that the application's state changed. What the
application did is the check's to prove, at `commit`.

## Ids and bounds

Tool ids are stable, lower-case and `[a-z0-9-]`-only, because they become function names in the Semantic Kernel host
(after which punctuation collapses and collisions throw):

| id | kind | payload | requires | grants |
|---|---|---|---|---|
| `read-screen` | observe | — | — | — |
| `type` | type | the text | — | — |
| `line` | line | the line, then Enter | — | — |
| `key` | key | one key name | — | — |
| `wait-for-text` | wait | the text | — | — |
| `close` | close | — | — | `session-closed` |
| `commit` | commit | — | the task's `CommitRequires` | `done:<task>` |

Everything is bounded in `TerminalWorldOptions` (the `WebWorldOptions` shape):

| bound | default | effect |
|---|---|---|
| `ActionTimeoutMs` | 5000 | one read or input, and the runtime client's own request timeout |
| `SettleMs` | 300 | how long the world waits for the revision to move after input before reading the screen as it is |
| `WaitTimeoutMs` | 20000 | a `wait-for-text`; its failure carries the runtime's words and the text it waited for |
| `MaxText` | 2000 | the screen the persona keeps, cut with `…`; the wait check still reads the full screen |
| `Keys` | the runtime's nine | the key allow-list; a name outside it is refused before anything is sent |
| `ApprovalPrompts` | six common prompts | screen text that means a person's turn; empty disables the handoff |

`Problems()` names every unusable value at once and `OpenAsync` refuses to start on any of them (T6).

## The driver seam

`ITerminalDriver` is the whole surface: `ObserveAsync`, `TypeAsync`, `SendLineAsync`, `SendKeyAsync`, `WaitAsync`,
`WaitForTextAsync`, `CloseAsync`, `ForcedShutdown`, `RecycleAsync`. It has two implementations and the world has one
code path:

- **`SentioraTerminalDriver`** wraps one `SentioraClient` (one runtime process) and one `SessionHandle`. It owns what
  it opened; a client or session injected through `TerminalWorldOptions` is shared and left alone. It recycles a
  host it owns.
- **`SimulatedTerminal`** is a scripted screen: input steps (line, text, key) produce a new screen after a delay and
  can exit the child; a `Redraw` step changes the screen with no input after its delay. Its clock starts at the first
  observation, so the opening screen is always read before a redraw. It throws the same
  `SentioraException("text … not seen within Nms", "terminal.stream")` the real client does, which is why the same
  world code produces the same honest failure over both.

The simulation is in the package, not the test project, because it is a deliverable in its own right (the Sentiora
roadmap calls for a real and a simulated terminal world) and because a host can use it to try a task offline.

The real driver sends a key as the runtime's own sequence through `terminal.type` (`TerminalKeys`, from
`src/server.js`'s `keys` table). The runtime has a `terminal.key` method, but the vendored C# client exposes no call
for it; since `terminal.key` is the same `Session.input` with that sequence, behaviour is the same and the vendored
client is not modified. If the client gains a key method, `SendKeyAsync` is one line.

## Sessions and lifecycle

`TerminalWorld.OpenAsync` validates the options, then either uses the injected driver, or starts a client and creates
one session (`options.Create`), and reads the terminal once. One world is one driver, one runtime process, one session.

`DisposeAsync` asks the driver to dispose: it closes the session it owns and then disposes the client it owns (which
closes the host's stdin, waits, and kills the tree only if the host will not exit). A forced kill is **not hidden**:
`TerminalWorld.ForcedShutdown` reports it and `Shutdown` carries the sentence. The live test asserts
`ForcedShutdown == false` after a clean close.

A `SentioraException` or timeout that means the host stopped answering (`terminal.* did not answer within Nms`,
`runtime exited before answering`, `client is closed`) is a failed episode, not a live world: the driver is asked to
recycle (restart the runtime and create the session again), and the failed `ActionOutcome` says whether it did. When
the host cannot be restarted (or the client was injected and is not the world's to restart), the world sets `Failure`
and every later action says so in the runtime's words, rather than pretending to keep working. A bounded wait's
timeout (`text … not seen within Nms`) and a stale-revision retry are deliberately **not** fatal: they are the
runtime working as specified.

## Cancellation and the human handoff

`ActAsync`, `OpenAsync`, `RefreshAsync` and `DisposeAsync` take a `CancellationToken`; the synchronous `Act` cannot
(Core's `IWorld` has no token), so it runs the async path on the thread pool exactly as `WebWorld` does. Inside an
act every runtime call also has its own wall-clock bound, because the protocol has no cancellation: `terminal.wait`
and `terminal.stream` are bounded by `WaitTimeoutMs`, everything else by `ActionTimeoutMs`. A caller's cancellation
propagates; an action's own timeout becomes a failure.

A world never hands off by itself. It supplies the condition: when the screen shows one of `ApprovalPrompts`, a fresh
read sets `TerminalHandoff` (the loop tick from `BeginTick` and the prompt text). Reading, waiting and closing still
work; `type`, `line` and `key` refuse with *"a person is needed: the screen shows \"…\", and this world will not answer
it for them"*. A seat or runner reads `AwaitingHuman`/`Handoff` and escalates; nothing is guessed on a person's behalf.

## Pre-registered tests

| id | claim | falsifier | status |
|---|---|---|---|
| T1 | a menu is driven by a line and redrawn; `read-screen` reports the next menu and not the old one | the old screen read, the line not reaching the terminal | holds (`MenuTests`) |
| T2 | a screen that changes with no input is found by `wait-for-text` | a wait that never sees it, or the redraw before the opening read | holds (`RedrawTests`) |
| T3 | a wait past its bound fails honestly and names the text and the bound | a success, a hang, or a failure that does not say what it waited for | holds (`DelayedResponseTests`) |
| T4 | an exited child is reported with its exit code by a read and a close; sending to it fails; a commit after it fails honestly | a missing exit code, a commit that lands, a crash | holds (`ProcessExitTests`) |
| T5 | the world cannot be fooled: a driver claiming a text the screen lacks fails the wait, and a screen saying success cannot make a commit land unless the check agrees; a commit the check backs does land | a wait that succeeds on the driver's word, a commit that lands without the check, or one that never lands | holds (`HonestyTests`) |
| T6 | the catalogue is seven stable ids, only the four text tools need a payload, only `close`/`commit` promise facts, only `commit` requires anything; bad options are all named before start and no runtime is started | a missing/extra id, a payload-less text tool, an unlisted promise, a start on bad options | holds (`CatalogueTests`) |
| T7 | the real runtime spawns the fixture, the screen is readable, a line is typed and its echo seen, and the close is clean (`ForcedShutdown == false`) | a hang, a wrong echo, a forced shutdown | holds (`LiveTerminalTests`, real PTY) |
| T8 | an approval prompt on screen is named and not answered, and reading still works | input sent to the prompt, or a read refused | holds (`HonestyTests`) |

## Results

`dotnet test tests/Creatura.Worlds.Terminal.Tests`: **19 passed, 0 failed, 0 skipped** — 18 offline (179 ms) and the
live test (5 s, including dispose). The live test ran against `C:\Users\joche\source\repos\st-failspawn` at `1c5a551`,
node v24.18.1, with the runtime's `test/fixtures/interactive.mjs` (cols 100, rows 30): `FIXTURE READY` seen through
`wait-for-text`, `echo hello` typed, `ECHO:hello` seen, `close` confirmed, no forced shutdown.

The offline results are about the world's own logic, not about a real terminal application: T1–T6 and T8 use
`SimulatedTerminal`. The real path is covered by T7 only.

## What is not verified, and what is not built

- **No manifest, no CLI.** v1 follows the Web precedent and registers nothing; `creatura run --world terminal` needs a
  `WorldManifest` in `WorldCatalog`, which would be one static `Register` call from this package (it references
  `Creatura`, not `Creatura.Worlds`, so that is a new reference and a deliberate decision, not a line to slip in).
- **No replay/recording.** The runtime can record input/output, but `CreateSessionRequest.RecordInput`/`RecordBytes`
  are not surfaced as options and the world writes no `creatura.web-replay/1` file; `docs/replay-format.md` would be
  the contract.
- **No barriers.** There is no `TerminalBarrier`; fault injection is the test-only `LyingDriver` and the simulated
  script. That is enough to prove the split, not enough for a barrier curriculum.
- **No resize.** The runtime has `terminal.resize`; the vendored client exposes no call, and no tool does.
- **Keys are sequences, not `terminal.key`.** Behaviourally the same; a runtime-side key error is therefore never
  seen, because the allow-list is checked in the world.
- **Secrets are not handled.** The world avoids echoing the payload by reporting a character count, but the payload is
  in `ActionOutcome`/`LastAct` and therefore in whatever trace a host writes. A password typed into a TUI would be
  recorded. The Web world's `Secret` treatment has no terminal analogue yet.
- **A real TUI is not exercised.** The live fixture is a line-command application; screen diffing, alt-screen and
  cursor-driven menus are only simulated. No real application was driven to a goal.
- **Typing is not verified to have landed.** An input tool reports that the runtime accepted the input and re-read the
  screen; it cannot prove the application changed, and does not claim it (see above). Only the check proves an
  outcome.
- **No seat, no loop, no `AgentTask`.** The world exposes `Current`/`Facts`/tools for a host, but nothing here runs a
  model or builds a task; the T1–T8 runs are direct `Act` calls.
- **Sharing one host across worlds is implemented but untested.** `Client`/`Session` injection exists; no test opens
  two worlds on one client.
- **`wait-for-text` promises no fact**, by design (the screen is the report); the brief's "grants only after
  re-reading" is implemented as "succeeds only after re-reading", with nothing in the registry to grant.
