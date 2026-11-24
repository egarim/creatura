# Creatura.Graph

Typed knowledge for a Creatura agent: entities and relationships in six categories — semantic, episodic, causal, task, social and environmental — each carrying where it came from, when it was true, how well it is supported and who may read it. Optional, and in memory: the Core has never heard of it, and no storage engine is chosen before the benchmarks say which one is worth paying for. No package dependencies.

```sh
dotnet add package Creatura.Graph --prerelease
```

```csharp
using Creatura.Graph;

var store = new InMemoryGraphStore(GraphSchema.Benchmark());
var graph = store.Open(new GraphCaller("alice", "acme", "platform"));

graph.Put(new GraphNode("service:orders", "Application", GraphCategory.Semantic));
graph.Put(new GraphNode("team:payments", "Team", GraphCategory.Social));
graph.Put(new GraphEdge("o1", "service:orders", "ownedBy", "team:payments", GraphCategory.Semantic)
{
    Warrant = GraphWarrant.Plain with
    {
        Verification = VerificationStatus.Observed,
        Provenance = new GraphProvenance(ProvenanceKind.Observation, "alice") { Corpus = "onboarding v3" },
        Stamps = new GraphStamp { Observed = at, ValidFrom = january, ValidUntil = july },
    },
});

// Bounded multi-hop, and the bound is part of the answer.
var path = graph.Path("service:orders", "person:oncall");
Console.WriteLine($"{path.Reaches("person:oncall")}: {string.Join(" -> ", path.Ids)} ({path.TruncatedBy})");
```

A relationship that contradicts an exclusive one already held is marked as a contradiction and both sides are kept, because the conflict is the finding. An agent may not verify its own claim, a temporal coincidence may not be verified into a cause, deleted knowledge does not come back, and free text is redacted on the way in.

Give an agent the graph as an extension, and it retrieves instead of remembering everything:

```csharp
IAgent agent = new ReferenceLoop(new SelfModelRegulator(Identity.Bach),
    options: AgentOptions.With(new GraphMemoryExtension(graph)), name: "Bach with a graph");
```

The extension may narrow what an agent is offered and never widen it: poisoned knowledge can make an agent do less, and cannot make it do something the world did not already allow.

More: [docs/cognitive-graph/architecture.md](https://github.com/egarim/creatura/blob/main/docs/cognitive-graph/architecture.md), and issues [#109](https://github.com/egarim/creatura/issues/109) and [#110](https://github.com/egarim/creatura/issues/110).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
