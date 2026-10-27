# Creatura

An agent framework for building agents out of regulation patterns taken from how living things and minds
keep themselves on course, as described by the people who studied them. The core is one generic agent: a
loop, a control layer between the model and its tools, and a contract for worlds that can lie. Everything
else plugs in: a design is a regulator that fills the loop's slots, a behaviour is an extension that hooks
into its verbs, and a world is where both are trained and tested.

This repository is the framework's home. The code is being extracted step by step from the research
repository [egarim/biological-agents](https://github.com/egarim/biological-agents), following its
[design-framework.md](https://github.com/egarim/biological-agents/blob/main/design-framework.md), with
golden traces as the gate for every step. The Core arrived here after migration step 5 (the control layer,
the reference loop, the first two regulators); steps 6 to 8 added the hybrid, map-keeper and orienting regulators, the
join laws and the distrust policies; the remaining steps continue over there, on the branch `creatura-core`, against this checkout.

## Layout

```
Creatura.slnx
src/Creatura/                 the framework Core: no package references, no project references
  Core/                       the loop, the control layer, worlds, tasks, trace, identity (namespace Creatura)
  Core/Regulators/            SelfModelRegulator, ColonyRegulator, HybridRegulator, MapKeeperRegulator (+ Story,
                              TerritoryMap, Posture), OrientingRegulator; Regulators.Join (namespace Creatura)
  Hotz/                       the critic: claims, routes, the interrupt budget (namespace Creatura.Hotz)
  Orienting/                  habituation by match, valence, affect, the behaviourist control, the Sokolov
                              hypotheses as rows (namespace Creatura.Orienting)
src/Creatura.Worlds/          the bench's worlds: sample worlds, manifests, barrier sets, controls, the agent and
                              extension catalogs, the ingenuity series; no packages, no file IO (namespace Creatura.Worlds)
src/Creatura.Bench/           the bench's protocols: oracle and outcome class, hypotheses, batteries, arms and rows,
                              the trainer and the exam over the record store, the legacy CSV writers, the fine-tune
                              seam; may do file IO (namespace Creatura.Bench)
src/Creatura.Seats/           the model seats: IModelTransport (Ollama, OpenAI-compatible, Azure Foundry) over an
                              injected HttpClient, ModelClient, ModelPolicy, ModelJudge, ModelCatalog from models.json,
                              the seat's biology, SeatResolver, the call log and the scoreboard; no packages; may do
                              file IO (namespace Creatura.Seats)
src/Creatura.Hosts.AgentFramework/
                              the Microsoft Agent Framework host: ControlLayerMiddleware, ToolFilteringChatClient,
                              WorldTools, ControlledAgent, PlannerChatClient (the offline model) and ChatClientPolicy
                              (any IChatClient in the loop's seat); the ONLY project here with a package reference
                              (Microsoft.Agents.AI.OpenAI) (namespace Creatura.Hosts.AgentFramework)
samples/QuickStart/           the quick start below, offline
samples/AgentFrameworkHost/   the control layer on Agent Framework over the toy suite's four barriers, offline by
                              default; --compare runs it beside the designs, --seat <catalog name> puts a model in it
samples/models.json           the compiled model catalog written out, to copy beside an executable and edit
tests/Creatura.Tests/         xunit; the tests that need nothing but the Core and its toy domain, plus the golden traces,
                              FakeHost and CorePurityTest; no Agent Framework package
tests/Creatura.Hosts.AgentFramework.Tests/
                              the host's tests and its four goldens (golden/restore-*-controlled.jsonl)
```

```sh
dotnet build Creatura.slnx
dotnet test tests/Creatura.Tests
```

## Building beside biological-agents

The research repository references this one as a **sibling checkout**: its `BiologicalAgents.csproj` points at
`../../../../creatura/src/Creatura/Creatura.csproj`, so the two must sit next to each other:

```
~/Projects/creatura/
~/Projects/biological-agents/
```

Clone both into the same parent folder, build this one first if you like (it has no dependencies either way),
then build the research solution; its golden traces are the gate that the Core here still behaves exactly as
it did there. The research repository's rebuild benchmark also grades the Core modules (`Planner`,
`CoherenceAudit`, `PredictionTracker`, `Table`) from this checkout; `REBUILD_CREATURA_ROOT` overrides the
sibling path.

*Creatura*, Late Latin: that which has been made; first the whole created order, then each made thing.

## Quick start

The whole of this is [samples/QuickStart](samples/QuickStart/Program.cs), a console that runs offline
(`dotnet run --project samples/QuickStart`): the seat is the built-in planner and the world is the toy
"restore the service" domain, with one tool made to lie once.

A **creatura** is `ReferenceLoop` with a regulator in it. The plainest one fills no slot and still gets the
control layer: only visible tools are offered, every effect is checked against the facts, the liar is caught
and quarantined, and a lie never counts as progress.

```csharp
using Creatura;

var world = RestoreServiceDomain.NewWorld();
world.MakeLiar("apply-patch");                       // reports success once, changes nothing

IAgent plain = new ReferenceLoop(new PlainRegulator(), name: "plain loop");
var run = await plain.RunAsync(world, maxTicks: 40);
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks");
foreach (var row in run.Trace) Console.WriteLine($"t{row.Tick} {row.Level} {row.Action} {row.Kind} {row.Detail}");

sealed class PlainRegulator : IRegulator
{
    public Identity Identity { get; } = new("operator", "you");
}
```

A **design is a regulator**. Swap one in; the loop does not change:

```csharp
IAgent bach     = new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), name: "Bach");
IAgent levin    = new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5),
                      layer: ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true }, name: "Levin");
IAgent peterson = new ReferenceLoop(new MapKeeperRegulator(),
                      layer: ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst }, name: "Peterson");
IAgent sokolov  = new ReferenceLoop(new OrientingRegulator(), name: "Sokolov");
IAgent hybrid   = new ReferenceLoop(new HybridRegulator(), name: "Hybrid");
```

**Extensions** add behaviour without touching the regulator:

```csharp
var memory = new EffectMemory();
IAgent explorer = new ReferenceLoop(new ColonyRegulator(Identity.Levin),
    options: AgentOptions.With(new HonestFailureExtension(), new ExplorationExtension(memory, scaled: true)),
    name: "Levin + honest + explore");
```

**The record** is what a creature earns across cases. `RecordCarryExtension` seeds a design's self-model and
prediction record before the first tick and writes them back at sleep; given a store, the loop loads the
record before the run (a regulator's private parts: the hybrid's cell, the orienting agent's resting
inhibition, the map-keeper's territory) and saves it after sleep. Every run leaves its record on
`AgentRun.Record`; `InMemoryRecordStore` and `JsonRecordStore` (one file per key) are the two stores.
Quarantine is never persisted: a new case starts trusting.

```csharp
var store = new JsonRecordStore("records");
IAgent trained = new ReferenceLoop(new SelfModelRegulator(Identity.Bach),
    options: AgentOptions.With(new RecordCarryExtension(store, "bach")),
    store: store, recordKey: "bach");          // case ten knows what case one learned
```

**Your own design** fills only the slots it needs; every other member of `IRegulator` has a default:

```csharp
sealed class CautiousRegulator : IRegulator
{
    public Identity Identity { get; } = new("cautious operator", "you");

    // Error: the share of the task still undone (1.0 = nothing done yet).
    public double Error(RegulatorContext c) => 1.0 - c.Task.Progress(c.World);

    // What error buys: freedom, but never more than half of it, so the retry budget stays small.
    public double Freedom(RegulatorContext c, double error) => Math.Min(0.5, Plasticity.Freedom(error));

    // What the seat reads: a standing note beside the facts and the visible tools.
    public SeatView Seat(RegulatorContext c) => new() { Notes = ["one step at a time; never report a step you did not see land"] };

    // Stop: past the deadline with less than half done, hand off to a person instead of pressing on.
    public StopReason? Stop(RegulatorContext c) =>
        c.Tick > 12 && c.Task.Progress(c.World) < 0.5
            ? new StopReason(StopCause.Escalation, "deadline",
                  new HandOff(HandOffReason.StuckRegulation, "past the deadline with less than half done; a person should look"))
            : null;
}
```

A **critic with no hands** watches the run and reads the route afterwards. It can wrap the world the agent
acts on, or ride the loop as an observer-only extension (the rows are identical with or without it); a host
that owns its control layer can also hand it the layer (`citizen.Watch(layer)`, fed by the layer's
`ActionObserved` event):

```csharp
using Creatura.Hotz;

var owner   = new OwnerToken();
var citizen = new HotzAgent(owner, RestoreServiceDomain.Task, goalEvidence: ["alert.resolved"]);

var watched = citizen.Watch(world);                 // through the world
var run     = await bach.RunAsync(watched);
var route   = citizen.Close(run, watched);          // claims, judgements, what was filtered and why

ExtensionCatalog.Register("critic", _ => new CriticExtension(citizen));   // through the loop, by name
run   = await new ReferenceLoop(regulator, options: ExtensionCatalog.Parse(["critic"])).RunAsync(world);
route = citizen.Close(run, world);
```

What a run of the sample prints: every regulator reaches the goal in 7 to 9 ticks, every one catches the
same lie at tick 3 in its own vocabulary (`effect missing`, `contradiction`, `anomaly, approaching next
tick`, `punishment … orienting, then approach once`), and the critic closes each case with eight claims,
eight judgements and one flag.

## The bench

`Creatura.Bench` is where a design is trained and tested. An **oracle** (`IOracle`) classifies a run by evidence the
agent cannot author: `RegistryOracle` reads the task's goal over the facts of the world the agent could not write to, and
a run that reports the goal without them is *claimed*, never *real*. A **battery** is a pre-registered experiment:
`Hypothesis` rows stated first with their falsifiers, `Arm`s naming an agent, extensions, a seat, a world and its
barriers, repeats, and the oracle; `BatteryReport.Score` says held / failed / mixed / not run per hypothesis. The research
batteries (H1 to H20, P1 to P4) are transcribed in `ClosedBatteries` with their recorded verdicts; the Sokolov hypotheses
(S1 to S7) are a battery that can run.

**Train and test** go through the record store. `Trainer.RunAsync` runs a creature through a `Curriculum` case after
case with its `AgentRecord` saved after each (ten licence cases is the canonical one: a design that starts every case from
nothing refuses the release every time, a carried record earns it by case 2). `Exam.RunAsync` loads the record once into
a `ReadOnlyRecordStore` that refuses every save, runs the battery's arms on it, and scores the hypotheses.

```csharp
using Creatura.Bench;

var store = new JsonRecordStore("records");
var training = await Trainer.RunAsync("bach", Curriculum.Licence(10), store, "bach");   // releases from case 2
var exam = await Exam.RunAsync(SokolovBattery.Battery, store, "bach");                   // never writes to "bach"
Console.WriteLine(exam.Report.ToMarkdown());
```

`Row` carries the columns every result file has had, and `Row.ToLegacyCsv` writes the two archived shapes byte for byte;
`AppendOnlyCsv` only ever appends and sends a changed header to a new file name. `ExampleRecorder` is the fine-tune seam: one
example per judged action (the rendered context, the suggestion, the registry's verdict) and the outcome class at sleep,
into an `IExampleSink`; nothing consumes it yet.

## The seats

`Creatura.Seats` is where a model takes the decision seat or the critic's judge seat, with no package behind it:
`IModelTransport` is one request shape in and one reply out, implemented three times over an injected `HttpClient`
(`OllamaTransport` for `/api/chat`, `OpenAiCompatibleTransport` for `/chat/completions`, which DeepSeek, Azure OpenAI v1
and Ollama's own `/v1` all speak, `AzureFoundryTransport` for `/models/chat/completions`). `ModelClient` picks the
transport by the endpoint's `Provider`, reads the key by the environment-variable name the endpoint carries, and retries
once on a dropped connection, a 401, a 408, a 429 or a 5xx; nothing that looks like a key reaches an error text or a log
line (`Secrets.Scrub`).

`ModelPolicy` is the one prompt layout and the one `{tool, why, content}` answer shape, by function calling or by one
JSON object; only a visible tool id or no action is an answer, and a hidden tool named is refused, never obeyed. Every
call leaves a `CallRecord` on the run's `CallLog` (the prompt verbatim, never a key) and moves the seat's
`SeatTelemetry`, which `Scoreboard.RowFor` turns into a `Row` that keeps the log's path and never a prompt.
`ModelJudge` seats a model in the critic's compare step with the closed answer set, four arms (weights, spec, memory,
memory-spec) and a cache (`IJudgeCache`).

`ModelCatalog` is the seats by name: an optional `models.json` beside the executable, else the compiled table
([samples/models.json](samples/models.json) is that table written out; the schema is on `CatalogRow`). Rows whose
base URL is somebody's own name an environment variable for it and are skipped, with a message, when it is unset.
`SeatResolver` is what `BenchOptions.Seats` takes: `bfs`, `scripted:<path>` or `model:<name>` to an `IPolicy`;
`SeatFactory` is the one place a program builds a seat by name. `BiologyExtension` tells a design what it runs on
(the endpoint's sheet plus what the scoreboards measured) and asks it back at sleep.

```csharp
using Creatura.Seats;

var seat = SeatFactory.Model("deepseek-chat", log: new CallLog("runs/calls.jsonl"));   // the key is read at the first call, by name
var run  = await AgentCatalog.Make("bach", seat, WorldCatalog.Permit.Task).RunAsync(WorldCatalog.Permit.Create(WorldCatalog.Permit.Set("all")));
var row  = Scoreboard.RowFor(DateTimeOffset.UtcNow, seat, "seat", "demo-worlds", "permit", "bach", 1,
               RegistryOracle.For(WorldCatalog.Permit).Classify(run, world), run.Ticks, Measures.LiesCaught(run), false, "barriers=all");
```

## Hosts

A host is a runtime that owns the model call and the function-calling loop; the control layer rides it instead of
the reference loop. A host must supply three things. **The layer's verbs, at its own hooks:** `BeginRound` / `Stop` /
`Assemble` where a round opens, `BeginTick` and `Offer` before each model request (the offered tools are the only
schemas the model gets), `Admit` before a tool runs (refuse or terminate when it says so), `Judge` on the tool's
answer (its feedback is all the model sees; terminate when it says so), `Close` on the model's reply, `Sleep` at the
end. **A tool wrapper that calls `ReportOutcome`** with what the world answered, so `Judge` can tell an honest failure
from a lie; a host that cannot see the outcome passes none, and the layer reads the report as a claim of success.
**A tick definition**: the reference loop's tick is one decision; `Creatura.Hosts.AgentFramework`'s is one model call,
because that is the unit the framework exposes. `FakeHost` in the tests is the whole contract in one small class with
no framework at all, and it reproduces the Agent Framework host's goldens byte for byte.

`Hosts.SemanticKernel` is reserved, not built; its shape is decided. `Admit` and `Judge` go in an
`IFunctionInvocationFilter` around `next` (admit before, judge after, `context.Terminate` when either says so, the
feedback as the function's result); `Offer` goes in a prompt-render filter (`IPromptRenderFilter`), which removes the
distrusted functions' schemas before the request leaves; termination of the automatic function-calling loop goes
through `IAutoFunctionInvocationFilter`, where the layer's terminate ends the loop rather than the single call. Tick =
one model request there too. OpenClaw, Hermes and the Copilot SDK stay documentation: their hooks are mapped onto the
same verbs in the research repository's
[docs/applying-elsewhere.md](https://github.com/egarim/biological-agents/blob/main/docs/applying-elsewhere.md), section "Hosting".

## Anatomy

[docs/anatomy.html](docs/anatomy.html) draws the parts and who talks to whom (plate I), one tick of the loop
(plate II), the kinds of things with their types, and the five Latin names. The same page is published at
https://claude.ai/artifact/QncbLz7HcCTxKbUQQPksKA. Scenario pictures come later, when there are test
scenarios to picture.

## Next: identity and the cell network

[docs/design-cells.md](docs/design-cells.md) designs a strong identity for each agent (who it is, the vessel it runs on, where it
sits on the network), a signed append-only work ledger that says who is working on what, and a cell network with
in-process, mesh and relay implementations. Not built yet; its open decisions are the owner's.
