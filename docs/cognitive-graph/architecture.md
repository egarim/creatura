# Design: the cognitive graph

Issue [#109](https://github.com/egarim/creatura/issues/109) (architecture) and
[#110](https://github.com/egarim/creatura/issues/110) (benchmark). Reference:
[Zeng et al., *Cognitive Graph for Multi-Agent Reading Comprehension at Scale*](https://arxiv.org/abs/2404.16130).

**Status: the contracts, the in-memory store, the governance rules and the agent extension are built
(`src/Creatura.Graph`, 81 tests). The G01–G14 fixtures, the four experimental arms and the results are not;
they are the next unit of work and nothing here claims a measured improvement.**

The question is not whether a graph database is better than a memory system. It is whether structured
knowledge — relationships, accumulated experience, provenance — improves the reasoning, learning and efficiency
of a Creatura agent, and under what conditions it does not. That is an experimental question, so the
architecture is written to be measured and to be able to lose.

A language model is a component of cognition, not necessarily the entire cognitive system. This document is
about the part around it.

## The audit: what already existed

Issue #109 asks that existing records, Cells, regulator maps, task dependencies and Bench be audited before any
abstraction is defined, so that the graph does not become a second source of truth for something the framework
already keeps. This is what the audit found, and what was done about it.

| Already in the repository | Where | What the graph does about it |
|---|---|---|
| `AgentRecord` and its `Parts` dictionary (JSON by key) | `Core/Record.cs` | Reused as the persistence seam. Nothing is added to `AgentRecord`; a graph that persists does it as a part, the way `RecordCarryExtension` and `UxMemory` do. |
| `IAgentExtension`, `TickContext`, `SleepContext` | `Core/Extensions.cs` | Reused as the only integration point. The graph is an extension; it is not a new loop, a new regulator or a new slot. |
| `SkillBelief(Supported, Attempted)`, `MemoryUse(Shown, Good)` | `Core/SelfModel.cs`, `Ratings/MemoryCredit.cs` | Copied in shape, not referenced: `GraphSupport(Supporting, Total)` holds counts and computes a rate. A stored confidence float is the one number in a system like this that drifts from its own evidence. |
| `Contradiction(Tick, Claim, Evidence)`, `CoherenceAudit` | `Core/Coherence.cs` | Extended, not replaced. The Core records contradictions and never resolves them; the graph keeps both sides readable and resolves only by superseding. |
| `Hotz.Verdict { Supported, Contradicted, HonestFailure, Unchecked }` and "a judgement with no citation is not shown" | `Hotz/Claims.cs` | Adopted as the stance for `VerificationStatus`. `Unchecked` as a first-class answer is why the graph has a `Hypothesis` rung. |
| `Story`, `TerritoryMap`, `Episode` — procedures, episodes, rules, `Unknown`/`Unreachable`/`Dead` | `Core/Regulators/Story.cs` | Left alone. The map-keeper already has an episodic/semantic/procedural split that works per run; the graph is for knowledge that outlives a run. Its rule that a `Dead` fact is re-tested rather than trusted is inherited as a principle. |
| `Tool.Requires` / `Tool.Grants` and `Planner.Find` (BFS, `maxDepth: 8`) | `Core/World.cs`, `Core/Planner.cs` | Not duplicated. The task graph holds dependencies *between tasks*; the planner keeps solving *between facts*. The graph may withhold a tool; it never becomes the planner. |
| `DreamSleepExtension.Walk` — `depth >= 8 || dreamed > 64`, one `IHabitat.Isolate` per branch | `Core/Extensions.cs` | Its bounds are the graph's defaults. Bounded traversal here is not a new invention. |
| `RoadMap`, `MapNode`, `MapRoad(A, B)` with `Path(from, to, usable)` | `Scenarios/RoadMap.cs` | The only typed node/edge graph in the repository, and the shape `GraphEdge.Touches`/`Other` follows. Its split — the map is static knowledge, whether a road is open is sensed and can lie — is the split the graph keeps between knowledge and world state. |
| `SignedEntry` (`Cell`, `Sequence`, `Timestamp`, `Endorsement`, `Signature`, `Id = "cell#sequence"`, `Hash()`) and `WorkLedger` | `Cells/Entries.cs`, `Cells/WorkLedger.cs` | Reused as the strong provenance form. `GraphProvenance.EntryId`/`EntryHash` point at a ledger entry rather than inventing an identity model, and no new crypto exists here. |
| `Scrubber.Scrub` — private keys, URL credentials, bearer, known tokens, `key=value`, long runs | `Cells/Scrubber.cs` | Reused for redaction. The repository already has three redactors (`Cells`, `Seats/Transport`, `Scenarios/ConvoyBattery`); a fourth would be a fourth place to get wrong. |
| `SharedFiles.Seal` — AES-GCM to a recipient set derived from the ledger | `Cells/SharedFiles.cs` | Named as the strong isolation route, not used yet. Labels authorize; sealing is what makes a boundary cryptographic. |
| `Battery`, `Hypothesis(Id, Statement, Falsifier, Score)`, `Arm`, `Row`, `IOracle`, `Exam`, `BatteryReport` | `Bench/*` | The graph's benchmark will be a `Battery` with four `Arm`s and its own `IOracle`s. No new harness. |
| `Identity.Scope`, `LightCone.Scope` — both declared, neither ever set | `Core/Identity.cs`, `Core/Control.cs` | The reserved hooks `GraphScope` fills. Nothing in the Core was changed to make room. |

**Confirmed absences** — things the audit looked for and did not find, which is why they are new here: any typed
*edge* between knowledge items (the only edges anywhere are `MapRoad` and `Story.Parent`); temporal validity
beyond a single write timestamp (`docs/design-axi.md` pre-registers "no fading of old evidence" as a known
failure); *revision* of a recorded contradiction (`CoherenceAudit` and `Revocation` are both append-only and
monotone); per-item scope or tenant labels; and any per-action identity finer than `(Tick, Tool)`.

No `Graph`, `Node`, `Edge`, `Knowledge`, `Provenance`, `Retriev*`, `Embedding`, `Vector` or `Tenant` type or
member exists in `src/Creatura/`. The words "episodic" and "semantic" appear there only as the map-keeper's
labels for its own memory. The field was clean.

## Decisions

**1. The graph is a separate, optional, package-free project.** `src/Creatura.Graph` references `Creatura` and
`Creatura.Cells` and nothing else, and `CorePurityTest` now names it alongside the other five package-free
projects. The Core does not reference it, does not know it exists, and is unchanged. An agent that loads no graph
extension is the agent that always existed — which is asserted by a test
(`AnExtensionOverAnEmptyGraphLeavesTheRunAsItWas`), not merely intended.

**2. It references `Creatura.Cells` for two things and only two.** `Scrubber`, so that secret exclusion is the
ledger's and not a fourth redactor; and `SignedEntry.Id` as the strong provenance form. Everything else in Cells —
identity, endorsement, revocation, sync — is untouched. The dependency direction is the same one
`CreaturaId.OfRecord` already established: Cells may know about the Core's record, never the reverse.

**3. Six categories are one enum, not six stores.** A relationship usually crosses them — a task an agent
experienced in an environment, discovered from a colleague — and splitting the storage would split the traversal
that is the only reason to have a graph. `GraphCategory` is metadata on a node or edge; there is one adjacency
index.

**4. Verification is a ladder and confidence is counts.** `VerificationStatus` runs
`Hypothesis < Inferred < Claimed < Observed < Verified`. What a model asserts is `Claimed` and never `Observed`:
only something the world showed is observed, and only something checked against evidence its asserter could not
author is verified. Separately, `GraphSupport(Supporting, Total)` computes a rate with a 0.5 prior, in the shape
of `SkillBelief`. Confidence is not verification, and three outcomes do not make an observation a fact.

**5. Contradiction is a property of the schema, not of text.** A `RelationType` says whether a relation is
`Exclusive`. Two live values for one subject in an exclusive relation over overlapping validity periods
contradict; both are marked `GraphState.Contradicted` and neither is removed. Replacing the first with the second
would launder whichever source spoke last into a fact. Two values in a *free* relation are just two facts.

**6. Temporal coincidence is never promoted to a cause.** A `RelationType` may be `Temporal` (`preceded`,
`coincidedWith`). A temporal relation is refused verification outright — `Verify` answers "a coincidence is not a
cause" — so no amount of assertion can turn one into a causal fact. That is scenario G08 as a type rule.

**7. Nothing reads a clock to make a decision.** `GraphStamp.HoldsAt(at)` takes the moment; a caller passes it.
This is the convention `OfferLedger` already states for an offer's expiry: expiry is not a state, because it
depends on the clock, so readers compare against theirs. Validity windows are read the same way.

**8. Bounds are the Core's, and the bound is part of the answer.** `MaxDepth = 8` from `Planner.Find` and
`DreamSleepExtension`, `MaxNodes = 256`, `MaxEdges = 1024`, `MaxDuration = 2s`, `Limit = 100` results. The visited
set is not optional and cannot be switched off, so a cycle costs one visit. A `GraphWalk` carries
`Truncation` — `Depth`, `Nodes`, `Edges`, `Duration`, `Limit` or `None` — because "the answer was not there" and
"the answer was past the bound" are different findings, and an experiment that cannot tell them apart cannot
report a negative result honestly.

**9. Authorization is outside the model, and a refusal looks like an absence.** Every read is filtered by an
`IGraphAccessPolicy` and every write authorized by it, before the model sees anything. `Node("theirs")` and
`Node("absent")` both return null for a caller who may not read; `Verify` and `Delete` both refuse. A wall that
answers differently for the two can be probed for what is behind it.

**10. The extension may narrow what an agent is offered and never widen it.** `VisibleTools` returns the list it
was given minus the tools the task graph says are blocked. It adds no tool, grants no fact and lifts no gate; the
control layer still checks every effect against the registry, and retrieved text is a note the seat reads, not an
instruction anybody obeys. Poisoned knowledge can therefore make an agent do less. It cannot make an agent do
something the world did not already allow. That asymmetry is the whole of the poisoning defence that lives in the
extension rather than in the store, and it is what makes G09 a testable property instead of a hope.

**11. Verification does not travel; observation does.** `Import` re-scopes a snapshot to the importer's own
workspace and marks the provenance `ArrivedAs = Import` while keeping who originally asserted it — so one agent's
discovery reaches another (G06, G15) and an experiment can still tell its own oracle graph from somebody else's
claim. `LineExtractor.Ceiling` caps what a corpus may call itself by who vouches for it: an oracle graph may carry
`Verified`, an observation may carry `Observed`, and an extraction or an agent assertion is `Claimed` whatever the
text said. A corpus that could promote itself by saying `!verified` would make the ladder decorative.

**12. An agent may not verify its own claim.** `Verify(id, evidence, verifier)` is refused when `verifier` is the
provenance source. This is the same rule the benchmark's independent oracles exist to enforce, applied one level
down, and it is what scenario G02 turns on.

**13. Deletion leaves a tombstone.** `Delete` drops the content and keeps the id, the category and the reason in
the audit trail. Erasing the id too would make the deletion unauditable; keeping the content would make it
meaningless. Relationships that named a deleted node stop being readable, because leaving them would leak the id
that was deleted for. Deleted knowledge does not come back under the same id — not by revision, not by
supersession, and not by re-importing the snapshot it came from.

**14. In memory first, and no engine chosen.** `InMemoryGraphStore` is dictionaries and two adjacency indexes
behind one lock. A fixture is rebuilt per run, so a database would only add something to reset. Issue #109 is
explicit that no storage engine is committed to before the benchmarks say which one is worth paying for; what a
SQLite or Postgres provider would implement is `IGraphStore`, and that contract is the deliverable.

**15. Contradictions are recomputed, never stored.** `Contradictions()` reads the edges every time. A stored
contradiction list is the one number in a system like this that can drift from the evidence behind it, and
`docs/design-ratings.md` already states the rule: the rows are the record, the rating is a reading of them.

## What the graph is not

- **Not a second planner.** `Tool.Requires`/`Grants` and `Planner.Find` still solve between facts. The graph may
  say a tool waits on something; the registry still says whether that something holds.
- **Not a replacement for the record.** `AgentRecord` still carries skills, predictions, stage and the designs'
  own parts. A graph that persists does it as a record part.
- **Not a replacement for the map-keeper's territory.** That is per-run and ephemeral and works; this is for
  knowledge that outlives a run.
- **Not a vector store.** There is no embedding and no similarity search here. Arm B of the benchmark is the
  text/bag retrieval the graph is supposed to be compared against, and it will be implemented as its own adapter
  rather than smuggled in as a default.
- **Not trusted content.** Retrieved knowledge is untrusted data. It cannot override a policy, grant a tool or
  authorize a write, and it is redacted on the way in rather than on the way out — knowledge that held a secret
  has already leaked to every reader, export and snapshot by the time anybody asks.

## Documents

- [The data model](data-model.md): the contracts, field by field, with examples.
- [Security and knowledge governance](security.md): isolation, redaction, the canaries, and the known limitations.
- Experimental methodology, the G01–G14 fixtures and the results: with issue #110, not here. Nothing has been
  measured yet.

## Next

1. G01–G14 fixtures with hidden ground truth and independent oracles, as a `Battery` in `Creatura.Bench`
   (#110). Pre-registered thresholds before any run.
2. The four arms: no persistent memory, text retrieval, graph retrieval, hybrid. Same seat, prompts, tools,
   corpus, budgets and oracle; only the retrieval adapter differs.
3. Retrieval-level evaluation first (deterministic, offline, no model), then the agent-in-the-loop arms on local
   seats. Graph construction and extraction cost counted on the same side of the ledger as retrieval.
4. Only then: a recommendation on persistence, and whether any of this was worth it.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
