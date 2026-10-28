# Changelog

## Unreleased

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
  recorder) is proposed in [docs/design-trained-models.md](docs/design-trained-models.md).
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

The first version: the framework extracted from the research repository
[egarim/biological-agents](https://github.com/egarim/biological-agents) following its
`design-framework.md`, with golden traces as the gate for every step. Nothing in this release was
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
- Building beside `biological-agents` needs both checkouts side by side; no NuGet package is published.
