namespace Creatura.Graph.Tests;

/// <summary>The callers and the small builders every graph test uses. Tenants and workspaces are spelled out because isolation is the thing under test.</summary>
internal static class Graphs
{
    public static readonly GraphCaller Alice = new("alice");
    public static readonly GraphCaller Bob = new("bob");

    /// <summary>Same tenant, another room: reads only what was shared out, writes only in its own.</summary>
    public static readonly GraphCaller OtherRoom = new("bob", "default", "other-room");

    /// <summary>Another tenant altogether: the wall.</summary>
    public static readonly GraphCaller Outsider = new("mallory", "other-tenant");

    public static InMemoryGraphStore Store(IGraphAccessPolicy? policy = null) => new(GraphSchema.Benchmark(), policy);

    public static GraphNode Node(string id, string type = "thing", GraphCategory category = GraphCategory.Semantic, GraphWarrant? warrant = null) =>
        new(id, type, category) { Warrant = warrant ?? GraphWarrant.Plain };

    public static GraphEdge Edge(string id, string subject, string relation, string obj,
        GraphCategory category = GraphCategory.Semantic, GraphWarrant? warrant = null) =>
        new(id, subject, relation, obj, category) { Warrant = warrant ?? GraphWarrant.Plain };

    /// <summary>A chain of one-way relationships, n0 -&gt; n1 -&gt; …, for the bound and cycle tests.</summary>
    public static (InMemoryGraphStore Store, ICognitiveGraph Graph) Chain(int length)
    {
        var store = Store();
        var graph = store.Open(Alice);
        for (var i = 0; i < length; i++)
        {
            graph.Put(Node($"n{i}"));
            if (i > 0) graph.Put(Edge($"e{i}", $"n{i - 1}", "uses", $"n{i}"));
        }

        return (store, graph);
    }
}

