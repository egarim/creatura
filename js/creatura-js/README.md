# creatura-js

Creatura's control layer in TypeScript: the verbs that stand between a model and its tools on a phone. This is
slice S1 of [`docs/design-mobile.md`](../../docs/design-mobile.md). It is a port of `Creatura.Core`'s control layer,
kept identical to the C# by replaying the C# host's golden traces byte for byte (test M1).

Strict TypeScript, no runtime dependencies, nothing beyond ES2020: the package compiles with no Node or DOM types, so
it runs in Node, a browser and React Native. Unpublished (`"private": true`), under the repository's
[PolyForm Noncommercial licence](../../LICENSE.md).

## What is ported

| C# (src/Creatura/Core) | TypeScript (src) |
|---|---|
| `ControlLayer`, `ControlLayerOptions`, `JudgeRule`, `HonestFailureRule`, `ResetFailures` | `control-layer.ts` |
| `IControlFrame`, `HostFrame` | `control-frame.ts` |
| `Quarantine`, `IDistrust`, `StageInterlock` | `quarantine.ts` |
| `PredictionTracker`, `CoherenceAudit` | `records.ts` |
| `SelfModel`, `SkillBelief` | `self-model.ts` |
| `Stage`, `Stages`, `Plasticity`, `Budget` | `stages.ts` |
| `Admission`, `ActionReport`, `EffectJudgement`, `ActionObservation`, `StopReason`, `Closure` (Verbs.cs) | `verbs.ts` |
| `Tool`, `ActionOutcome`, `IWorldView`, `IWorld` (World.cs) | `world.ts` |
| `CatalogueWorld`, `FaultRule`, `FaultKind`, `World` | `catalogue-world.ts` |
| `TraceEvent`, `AgentRun` and its JSON lines (Trace.cs), `TraceText` | `trace.ts`, `trace-text.ts` |
| `ActionVerdict`, `TraceKind`, `RefusalReason`, `GateDecision`, `StopCause`, `HandOff` | `types.ts` |
| `AgentTask`, `RestoreServiceDomain`, `Planner` | `task.ts`, `restore.ts`, `planner.ts` |
| the Agent Framework host's loop (`ControlledAgent`, `PlannerChatClient`; `FakeHost` in the tests) | `host.ts` (`ControlledHost`, `plannerSeat`) |
| .NET's number, JSON and string rules the traces depend on | `format.ts` |

The C# names map one to one, in camelCase: `Offer()` is `offer()`, `Admit(toolId)` is `admit(toolId)`,
`Judge(tool, report)` is `judge(tool, report)`, `Stop()` is `stop()`, `Close(reply, noRoute)` is `close(reply, noRoute)`.
C#'s enums are const objects whose values are the member names, which is what the traces write. C#'s `null` is
`undefined`. The `ActionObserved` event is `onActionObserved(listener)`.

Not ported, and staying in C#: the designs (Bach, Levin, hybrid, map-keeper) and their regulators, the extensions and
their hooks, the bench, the convoy, the seats, the training tools, the hosts, the cells (the self-model's vessel), the
orienting valence table, and the decorator barriers in `Barrier.cs` (the goldens' barriers are fault rows in the world,
which are ported).

## The host

`ControlledHost` runs a world the way the C# Agent Framework host does, with one tick per model call: per round
`beginRound`, `stop`, `assemble`, then model calls until one answers with text or a verb says terminate; per call
`beginTick` and `offer`; per tool call `admit`, the world's `act`, `reportOutcome`, `judge`; per reply `close`; at the
end `sleep`. The model is a `Seat`, a function from what the model is shown to a tool call or text, sync or async:

```ts
import { ControlledHost, RestoreServiceDomain, writeJsonl } from "creatura-js";

const run = await ControlledHost.offline().run(RestoreServiceDomain.withBarrier("patchlies"), 30);
console.log(writeJsonl(run));

// An on-device model in the seat: return { tool } to call a tool, { text } to answer.
const host = new ControlledHost((world, task) => async (call) => myModel(call.instructions, call.prompt, call.offered, call.feedback),
  { policyName: "on-device" });
```

The run names its host in two strings: the agent (`"Controlled (JS)"` by default) and the framework in the first note
(`"creatura-js"`). The C# host writes `"Controlled (AF)"` and `"Microsoft.Agents.AI"` there, so the golden test hands
those two strings to the host, as C#'s own `FakeHostTests` does, and a second test checks that with its own strings
those are the only two lines that differ.

## Running the tests

```bash
cd js/creatura-js
npm ci
npm test
```

`npm test` compiles `src` and `test` with `tsc` into `build-test/` and runs them with Node's built-in test runner
(Node 22). `npm run build` writes the package to `dist/`. TypeScript and `@types/node` are the only (dev) dependencies.

- `test/golden.test.ts`: test M1. Replays `restore-{clean,patchrevoked,patchlies,deployrevoked}-controlled` and compares
  each with `tests/Creatura.Hosts.AgentFramework.Tests/golden/` row for row and byte for byte. The files are read from
  the repository, never copied, so a change in C# that moves a golden fails this build too. A golden is never
  re-recorded from here: that is the owner's decision (`tests/Creatura.Tests/golden/README.md`).
- `test/control-layer.test.ts`: the component tests of `tests/Creatura.Tests/ControlLayerComponentTests.cs`, ported, plus
  the quarantine's decay, stuck regulation and the action observers.
- `test/world.test.ts`: the world's refusal order and barriers, the planner, and host paths the goldens never take.
- `test/format.test.ts`: .NET's formatting. Every expected string was printed by .NET 10 itself: `P0` and `F2` round
  the exact value with ties to even ("12%" for 0.125), keep the sign of a negative zero ("-0%") and group percents
  ("1,235%"); JSON numbers are the shortest round-trip digits, fixed from 1E-04 up to 17 integer digits. The port matched
  .NET on 71,146 sampled values for `F0`–`F3` and `P0`–`P2`.

One known gap: .NET's JSON encoder also escapes code points unassigned in its Unicode tables. Reproducing that would
need those tables; no string the control layer writes contains one.
