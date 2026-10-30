# Changelog

## Unreleased

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
