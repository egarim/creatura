# The cognitive graph: roadmap

What is built, what is designed and not built, and what the evidence so far says to do next. Status as of the
benchmark run at seed 20261125 ([results.md](results.md)).

## Built

| | |
|---|---|
| Contracts | `GraphNode`, `GraphEdge`, `GraphWarrant`, `GraphStamp`, `GraphProvenance`, `GraphSupport`, `GraphScope`, `RelationType`, `GraphSchema` |
| Store | `InMemoryGraphStore`, `IGraphStore`, `ICognitiveGraph`, `GraphSession` — bounded traversal, contradiction detection, revision, supersession, tombstones, audit trail |
| Governance | `IGraphAccessPolicy`, `WorkspaceAccessPolicy`, `UnrestrictedAccessPolicy`, `GraphRedaction`, `SecretCanary` |
| Transport | `GraphSnapshot` (`creatura.graph/1`), export filtered by policy, import that cannot escalate |
| Acquisition | `IKnowledgeExtractor`, `LineExtractor`, `KnowledgePipeline`, `GraphText.Normalize` |
| Integration | `GraphMemoryExtension` over `IAgentExtension` |
| Benchmark | `Creatura.Graph.Bench`: 14 fixtures, 4 arms, one shared answerer, 13 pre-registered hypotheses, JSONL and markdown writers |
| Tests | 81 in `Creatura.Graph.Tests`, 21 in `Creatura.Graph.Bench.Tests` |

## Designed, not built

- **Persistent providers.** SQLite first, then Postgres. `IGraphStore` is the contract; nothing else has to
  change. The benchmark gives no reason to build either yet.
- **Vivarium visualization.** `GraphSnapshot` is the provider-neutral export a visualizer would read, and it
  already carries the relations so a reader does not need the schema that produced it. Issue #109 is explicit that
  this comes after the contract is proven.
- **Custodia coordination (#108).** Credential-shaped identifiers are refused today, which is a deny-list. A
  credential broker would make it an allow-list: knowledge about a credential would hold a reference to Custodia
  and never a shape that could be a secret.
- **Ledger-backed provenance and audit.** `GraphProvenance.EntryId` points at a `SignedEntry`, and the audit trail
  is an in-memory list. Putting the trail in the work ledger would make it signed and gap-free, at the cost of a
  dependency on a cell existing.
- **Sealed exports.** Tenant isolation is administrative today. `SharedFiles.Seal` is the cryptographic version
  and is not wired up.

## Next, in the order the evidence suggests

1. **Ranked retrieval.** G12 is the one scenario every arm failed, and the graph arm failed it because a
   high-degree neighbour consumed the budget. `GraphTraversalOptions.Rank` already exists and is unused.
   Pre-register the ranking rule and the fan-out cap *before* building either, with the current unranked result as
   the baseline.
2. **Fixtures that discriminate.** Eleven of fourteen scenarios ended in a tie. Corpora where the answer is
   genuinely distributed — longer chains, more stale revisions, denser distractors — and independently generated
   variants rather than name permutations. Until then the sample size is 14 and no interval is claimed.
3. **A dense-retrieval arm.** Arm B is TF-IDF. H09 (hybrid) failed, but it failed against a weak B, and the
   failure mode was governance rather than recall. Both deserve a fairer second test.
4. **Govern the text side of any hybrid.** Arm D leaked because the policy applies to the graph and not to the
   index that fills the rest of the budget. Either apply scope to both or do not mix them.
5. **The agent-in-the-loop experiment.** A model seat (local Ollama: `gemma3:4b`, `qwen3:4b-instruct`,
   `LFM3-1.7B`), the four arms as four `ExtensionCatalog` names, a `Battery` in the bench's own shape, and
   independent oracles over world state rather than over retrieved text. This is the experiment that would say
   whether any of it changes what an agent *does*.
6. **G15 to G20.** Model substitution and small-versus-large need step 5. Consolidation, invalidation,
   multi-agent discovery and recovery from incorrect memory need only fixtures: `Supersede`, validity windows and
   `RecordOutcome` are already there.

## Deliberately not planned

- A graph database, until a corpus is large enough that rebuilding it per run costs more than querying it.
- Embeddings inside `Creatura.Graph`. The project is package-free and stays that way; a dense arm belongs beside
  the model seats, not inside the store.
- Any change to the Core. `Identity.Scope` and `LightCone.Scope` are the hooks, and filling them is not on the
  critical path.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
