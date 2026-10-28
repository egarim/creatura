# Creatura

An agent framework for building agents out of regulation patterns taken from how living things and minds
keep themselves on course, as described by the people who studied them. The core is one generic agent: a
loop, a control layer between the model and its tools, and a contract for worlds that can lie. Everything
else plugs in: a design is a regulator that fills the loop's slots, a behaviour is an extension that hooks
into its verbs, and a world is where both are trained and tested.

This repository is the framework's home. Its designs were first worked out in a private research repository, and
the code moved here step by step, with golden traces as the gate for every step. The designs are the author's
readings of other people's public work; [docs/sources.md](docs/sources.md) says whose, and where each idea comes
from.

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
                              (any IChatClient in the loop's seat); package: Microsoft.Agents.AI.OpenAI
                              (namespace Creatura.Hosts.AgentFramework)
src/Creatura.Cells/           the cells: CreaturaId, CellKey / OwnerKey (ECDSA P-256), VesselProbe and Hardware, Position,
                              CellIdentity, SignedEntry and the eight entry kinds, WorkLedger, JsonLedgerStore, signed hub
                              requests, revocations, shared files (CellCipher, SharedFiles), ICellNetwork; no packages
                              (namespace Creatura.Cells)
src/Creatura.Cells.Sqlite/    SqliteLedgerStore, the ledger in SQLite; package: Microsoft.Data.Sqlite
src/Creatura.Cells.Sync/      the ledger over SyncFramework (LedgerSync); package: BIT.Data.Sync
src/Creatura.Cells.Hub/       the ledger hub, `creatura-hub`: an ASP.NET host for the sync endpoint and the shared files'
                              bytes; packages: BIT.Data.Sync.AspNetCore, Microsoft.Data.Sqlite
src/Creatura.Cli/             the `creatura cell` command over the cells, the hub client and the Claude Code hooks
src/Creatura.Scenarios/       several creaturas in one shared world: the convoy (ConvoyWorld, TruckView, ConvoyPolicy,
                              ScenarioMesh, ConvoyBattery), the replay writer, the recorder and dataset exporter, and the
                              trained networks' seats (ConvoyNetwork); no packages (namespace Creatura.Scenarios)
samples/QuickStart/           the quick start below, offline
samples/AgentFrameworkHost/   the control layer on Agent Framework over the toy suite's four barriers, offline by
                              default; --compare runs it beside the designs, --seat <catalog name> puts a model in it
samples/Convoy/               the convoy from the command line: one run, a battery, recordings, datasets, trial runs
samples/models.json           the compiled model catalog written out, to copy beside an executable and edit
models/                       trained policy networks (plain JSON weights), embedded in Creatura.Scenarios
tools/convoy-viewer/          a three.js page that plays a convoy replay back as a story
tools/train/                  the trainers, Python and numpy run with `uv`: by example (train_policy.py), by trial (train_trial.py)
tests/Creatura.Tests/         xunit; the tests that need nothing but the Core and its toy domain, plus the golden traces,
                              FakeHost and CorePurityTest; no Agent Framework package
tests/Creatura.Cells.Tests/   the cells' pre-registered tests, the hub, sync, shared files, hooks, the command and
                              CellsPurityTest
tests/Creatura.Hosts.AgentFramework.Tests/
                              the host's tests and its four goldens (golden/restore-*-controlled.jsonl)
tests/Creatura.Scenarios.Tests/
                              the convoy, the model seat, the recorder, the networks, shared distrust
```

```sh
dotnet build Creatura.slnx
dotnet test tests/Creatura.Tests
dotnet test tests/Creatura.Cells.Tests
dotnet test tests/Creatura.Scenarios.Tests
```

## Where it came from

The private research repository builds against this one as a sibling checkout, and its golden traces were the gate
that the Core here behaves exactly as it did there. Nothing in this repository depends on it.

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
into an `IExampleSink`. The convoy's recorder attaches it beside its own richer records, and its exporter turns them
into training sets ([docs/design-trained-models.md](docs/design-trained-models.md)).

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
same verbs in the research notes, which are not published.

## Anatomy

[docs/anatomy.html](docs/anatomy.html) draws Creatura in four plates: the project's icon, a winged lion, with each
part named for the part of the framework it stands for (plate I); the parts and who talks to whom, the cells
included (plate II); one tick of the loop (plate III); and the mesh, across vessels through the hub and inside the
convoy's world (plate IV). Open the file in a browser; it needs nothing else.

## Cells

[docs/design-cells.md](docs/design-cells.md) gives each agent an identity bound to its hardware and network, and a
signed, append-only work ledger that says who is working on what. What is built:

- **Identity and the ledger.** An owner key endorses each vessel's cell; every entry is signed by its cell and
  verified on merge. The eight entry kinds: hello, claim, progress, hand-off, release, heartbeat, artifact (a shared
  file) and warning (something a cell saw that the others may act on).
- **The hub and sync.** `creatura-hub` serves the ledger over SyncFramework on the mesh address, trusting the owner
  keys it is given (`--trust`), so it can run on a vessel that does not hold the owner key. Every request is signed by
  its cell; replays and revoked cells are refused.
- **Joining a vessel.** On a new vessel: `request`; on the owner's: `endorse`; back on the new one: `accept`.
- **Shared files** ([docs/design-shared-files.md](docs/design-shared-files.md)). Sealed per recipient cell; the hub
  keeps only ciphertext.
- **Claude Code hooks.** Each session's prompts and tools become a claim with its progress and a hand-off.
- **Automatic sync on macOS.** launchd agents for the sync, and for the hub on the vessel that serves it.

```bash
dotnet run --project src/Creatura.Cli -- cell init
dotnet run --project src/Creatura.Cli -- cell --help
```

| group | commands |
|---|---|
| this cell | `init`, `whoami`, `owner-key` |
| work | `claim`, `progress`, `release`, `handoff`, `heartbeat`, `ledger` |
| more vessels | `request`, `endorse`, `accept`, `revoke` |
| the hub | `sync`, `publish-hub`, `launchd-config`, `install-launchd`, `uninstall-launchd` |
| shared files | `share`, `files`, `fetch` |
| Claude Code | `hook`, `hooks-config`, `install-hooks`, `uninstall-hooks`, `publish-bin` |

Every command takes `--home <dir>` (default `~/.creatura`, or `$CREATURA_HOME`), `--creatura <name>`, `--no-mesh` and
`--store json|sqlite`. Private keys, the vessel salt and the ledger are written owner-only; the raw hardware
identifier is never written or printed. A hub must run a version that knows every entry kind its cells write.

## The convoy: machines acting together

[docs/design-machine-vessels.md](docs/design-machine-vessels.md) gives creaturas physical vessels. Three trucks, each
a creatura with its own cell and ledger, deliver 30 units of supplies to four villages after a flood, coordinating
over an in-memory mesh. The world lies: one flooded road keeps reporting itself open, and one truck's fuel gauge reads
high. A judge reads only the world. Everything runs offline and a seed replays byte for byte.

```bash
dotnet run --project samples/Convoy -- --arm J --seed 0
dotnet run --project samples/Convoy -c Release -- --battery --seeds 50
dotnet run --project samples/Convoy -- --arm J --seed 0 --replay run.jsonl --quiet
```

The battery runs every arm over the same 50 seeds and scores the pre-registered hypotheses. The latest run
(2026-10-28), units delivered of 1500 and lies acted on:

| arm | what is in the trucks | delivered | lies |
|---|---|---|---|
| A | the plain loop and the trucks' planner, a shared ledger | 1406 | 262 |
| C | as A, no ledger: each truck alone | 1430 | 407 |
| J | as A, with three fixes: stalled claims taken over, a lying road distrusted, a stranded truck handing back its claim | 1500 | 54 |
| W | as J, with distrust shared through the ledger (a `Warning` entry) | 1500 | 45 |
| N | as J, with a policy network trained by example on J's planner | 1500 | 54 |
| Q | as W, with that network trained further by trial | 1500 | 41 |

Arms B, D, E, G, H, I, K and L test one design or one fault at a time. Arm R is the first attempt at training by
trial, which delivered less. Arm S puts a language model in the seat, set up as
[docs/convoy-models.md](docs/convoy-models.md) describes. Every result, including the failed hypotheses, is in the
design.

**Watching a run.** [tools/convoy-viewer/index.html](tools/convoy-viewer/index.html) plays a replay back in three.js:
the map, the trucks, the story, and each truck's agent definition in C#. Open the page and drop a replay file on it. The
format is [docs/replay-format.md](docs/replay-format.md).

## Trained models

[docs/design-trained-models.md](docs/design-trained-models.md): a seat does not have to be a language model. The
convoy records every decision with what it came to, exports training sets, and trains small networks that fill the
seat. The trucks' planner keeps no state between ticks, so a small network can copy it.

1. **The recorder.** `--record` keeps every decision; `--dataset` also exports policy, chat and transition sets, never
   from the battery's seeds 0 to 49.
2. **By example.** A 97-64-64-36 network copies arm J's planner: 99.97% of held-out decisions (arm N).
3. **By trial.** PPO in the simulator. The first attempt traded deliveries for caution (arm R). The second priced a
   village left short and anchored the network to its teacher (arm Q).

```bash
dotnet run --project samples/Convoy -c Release -- --dataset datasets/convoy-j --arms J --seeds 100-599
uv run tools/train/train_policy.py datasets/convoy-j models/convoy-policy-j.json --held-out-from 550
dotnet build samples/Convoy -c Release
uv run tools/train/train_trial.py models/convoy-policy-j.json models/convoy-policy-w-trial.json \
    --arm W --end-penalty 5 --anchor 0.2 --keep-at-least-start --work datasets/trial-w
```

Datasets are not committed; the commands make them again. The weights are, in `models/`.

## License

Creatura is free to use for any noncommercial purpose under the
[PolyForm Noncommercial License 1.0.0](LICENSE.md): personal use, study, research, hobby projects, and use by
charities, schools, public research and government bodies. Commercial use is not covered; for that, ask the author
for a separate license (github.com/egarim). The author keeps the copyright, and anyone who shares the code or work
based on it must pass on the license and its notice, `Copyright (c) 2026 Jose Ojeda`.

The software comes as is, with no warranty, and the author is not liable for any damage that comes from using it,
as far as the law allows (see "No Liability" in the license). The packages it depends on keep their own licenses
(MIT, Apache-2.0, and the "No Liability" license of BIT.Data.Sync).
