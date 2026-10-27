# Changelog

## Unreleased

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
