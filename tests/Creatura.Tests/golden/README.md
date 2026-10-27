# Golden traces

**Re-recording a golden is a decision, not a fix.** To re-record on purpose, name the files and say why in
the commit message:

```bash
CREATURA_RECORD_GOLDENS=permit-all-hybrid,flood.liar-all-hybrid dotnet test tests/Creatura.Tests --filter "FullyQualifiedName~GoldenTraceTests"
```

`CREATURA_RECORD_GOLDENS=all` re-records the whole set. (The variable was `BIOLOGICAL_AGENTS_RECORD_GOLDENS`
while the set lived in the research repository, egarim/biological-agents, until migration step 11.) Any drift
the theory reports (a trace row, a note, the goal or the tick count) must be a **named decision in the commit
message** of the commit that re-records the file: which rows moved, in which scenarios, and which design or
control-layer rule changed. A golden that moves without that sentence is a regression.

## What these are

One file per scenario, recorded on 2026-10-27 in the research repository against the loops as they were before
any step of its `design-framework.md` migration (step 0), and moved here byte for byte at step 11 with the worlds
they run in. Every later step must leave the diff empty unless it names a change. `GoldenTraceTests` in
`tests/Creatura.Tests` rebuilds the scenario from the file name through `AgentCatalog` and `WorldCatalog`, runs it
with the planner (bfs) in the seat, and compares row by row; the first row that moved is the failure.

File name: `<world>-<barriers>-<agent>[-<extensions>].jsonl`. No token holds a dash; extensions are joined
with `+` and a `:` in an extension name is written `_` (`explore:scaled` → `explore_scaled`).

| token | values |
|---|---|
| world | `restore` (the toy suite, `Core/RestoreServiceDomain.cs`), `permit`, `irrigation`, `livestock`, `flood` (the `WorldCatalog` ids); `permit.liar` = `LiarWorld` over the permit office (every tool lies once); `permit.opaque` = `LearnedWorld(OpaqueWorld(permit))`; `flood.liar`; `licence` = `LicenceWorld` (the battery 2 learning case) |
| barriers | `restore`: `clean`, `patchrevoked`, `patchlies`, `deployrevoked`; demo worlds: `none` (only the shortcut switches, as on the page) or `all`; `licence`: `case1`, `case2` (case 2 runs after case 1 with the same carried record) |
| agent | the `AgentCatalog` names: `levin`, `bach`, `hybrid`, `peterson`, `adversary`, `careless` (the demo control), `orienting` (the orienting regulator on the reference loop, step 8), `behaviourist` (its control) |
| extensions | `honest`, `record`, `explore_scaled`, `explore_budget` (the `ExtensionCatalog` names) |

Ticks: 30 everywhere, as the offline tests use, except `*.opaque` (120, the battery's series length).

Not here: the four `restore-*-controlled` goldens (`ControlledAgent`, the Agent Framework host, run offline with
the planner behind `PlannerChatClient` over the toy suite: `restore-{clean,patchrevoked,patchlies,deployrevoked}-controlled`).
They live in [`tests/Creatura.Hosts.AgentFramework.Tests/golden/`](../../Creatura.Hosts.AgentFramework.Tests/golden/),
compared by that project's `ControlledGoldenTests`, because rebuilding them needs `Microsoft.Agents.AI`, a package
`Creatura.Tests` must never reference (`CorePurityTest`). They stayed in the research repository until migration
step 16 and moved with the host byte for byte. Same format, same rule, same switch; the file names are the scenario
names, so re-record one with

```bash
CREATURA_RECORD_GOLDENS=restore-patchlies-controlled dotnet test tests/Creatura.Hosts.AgentFramework.Tests --filter "FullyQualifiedName~ControlledGoldenTests"
```

(`all` re-records both sets.)

Format (`AgentRun.WriteJsonl` / `ReadJsonl` in `Core/Trace.cs`, System.Text.Json only): line 1 is the header
(`agent`, `goalReached`, `ticks`), then one line per `TraceEvent` in order, then one line per note in order.
Details that embed line breaks use the platform newline, so record and compare on macOS/Linux (LF).

Nothing in the set draws on a random source: the planner sits in every seat and the random walker is not in
the set, so the opaque learned world has no seed to fix.

The orienting agent's goldens (`*-orienting*`, `*-behaviourist`) were recorded fresh at migration step 8 against the
regulator as first built: they are owed to no earlier loop, so a change there is a decision about the design,
not a regression against one. `restore`, `permit` and `flood` state their valence tables on the task;
`irrigation` and `livestock` do not, so their tables are inferred from the catalogue, which the first note says.

The stuck-regulation golden is `flood.liar-all-hybrid`: the hybrid over every barrier subset of the four demo
worlds, plain, under `LiarWorld` and under the opaque learned world, never fires the alarm on a plain world
or on the toy suite; it fires on the flood world under `LiarWorld` (tick 6) and on livestock under the opaque
learned world. The shortest one is kept.
