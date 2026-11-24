# The cognitive graph: data model

Everything here is in `src/Creatura.Graph`, namespace `Creatura.Graph`, package-free.
[The architecture and the reasons for these shapes](architecture.md); this is the shapes.

## Knowledge

### `GraphNode`

```csharp
public sealed record GraphNode(string Id, string EntityType, GraphCategory Category)
{
    public int Schema { get; init; }                                  // GraphSchema.Current
    public GraphWarrant Warrant { get; init; }                        // GraphWarrant.Plain
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
    public string? Summary { get; init; }                             // one line a seat can read
}
```

`Id` is stable across revisions: knowledge that changed its identity when it changed its mind could not be
revised, only duplicated. `EntityType` is open (`"Application"`, `"Incident"`, `"episode"`). `Summary` and
`Attributes` are free text and are redacted on the way in; `Id`, `EntityType` and relation names are structural
and are not (see [security](security.md#identifiers-are-not-redacted)).

### `GraphEdge`

```csharp
public sealed record GraphEdge(string Id, string Subject, string Relation, string Object, GraphCategory Category)
{
    public int Schema { get; init; }
    public GraphWarrant Warrant { get; init; }
    public IReadOnlyDictionary<string, string> Attributes { get; init; }
    public string? Summary { get; init; }
    public bool Touches(string nodeId);
    public string Other(string nodeId);                               // throws when it does not touch
    public GraphEdge Reversed(string? inverseRelation);
}
```

Subject–relation–object, following `MapRoad(Id, A, B)` in `Creatura.Scenarios` — the only other typed edge in the
repository — and adding a warrant, because a road on a paper map is static knowledge and a relationship an agent
believes is a claim.

### `GraphCategory`

`Semantic`, `Episodic`, `Causal`, `Task`, `Social`, `Environmental`. One enum and one adjacency index: a
relationship usually crosses categories, and six stores would mean six traversals.

### `GraphWarrant`

What every piece of knowledge carries beside its content. Nodes and edges share it, because a relationship is as
much a claim as an entity is.

```csharp
public sealed record GraphWarrant
{
    public static readonly GraphWarrant Plain;                        // the least trusting default there is
    public GraphStamp Stamps { get; init; }
    public GraphProvenance Provenance { get; init; }
    public VerificationStatus Verification { get; init; }             // Claimed
    public GraphSupport Support { get; init; }                        // (0, 0) -> 0.5
    public GraphScope Scope { get; init; }                            // default tenant, default workspace
    public GraphState State { get; init; }                            // Active
    public long Revision { get; init; }                               // 1
    public IReadOnlyList<string> Evidence { get; init; }
    public string? SupersededBy { get; init; }
    public bool HoldsAt(DateTimeOffset at);
    public bool IsRetrievable { get; }                                // Active or Contradicted
}
```

`Plain` is an unattributed agent claim, active, at revision one, holding for all time. Forgetting to say where
knowledge came from leaves it weak rather than leaving it looking strong.

### `VerificationStatus` and `GraphState`

Two axes, because they answer different questions.

```
VerificationStatus   Hypothesis < Inferred < Claimed < Observed < Verified
GraphState           Active | Contradicted | Superseded | Deleted
```

Verification is how well supported the content is. State is where the knowledge is in its life. A contradiction is
not a weaker claim — it is a claim in conflict, and the conflict is the finding — so it is a state and not a rung.
Queries filter on `MinVerification` with `>=`, which is why the ladder has to be ordered and `Contradicted` cannot
be in it.

### `GraphStamp`

```csharp
public sealed record GraphStamp
{
    public static readonly GraphStamp Always;
    public DateTimeOffset Observed { get; init; }                     // when the world showed it
    public DateTimeOffset? Written { get; init; }                     // when this record was made
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }                  // exclusive
    public bool HoldsAt(DateTimeOffset at);
    public bool IsTemporal { get; }                                   // any window at all?
}
```

Four stamps, because collapsing them is how a stale belief survives: a note written today about an ownership that
ended last year was *written* now, *observed* then, and is valid only over the period it was true for. Nothing here
reads a clock — a caller passes the moment it is asking about, as `OfferLedger` does for an offer's expiry.
`IsTemporal` exists so that a query asking for a moment can tell knowledge with a window from knowledge that
merely happened to be written at one.

### `GraphProvenance`

```csharp
public sealed record GraphProvenance(ProvenanceKind Kind, string Source)
{
    public static readonly GraphProvenance Unattributed;
    public string? EntryId { get; init; }                             // "cell#sequence"
    public string? EntryHash { get; init; }
    public string? Corpus { get; init; }
    public string? Evidence { get; init; }                            // redacted
    public string? Model { get; init; }
    public ProvenanceKind? ArrivedAs { get; init; }                   // Import, when it arrived in a snapshot
    public bool IsOracle { get; }                                     // Oracle and not imported
}
```

`ProvenanceKind`: `Oracle`, `Extraction`, `Observation`, `Agent`, `Inference`, `Import`.

`EntryId` is the strong form — a ledger entry is signed, content-addressed and globally unique, so knowledge that
came through a cell needs no new identity model. `Model` records which model produced an agent claim, with its
version, because knowledge belongs to the ecosystem and not to the provider that happened to find it. `ArrivedAs`
keeps what a thing *is* apart from how it *got here*, so one agent's discovery reaches another and an experiment
can still tell its own oracle graph from somebody else's claim.

### `GraphSupport`

```csharp
public sealed record GraphSupport(int Supporting, int Total)
{
    public static readonly GraphSupport None;
    public double Confidence { get; }                                 // 0.5 when Total == 0
    public GraphSupport With(bool held);
}
```

Counts and a rate computed from them, in the shape of `SkillBelief(Supported, Attempted)` and
`MemoryUse(Shown, Good)`. Never a stored float: that is the one number in a system like this that drifts away from
the evidence behind it without anybody noticing. The 0.5 prior means an untested belief is not a false one.

### `GraphScope`, `GraphCaller`

```csharp
public sealed record GraphScope(string Tenant, string Workspace = "default")
{
    public static readonly GraphScope Default;
    public string Owner { get; init; }                                // "" = anyone who may write here
    public IReadOnlyList<string> Readers { get; init; }               // [] = this workspace alone
}

public sealed record GraphCaller(string Agent, string Tenant = "default", string Workspace = "default");
```

The Core already reserves the field and no design fills it (`Identity.Scope`: "a light cone's scope, a tenant, a
site"; `LightCone.Scope`, which nothing constructs). A label is an authorization *input*, not a boundary: the
policy decides, and the policy runs outside the model.

### `RelationType` and `GraphSchema`

```csharp
public sealed record RelationType(string Name, GraphCategory Category)
{
    public bool Exclusive { get; init; }       // one subject, one object at a time
    public string? Inverse { get; init; }      // "owns" for "ownedBy"
    public bool Temporal { get; init; }        // must not be read as a cause
    public bool Verifiable { get; init; }      // can it be checked against the world at all?
}

public sealed class GraphSchema
{
    public const int Current = 1;
    public static GraphSchema Benchmark();     // the relations the fixtures and this document use
    public GraphSchema Declare(string name, GraphCategory category, bool exclusive = false,
                               string? inverse = null, bool temporal = false, bool verifiable = true);
    public RelationType? Relation(string name);
}
```

Relations are matched case-insensitively and keep their spelling, so a corpus written in `ownedBy` and one written
in `ownedby` mean one relation and the notes a seat reads still look like words. Node and entity ids stay
case-sensitive: they are structural, and folding them would merge two things that were named apart.

With no schema every relation is uninterpreted — nothing is exclusive, so nothing contradicts, and nothing is
temporal. A relation nobody declared is still storable, because a graph that refused unfamiliar knowledge would be
useless in exactly the situation knowledge is for; it is just storable without an interpretation.

`GraphSchema.Benchmark()` declares the relations the G01–G14 fixtures use, among them:

| relation | category | exclusive | inverse | temporal |
|---|---|---|---|---|
| `ownedBy` | Semantic | **yes** | `owns` | |
| `escalatesTo` | Social | **yes** | | |
| `assignedTo` | Task | **yes** | `assigned` | |
| `caused` | Causal | | `causedBy` | |
| `preceded` | Causal | | | **yes** (not verifiable) |
| `coincidedWith` | Causal | | | **yes** (not verifiable) |
| `blockedBy` | Task | | `blocking` | |
| `experienced` | Episodic | | | |
| `hosts` | Environmental | | `hostedBy` | |

## Reading and writing

```csharp
public interface IGraphReader
{
    GraphSchema Schema { get; }
    GraphNode? Node(string id);
    GraphEdge? Edge(string id);
    IReadOnlyList<GraphNode> Nodes(GraphQuery? query = null);
    IReadOnlyList<GraphEdge> Edges(GraphQuery? query = null);
    IReadOnlyList<GraphEdge> Adjacent(string nodeId, GraphQuery? query = null);
    GraphWalk Walk(string from, GraphTraversalOptions? options = null);
    GraphWalk Path(string from, string to, GraphTraversalOptions? options = null);
    IReadOnlyList<GraphContradiction> Contradictions(GraphQuery? query = null);
    int NodeCount { get; }                                            // what this reader may reach
    int EdgeCount { get; }
}

public interface IGraphWriter
{
    GraphMutation Put(GraphNode node);
    GraphMutation Put(GraphEdge edge);
    GraphMutation Verify(string id, string evidence, string verifier);
    GraphMutation Supersede(string id, string byId);
    GraphMutation Delete(string id, string reason);
    GraphMutation RecordOutcome(string id, bool held);
}

public interface ICognitiveGraph : IGraphReader, IGraphWriter
{
    GraphCaller Caller { get; }
    IGraphAccessPolicy Policy { get; }
}

public interface IGraphStore
{
    GraphSchema Schema { get; }
    IGraphAccessPolicy Policy { get; }
    int NodeCount { get; }                                            // the whole store
    int EdgeCount { get; }
    ICognitiveGraph Open(GraphCaller caller);
    GraphSnapshot Export(GraphCaller caller, GraphQuery? query = null);
    GraphImport Import(GraphCaller caller, GraphSnapshot snapshot, DateTimeOffset? at = null);
}
```

An agent gets an `ICognitiveGraph` and never an `IGraphStore`. The caller is fixed for the life of the session, so
an agent cannot start asking as somebody else mid-run; and a session's counts are what it may reach, not what the
store holds, because the difference is a fact about other people's tenants.

Five interfaces, not the nine issue #109 floats. `ICognitiveGraph` is the graph; `IGraphStore` is the place
knowledge lives; query and traversal are methods and an options record rather than interfaces of their own,
because an interface with one implementation is a declaration of intent and nothing else.

### `GraphQuery`

```csharp
public sealed record GraphQuery
{
    public static readonly GraphQuery All;
    public GraphCategory? Category { get; init; }
    public string? EntityType { get; init; }
    public string? Relation { get; init; }
    public string? Subject { get; init; }
    public string? Object { get; init; }
    public VerificationStatus? MinVerification { get; init; }
    public GraphState? State { get; init; }                           // Active
    public DateTimeOffset? At { get; init; }
    public ProvenanceKind? Provenance { get; init; }
    public string? Corpus { get; init; }
    public bool FollowInverse { get; init; }                          // true
    public int Limit { get; init; }                                   // 100, clamped to 10 000
}
```

`State` defaults to `Active`, so a retrieval that did not ask for superseded knowledge does not get it — showing an
answer that has been replaced is how a stale belief gets reinforced. `Deleted` is never returned whatever is asked.
`Contradictions()` drops the `State` filter and searches retrievable knowledge instead, because a contradiction *is*
a state and a search for active knowledge would filter out the very edges the conflict is made of.

### `GraphTraversalOptions`, `GraphWalk`

```csharp
public sealed record GraphTraversalOptions
{
    public static readonly GraphTraversalOptions Default;
    public int MaxDepth { get; init; }              // 8   — Planner.Find and DreamSleepExtension
    public int MaxNodes { get; init; }              // 256
    public int MaxEdges { get; init; }              // 1 024
    public TimeSpan MaxDuration { get; init; }      // 2 s
    public int Limit { get; init; }                 // 100 steps returned
    public bool FollowInverse { get; init; }        // true
    public Func<GraphEdge, bool>? Usable { get; init; }   // the caller's edge predicate
    public VerificationStatus? MinVerification { get; init; }
    public DateTimeOffset? At { get; init; }
    public Func<GraphEdge, double>? Rank { get; init; }   // expansion order; id order when null
}

public sealed record GraphWalk(IReadOnlyList<GraphStep> Steps, Truncation TruncatedBy,
                               int NodesVisited, int EdgesVisited, double Milliseconds)
{
    public bool Truncated { get; }
    public IReadOnlyList<string> Ids { get; }
    public IReadOnlyList<GraphEdge> Edges { get; }
    public GraphStep? First(string nodeId);
    public bool Reaches(string nodeId);
}
```

`Usable` is `RoadMap.Path`'s edge predicate: the graph is static knowledge and whether an edge may be crossed right
now is the caller's business. `Rank` defaults to nothing, and the walk then expands in id order, so the same graph
walks the same way every time and a result is reproducible from a seed and a fixture version alone.

`Path` never returns null. Read `Reaches(to)` for the answer and `TruncatedBy` for how much of the graph was
searched, because an absent path inside a bound is not an absent path.

### `GraphMutation`, `GraphContradiction`

```csharp
public enum MutationKind { Added, Revised, Superseded, Deleted, Refused, Unchanged }
public sealed record GraphMutation(MutationKind Kind, string Id, string? Reason = null);
public sealed record GraphContradiction(string Subject, string Relation, DateTimeOffset At,
                                        IReadOnlyList<string> EdgeIds);
```

Every refusal carries a reason, because a policy that says no without saying why can be neither audited nor
tested. `Unchanged` is distinct from `Refused`: deleting something twice is idempotent, and reporting it as a
refusal would hide the difference between "already gone" and "never yours".

## Acquisition

```csharp
public interface IKnowledgeExtractor
{
    string Name { get; }
    Extraction Extract(string text, GraphProvenance provenance, GraphScope scope);
}

public sealed record Extraction(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges,
                                IReadOnlyList<string> Skipped);

public sealed class KnowledgePipeline
{
    public KnowledgePipeline(IGraphStore store, IKnowledgeExtractor? extractor = null);
    public GraphIngest Ingest(GraphCaller caller, string text, GraphProvenance provenance, GraphScope? scope = null);
}

public sealed record GraphIngest(int Added, int Revised, int Contradicted, IReadOnlyList<string> Skipped,
                                 IReadOnlyList<GraphMutation> Refused, double Milliseconds);
```

`Skipped` and `Refused` are part of the result, not a diagnostic: an extraction error is a bug to be priced, and a
policy that worked silently could be neither tested nor audited. `Milliseconds` is there because a retrieval that
has to build a graph first is only cheaper than the alternative once the building is counted — which is the whole
of hypothesis H07 and the reason H10 exists.

### `LineExtractor`

Deterministic, no model, one statement per line, in the notation the design documents already write their examples
in — so a fixture reads like the thing it describes and nothing has to be inferred.

```
# a comment, or a blank line
service:orders : Application (semantic)
service:orders -> ownedBy -> team:payments @2026-01-01T00:00:00Z..2026-06-30T00:00:00Z [onboarding v3] !observed
```

A node line is `id : EntityType (category)`; **the colon needs a space either side**, because ids are namespaced
with colons of their own. A relationship line is `subject -> relation -> object` with three optional trailing
qualifiers in order: `@from..to` or `@instant`, `[corpus]`, `!status`. A subject or object may not contain `@`, `[`
or `!`. An endpoint nobody declared is declared as a `thing`, because a dangling relationship is worse than an
untyped entity, and an explicit declaration replaces the implied one.

Entity ids are normalized (`GraphText.Normalize`: trim, collapse inner whitespace, case-fold, cap at 200) so that
two mentions of one thing become one node. Relation names keep the corpus's spelling; the schema matches
case-insensitively.

`LineExtractor.Ceiling(kind, claimed)` caps what a statement may call itself by who vouches for the corpus:

| provenance | strongest it may claim |
|---|---|
| `Oracle` | `Verified` |
| `Observation` | `Observed` |
| `Import` | `Observed` |
| `Extraction`, `Agent`, anything else | `Claimed` |

## Transport

```csharp
public sealed record GraphSnapshot
{
    public const string FormatName = "creatura.graph/1";
    public string Format { get; init; }
    public int Schema { get; init; }
    public DateTimeOffset Exported { get; init; }
    public string ExportedBy { get; init; }
    public GraphScope Scope { get; init; }
    public IReadOnlyList<RelationType> Relations { get; init; }
    public IReadOnlyList<GraphNode> Nodes { get; init; }
    public IReadOnlyList<GraphEdge> Edges { get; init; }
    public string ToJson();
    public static GraphSnapshot Parse(string json);
}
```

Provider-neutral on purpose: a visualizer, another agent on another model and a future SQLite provider all read the
same document. JSON is camelCase with enums by name and nulls left out — camelCase rather than the Core's
PascalCase because this file is an interchange contract, and it matches the other interchange file in the
repository, `models.json`. `Parse` refuses a newer schema and an unrecognized format rather than misreading either.

`Relations` travels with the document so a reader can tell an exclusive relation from a free one without the schema
that produced it. An export drops any relationship with an end the caller may not read: a half edge in a snapshot is
a claim about something the reader was never allowed to see.

## Using it from an agent

```csharp
public sealed class GraphMemoryExtension : IAgentExtension
{
    public const string BlockedBy = "blockedBy";
    public GraphMemoryExtension(ICognitiveGraph graph, GraphMemoryOptions? options = null);
    public string Name { get; }                                       // "graph"
    public IReadOnlyList<GraphEdge> LastRetrieved { get; }
    public int Episodes { get; }
    public IReadOnlyList<GraphEdge> Retrieve(TickContext context);
    public static string Render(GraphEdge edge);
}

public sealed record GraphMemoryOptions
{
    public static readonly GraphMemoryOptions Default;
    public int MaxNotes { get; init; }                                // 8
    public int Depth { get; init; }                                   // 2
    public int MaxNodes { get; init; }                                // 64
    public bool WithholdsBlockedTools { get; init; }                  // true
    public bool RecordsOutcomes { get; init; }                        // true
    public bool RemembersFailures { get; init; }                      // true
    public VerificationStatus MinVerification { get; init; }          // Claimed
    public bool ShowsSuperseded { get; init; }                        // false
}
```

```csharp
IAgent agent = new ReferenceLoop(new SelfModelRegulator(Identity.Bach),
    options: AgentOptions.With(new GraphMemoryExtension(graph)), name: "Bach with a graph");
```

It hooks four of `IAgentExtension`'s slots and nothing else:

- **`PolicyNotes`** — seeds on the task's own words, the facts the registry already holds and the visible tool ids,
  walks out `Depth` from each seed, and renders at most `MaxNotes` relationships as
  `knowledge (observed): subject relation object`. Marked as knowledge and not as instruction, because that is what
  it is, and labelled with how well it is held, because a seat that cannot tell a rumour from a fact cannot use
  either.
- **`VisibleTools`** — withholds a tool when the graph holds an at-least-`Observed` `blockedBy` edge from it to a
  fact the registry does not hold. Subtractive only. The graph is not asked whether the tool may run; it is asked
  what the tool waits for, and the world still answers.
- **`AfterAction`** — records the outcome against the knowledge the agent acted on, moving confidence and not
  verification; and keeps a failure as an `episode` node with an `experienced` edge, because the mistake is what the
  next run needs and the success is what it could have worked out anyway.
- **`Retrieve`** / **`LastRetrieved`** — so a battery can report which evidence contributed to a decision rather
  than only that a decision was made.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md).