public class StoreTests
{
    [Fact]
    public void ANodeIsStoredAndReadBack()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);

        Assert.True(graph.Put(Graphs.Node("service:orders", "Application")).Applied);

        var read = graph.Node("service:orders");
        Assert.NotNull(read);
        Assert.Equal("Application", read!.EntityType);
        Assert.Equal(GraphCategory.Semantic, read.Category);
        Assert.Equal(1, graph.NodeCount);
    }

    [Fact]
    public void ARelationshipIsStoredAndReadBackFromEitherEnd()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("service:orders", "Application"));
        graph.Put(Graphs.Node("team:payments", "Team"));
        graph.Put(Graphs.Edge("o1", "service:orders", "ownedBy", "team:payments"));

        // ownedBy declares owns as its inverse, so one edge answers from both ends and the far end reads it the other
        // way round rather than not at all.
        var forward = graph.Adjacent("service:orders");
        var backward = graph.Adjacent("team:payments");
        Assert.Single(forward);
        Assert.Single(backward);
        Assert.Equal("ownedBy", forward[0].Relation);
        Assert.Equal("team:payments", forward[0].Object);
        Assert.Equal("owns", backward[0].Relation);
        Assert.Equal("service:orders", backward[0].Object);
        Assert.Equal(1, graph.EdgeCount);
    }

    [Fact]
    public void ARelationshipWithNoDeclaredInverseIsStillAdjacentInTheDirectionItWasWritten()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("a"));
        graph.Put(Graphs.Node("b"));
        graph.Put(Graphs.Edge("u1", "a", "uses", "b"));

        Assert.Single(graph.Adjacent("a"));
        // Adjacent from the far end, but not renamed: "uses" has no inverse in the schema, and inventing one would put
        // a statement in a seat's notes that the corpus never made.
        var fromB = graph.Adjacent("b");
        Assert.Single(fromB);
        Assert.Equal("u1", fromB[0].Id);
        Assert.Equal("a", fromB[0].Subject);
        Assert.Equal("uses", fromB[0].Relation);
        Assert.Equal("b", fromB[0].Object);
        // Reading the far end at all is the caller's choice, not the schema's.
        Assert.Empty(graph.Adjacent("b", GraphQuery.All with { FollowInverse = false }));
        Assert.Single(graph.Adjacent("a", GraphQuery.All with { FollowInverse = false }));
    }

    [Fact]
    public void AWalkStopsAtADepthBoundAndSaysSo()
    {
        var (_, graph) = Graphs.Chain(4);

        var walk = graph.Walk("n0", new GraphTraversalOptions { MaxDepth = 1 });

        Assert.Equal(Truncation.Depth, walk.TruncatedBy);
        Assert.True(walk.Truncated);
        Assert.Equal(new[] { "n0", "n1" }, walk.Ids);
    }

    [Fact]
    public void AWalkStopsAtANodeBoundAndSaysSo()
    {
        var (_, graph) = Graphs.Chain(4);

        var walk = graph.Walk("n0", new GraphTraversalOptions { MaxNodes = 2 });

        Assert.Equal(Truncation.Nodes, walk.TruncatedBy);
        Assert.Equal(new[] { "n0", "n1" }, walk.Ids);
    }

    [Fact]
    public void AWalkStopsAtAnEdgeBoundAndSaysSo()
    {
        var (_, graph) = Graphs.Chain(4);

        var walk = graph.Walk("n0", new GraphTraversalOptions { MaxEdges = 1 });

        Assert.Equal(Truncation.Edges, walk.TruncatedBy);
        // The edge that tripped the bound was examined before the walk stopped, so it is counted.
        Assert.Equal(2, walk.EdgesVisited);
    }

    [Fact]
    public void AWalkStopsAtAResultLimitAndSaysSo()
    {
        var (_, graph) = Graphs.Chain(6);

        var walk = graph.Walk("n0", new GraphTraversalOptions { Limit = 3 });

        Assert.Equal(Truncation.Limit, walk.TruncatedBy);
        Assert.Equal(3, walk.Steps.Count);
    }

    [Fact]
    public void AWalkThatFinishedSaysItWasNotTruncated()
    {
        var (_, graph) = Graphs.Chain(4);

        var walk = graph.Walk("n0");

        Assert.Equal(Truncation.None, walk.TruncatedBy);
        Assert.False(walk.Truncated);
        Assert.Equal(4, walk.Steps.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, walk.Steps.Select(s => s.Depth));
    }

    [Fact]
    public void AWalkOverASelfLoopTerminates()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("a"));
        graph.Put(Graphs.Edge("loop", "a", "uses", "a"));

        var walk = graph.Walk("a");

        Assert.Equal(Truncation.None, walk.TruncatedBy);
        Assert.Single(walk.Steps);
    }

    [Fact]
    public void AWalkOverACycleVisitsEachNodeOnce()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        foreach (var id in new[] { "a", "b", "c" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("ab", "a", "uses", "b"));
        graph.Put(Graphs.Edge("bc", "b", "uses", "c"));
        graph.Put(Graphs.Edge("ca", "c", "uses", "a"));

        var walk = graph.Walk("a");

        Assert.Equal(Truncation.None, walk.TruncatedBy);
        Assert.Equal(3, walk.Steps.Count);
        Assert.Equal(3, walk.Ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void APathIsTheShortestChainAndReachesItsTarget()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        foreach (var id in new[] { "service", "team", "person", "escalation", "detour", "spare", "extra" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("s-t", "service", "ownedBy", "team"));
        graph.Put(Graphs.Edge("t-p", "team", "supervises", "person"));
        graph.Put(Graphs.Edge("p-e", "person", "escalatesTo", "escalation"));
        // A strictly longer way round, which a breadth-first search must not take even though it is expanded first.
        graph.Put(Graphs.Edge("s-d", "service", "uses", "detour"));
        graph.Put(Graphs.Edge("d-s2", "detour", "uses", "spare"));
        graph.Put(Graphs.Edge("s2-x", "spare", "uses", "extra"));
        graph.Put(Graphs.Edge("x-e", "extra", "uses", "escalation"));

        var path = graph.Path("service", "escalation");

        Assert.True(path.Reaches("escalation"));
        Assert.Equal(Truncation.None, path.TruncatedBy);
        Assert.Equal(new[] { "service", "team", "person", "escalation" }, path.Ids);
        Assert.Equal(new[] { "s-t", "t-p", "p-e" }, path.Edges.Select(e => e.Id));
    }

    [Fact]
    public void APathThatWasCutShortSaysSoRatherThanReportingNoPath()
    {
        // n0 to n5 is five hops: reachable inside the default depth of 8, and not inside a depth of 2.
        var (_, graph) = Graphs.Chain(6);

        var cutShort = graph.Path("n0", "n5", new GraphTraversalOptions { MaxDepth = 2 });
        var searched = graph.Path("n0", "n5");

        Assert.False(cutShort.Reaches("n5"));
        Assert.Equal(Truncation.Depth, cutShort.TruncatedBy);
        Assert.True(searched.Reaches("n5"));
        Assert.Equal(Truncation.None, searched.TruncatedBy);
    }

    [Fact]
    public void APathToSomethingAbsentIsReportedAsSearchedAndNotFound()
    {
        var (_, graph) = Graphs.Chain(4);

        var walk = graph.Path("n0", "nowhere");

        Assert.False(walk.Reaches("nowhere"));
        Assert.Equal(Truncation.None, walk.TruncatedBy);
        Assert.Empty(walk.Steps);
    }

    [Fact]
    public void AQueryIsBoundedByItsOwnLimit()
    {
        var (_, graph) = Graphs.Chain(30);

        Assert.Equal(5, graph.Nodes(GraphQuery.All with { Limit = 5 }).Count);
        Assert.Equal(30, graph.Nodes(GraphQuery.All).Count);
        // And a limit past the ceiling is clamped rather than honoured.
        Assert.Equal(30, graph.Nodes(GraphQuery.All with { Limit = int.MaxValue }).Count);
    }

    [Fact]
    public void AQueryFiltersByCategoryTypeAndVerification()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("svc", "Application", GraphCategory.Semantic));
        graph.Put(Graphs.Node("inc", "Incident", GraphCategory.Episodic,
            GraphWarrant.Plain with { Verification = VerificationStatus.Observed }));
        graph.Put(Graphs.Node("rumour", "Application", GraphCategory.Semantic,
            GraphWarrant.Plain with { Verification = VerificationStatus.Hypothesis }));

        Assert.Single(graph.Nodes(GraphQuery.All with { Category = GraphCategory.Episodic }));
        Assert.Equal(2, graph.Nodes(GraphQuery.All with { EntityType = "Application" }).Count);
        Assert.Equal(2, graph.Nodes(GraphQuery.All with { MinVerification = VerificationStatus.Claimed }).Count);
        Assert.Single(graph.Nodes(GraphQuery.All with { MinVerification = VerificationStatus.Observed }));
        Assert.Empty(graph.Nodes(GraphQuery.All with { MinVerification = VerificationStatus.Observed, EntityType = "Application" }));
    }

    [Fact]
    public void KnowledgeIsOnlyReturnedAtAMomentItHoldsFor()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var july = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        graph.Put(Graphs.Node("service"));
        graph.Put(Graphs.Node("team:old"));
        graph.Put(Graphs.Edge("old", "service", "ownedBy", "team:old", warrant: GraphWarrant.Plain with
        {
            Stamps = new GraphStamp { Observed = january, ValidFrom = january, ValidUntil = july },
        }));

        Assert.Single(graph.Edges(GraphQuery.All with { At = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }));
        Assert.Empty(graph.Edges(GraphQuery.All with { At = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) }));
        // And asking for no moment at all is asking for everything, whenever it was true.
        Assert.Single(graph.Edges());
    }

    [Fact]
    public void AnExclusiveRelationHeldTwoWaysAtOnceContradictsAndKeepsBothSides()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        foreach (var id in new[] { "service", "team:one", "team:two" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("first", "service", "ownedBy", "team:one",
            warrant: GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Observation, "alice") }));
        var second = graph.Put(Graphs.Edge("second", "service", "ownedBy", "team:two",
            warrant: GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Agent, "bob") }));

        Assert.NotNull(second.Reason);
        Assert.Contains("contradicts", second.Reason!, StringComparison.Ordinal);
        Assert.Equal(GraphState.Contradicted, graph.Edge("first")!.Warrant.State);
        Assert.Equal(GraphState.Contradicted, graph.Edge("second")!.Warrant.State);

        var conflicts = graph.Contradictions();
        Assert.Single(conflicts);
        Assert.Equal("service", conflicts[0].Subject);
        Assert.Equal("ownedBy", conflicts[0].Relation);
        Assert.Equal(new[] { "first", "second" }, conflicts[0].EdgeIds);

        // Both sides are still readable: the conflict is the finding, and quietly keeping the later one would launder
        // whichever source spoke last into a fact.
        Assert.Equal(2, graph.Edges(GraphQuery.All with { State = GraphState.Contradicted }).Count);
    }

    [Fact]
    public void AnOwnershipThatChangedIsAHistoryAndNotAContradiction()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var july = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var id in new[] { "service", "team:old", "team:new" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("old", "service", "ownedBy", "team:old", warrant: GraphWarrant.Plain with
        {
            Stamps = new GraphStamp { Observed = january, ValidFrom = january, ValidUntil = july },
        }));
        graph.Put(Graphs.Edge("new", "service", "ownedBy", "team:new", warrant: GraphWarrant.Plain with
        {
            Stamps = new GraphStamp { Observed = july, ValidFrom = july },
        }));

        // Two owners, no conflict: this is G03, and calling it G04 would be the mistake.
        Assert.Empty(graph.Contradictions());
        Assert.Equal(GraphState.Active, graph.Edge("old")!.Warrant.State);
        Assert.Equal(GraphState.Active, graph.Edge("new")!.Warrant.State);
        Assert.Equal("team:old", graph.Edges(GraphQuery.All with { At = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) })[0].Object);
        Assert.Equal("team:new", graph.Edges(GraphQuery.All with { At = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) })[0].Object);
    }

    [Fact]
    public void AFreeRelationHeldTwoWaysIsNotAContradiction()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        foreach (var id in new[] { "app", "db", "cache" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("u1", "app", "uses", "db"));
        graph.Put(Graphs.Edge("u2", "app", "uses", "cache"));

        Assert.Empty(graph.Contradictions());
        Assert.Equal(2, graph.Adjacent("app").Count);
    }

    [Fact]
    public void RevisingKeepsTheIdentityAndRaisesTheRevision()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("svc", "Application") with { Summary = "an application" });
        var first = graph.Node("svc")!;

        var revised = graph.Put(Graphs.Node("svc", "Service") with { Summary = "the same thing, better named" });
        var second = graph.Node("svc")!;

        Assert.Equal(MutationKind.Revised, revised.Kind);
        Assert.Equal(1, first.Warrant.Revision);
        Assert.Equal(2, second.Warrant.Revision);
        Assert.Equal("Service", second.EntityType);
        Assert.Equal("the same thing, better named", second.Summary);
        // One identity, not two copies of it.
        Assert.Equal(1, graph.NodeCount);
    }

    [Fact]
    public void DeletingDropsTheContentKeepsTheProofAndRefusesResurrection()
    {
        var store = Graphs.Store();
        var graph = store.Open(Graphs.Alice);
        graph.Put(Graphs.Node("secret-thing", "Application") with { Summary = "something worth forgetting" });
        graph.Put(Graphs.Node("other"));
        graph.Put(Graphs.Edge("link", "other", "uses", "secret-thing"));

        var deleted = graph.Delete("secret-thing", "the owner asked");

        Assert.Equal(MutationKind.Deleted, deleted.Kind);
        Assert.Equal("the owner asked", deleted.Reason);
        // Not retrievable through any read.
        Assert.Null(graph.Node("secret-thing"));
        Assert.Empty(graph.Nodes(GraphQuery.All with { State = GraphState.Deleted }));
        Assert.Equal(1, graph.NodeCount);
        // And the relationship that named it does not leak the id it was deleted for.
        Assert.Empty(graph.Adjacent("other"));
        Assert.Empty(graph.Edges());
        // While the deletion itself stays provable.
        Assert.Equal(1, store.TombstoneCount);
        Assert.Contains(store.Audit, a => a.Kind == MutationKind.Deleted && a.Reason == "the owner asked");
        // It does not come back under the same id.
        Assert.Equal(MutationKind.Refused, graph.Put(Graphs.Node("secret-thing", "Application")).Kind);
    }

    [Fact]
    public void DeletingTwiceSaysAlreadyDeletedRatherThanNeverExisted()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("a"));
        graph.Put(Graphs.Node("b"));
        graph.Put(Graphs.Edge("e", "a", "uses", "b"));

        Assert.Equal(MutationKind.Deleted, graph.Delete("e", "stale").Kind);
        Assert.Equal(MutationKind.Unchanged, graph.Delete("e", "stale").Kind);
        Assert.Null(graph.Edge("e"));
    }

    [Fact]
    public void SupersedingMarksTheOldAnswerAndKeepsItReadable()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("cause:old"));
        graph.Put(Graphs.Node("cause:new"));

        var superseded = graph.Supersede("cause:old", "cause:new");

        Assert.Equal(MutationKind.Superseded, superseded.Kind);
        Assert.Equal("cause:new", graph.Node("cause:old")!.Warrant.SupersededBy);
        Assert.Null(graph.Nodes().FirstOrDefault(n => n.Id == "cause:old"));
        Assert.NotNull(graph.Nodes(GraphQuery.All with { State = GraphState.Superseded }).FirstOrDefault(n => n.Id == "cause:old"));
        // Nothing to supersede with is a refusal, not a deletion.
        Assert.Equal(MutationKind.Refused, graph.Supersede("cause:new", "cause:absent").Kind);
    }

    [Fact]
    public void AnAgentMayNotVerifyItsOwnClaim()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("incident"));
        graph.Put(Graphs.Node("token"));
        graph.Put(Graphs.Edge("c1", "incident", "caused", "token", GraphCategory.Causal,
            GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Agent, "alice") }));

        var self = graph.Verify("c1", "it said so", "alice");

        Assert.Equal(MutationKind.Refused, self.Kind);
        Assert.Contains("may not verify", self.Reason, StringComparison.Ordinal);
        Assert.Equal(VerificationStatus.Claimed, graph.Edge("c1")!.Warrant.Verification);

        var independent = graph.Verify("c1", "the token's expiry is in the log", "bob");

        Assert.True(independent.Applied);
        Assert.Equal(VerificationStatus.Verified, graph.Edge("c1")!.Warrant.Verification);
        Assert.Contains("the token's expiry is in the log", graph.Edge("c1")!.Warrant.Evidence);
    }

    [Fact]
    public void VerifyingNeedsEvidence()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("a"));
        graph.Put(Graphs.Node("b"));
        graph.Put(Graphs.Edge("e", "a", "uses", "b",
            warrant: GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Agent, "alice") }));

        Assert.Equal(MutationKind.Refused, graph.Verify("e", "   ", "bob").Kind);
    }

    [Fact]
    public void ACoincidenceCannotBeVerifiedIntoACause()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        foreach (var id in new[] { "recovery", "restart" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("c", "restart", "coincidedWith", "recovery", GraphCategory.Causal,
            GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Agent, "alice") }));

        var refused = graph.Verify("c", "they happened together", "bob");

        Assert.Equal(MutationKind.Refused, refused.Kind);
        Assert.Contains("coincidence", refused.Reason, StringComparison.Ordinal);
        Assert.Equal(VerificationStatus.Claimed, graph.Edge("c")!.Warrant.Verification);
    }

    [Fact]
    public void ProvenanceIsKeptAndAnOracleIsDistinguishableFromADiscovery()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("a", warrant: GraphWarrant.Plain with
        {
            Provenance = new GraphProvenance(ProvenanceKind.Oracle, "g01-fixture") { Corpus = "g01 v3" },
        }));
        graph.Put(Graphs.Node("b", warrant: GraphWarrant.Plain with
        {
            Provenance = new GraphProvenance(ProvenanceKind.Agent, "alice") { Model = "gemma3:4b", EntryId = "cell-abc#7" },
        }));

        Assert.True(graph.Node("a")!.Warrant.Provenance.IsOracle);
        Assert.False(graph.Node("b")!.Warrant.Provenance.IsOracle);
        Assert.Equal("gemma3:4b", graph.Node("b")!.Warrant.Provenance.Model);
        Assert.Equal("cell-abc#7", graph.Node("b")!.Warrant.Provenance.EntryId);
        Assert.Single(graph.Nodes(GraphQuery.All with { Provenance = ProvenanceKind.Oracle }));
        Assert.Single(graph.Nodes(GraphQuery.All with { Corpus = "g01 v3" }));
    }

    [Fact]
    public void AnOutcomeMovesConfidenceWithoutMovingVerification()
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("tool"));
        graph.Put(Graphs.Node("effect"));
        graph.Put(Graphs.Edge("e", "tool", "produced", "effect", GraphCategory.Episodic,
            GraphWarrant.Plain with { Verification = VerificationStatus.Observed }));

        Assert.Equal(0.5, graph.Edge("e")!.Warrant.Support.Confidence);
        graph.RecordOutcome("e", held: true);
        graph.RecordOutcome("e", held: true);
        graph.RecordOutcome("e", held: false);

        var support = graph.Edge("e")!.Warrant.Support;
        Assert.Equal(2, support.Supporting);
        Assert.Equal(3, support.Total);
        Assert.Equal(2.0 / 3.0, support.Confidence, 10);
        // Confidence is not verification: three outcomes do not make an observation a verified fact.
        Assert.Equal(VerificationStatus.Observed, graph.Edge("e")!.Warrant.Verification);
    }

    [Fact]
    public void AStoreWithNoSchemaInterpretsNothingAndSoContradictsNothing()
    {
        var store = new InMemoryGraphStore();
        var graph = store.Open(Graphs.Alice);
        foreach (var id in new[] { "s", "x", "y" }) graph.Put(Graphs.Node(id));
        graph.Put(Graphs.Edge("a", "s", "ownedBy", "x"));
        graph.Put(Graphs.Edge("b", "s", "ownedBy", "y"));

        Assert.Empty(graph.Contradictions());
        Assert.Null(store.Schema.Relation("ownedBy"));
        // With no schema there is no inverse to read the relationship back through, but it is still adjacent to the
        // far node in the direction it was written: unfamiliar knowledge is uninterpreted, not invisible.
        var fromX = graph.Adjacent("x");
        Assert.Single(fromX);
        Assert.Equal("a", fromX[0].Id);
    }

    [Fact]
    public void ASchemaKeepsItsRelationsInOneOrderEveryTime()
    {
        var schema = GraphSchema.Benchmark();
        var names = schema.Relations.Select(r => r.Name).ToList();

        Assert.True(schema.Relation("ownedBy")!.Exclusive);
        Assert.Equal("owns", schema.Relation("ownedBy")!.Inverse);
        Assert.True(schema.Relation("coincidedWith")!.Temporal);
        Assert.False(schema.Relation("coincidedWith")!.Verifiable);
        Assert.Null(schema.Relation("nonsense"));
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.Equal(names, GraphSchema.Benchmark().Relations.Select(r => r.Name));
    }

    [Fact]
    public void ConcurrentWritersNeitherLoseKnowledgeNorCorruptTheIndexes()
    {
        var store = Graphs.Store();
        var graph = store.Open(Graphs.Alice);
        const int writers = 8;
        const int each = 50;

        Parallel.For(0, writers, w =>
        {
            for (var i = 0; i < each; i++)
            {
                var id = $"w{w}-n{i}";
                graph.Put(Graphs.Node(id, "Application"));
                graph.Put(Graphs.Edge($"w{w}-e{i}", id, "uses", $"w{(w + 1) % writers}-n{i}"));
            }
        });

        Assert.Equal(writers * each, store.NodeCount);
        Assert.Equal(writers * each, store.EdgeCount);
        // Every relationship still resolves at both ends, which is what a torn index would break.
        Assert.All(graph.Edges(GraphQuery.All with { Limit = GraphBounds.MaxResultLimit }), edge =>
        {
            Assert.NotNull(graph.Node(edge.Subject));
            Assert.Equal(edge.Id, graph.Edge(edge.Id)!.Id);
        });
        // The eight writers' first nodes form one ring, and a walk of it visits each exactly once despite the cycle.
        var ring = graph.Walk("w0-n0");
        Assert.Equal(Truncation.None, ring.TruncatedBy);
        Assert.Equal(writers, ring.Steps.Count);
        Assert.Equal(writers, ring.Ids.Distinct(StringComparer.Ordinal).Count());
    }
}
