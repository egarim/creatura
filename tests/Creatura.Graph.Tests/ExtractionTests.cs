namespace Creatura.Graph.Tests;

public class ExtractionTests
{
    private const string Corpus = """
        # the orders service and who answers for it
        service:orders : Application (semantic)
        team:payments : Team (social)
        service:orders -> ownedBy -> team:payments [onboarding v3] !observed
        team:payments -> escalatesTo -> person:oncall [onboarding v3] !observed
        """;

    private static (InMemoryGraphStore Store, ICognitiveGraph Graph) Empty()
    {
        var store = Graphs.Store();
        return (store, store.Open(Graphs.Alice));
    }

    [Fact]
    public void ALineCorpusBecomesNodesAndRelationships()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract(Corpus, new GraphProvenance(ProvenanceKind.Observation, "onboarding"), GraphScope.Default);

        Assert.Equal(3, extraction.Nodes.Count);
        Assert.Equal(2, extraction.Edges.Count);
        Assert.Empty(extraction.Skipped);
        Assert.Contains(extraction.Nodes, n => n.Id == "service:orders" && n.EntityType == "Application");
        Assert.Contains(extraction.Nodes, n => n.Id == "team:payments" && n.EntityType == "Team" && n.Category == GraphCategory.Social);
        // The relation's own declared category, not a default.
        Assert.Equal(GraphCategory.Social, extraction.Edges.Single(e => e.Relation == "escalatesTo").Category);
        Assert.Equal("onboarding v3", extraction.Edges[0].Warrant.Provenance.Corpus);
    }

    [Fact]
    public void AnUndeclaredEndpointBecomesAThingRatherThanADanglingReference()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("service:orders -> dependsOn -> db:primary",
            new GraphProvenance(ProvenanceKind.Extraction, "onboarding"), GraphScope.Default);

        Assert.Contains(extraction.Nodes, n => n.Id == "db:primary" && n.EntityType == "thing");
        Assert.Contains(extraction.Nodes, n => n.Id == "service:orders" && n.EntityType == "thing");
    }

    [Fact]
    public void AnExplicitDeclarationReplacesTheEndpointARelationshipImplied()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("""
            service:orders -> dependsOn -> db:primary
            db:primary : Database (environmental)
            """, new GraphProvenance(ProvenanceKind.Extraction, "onboarding"), GraphScope.Default);

        var db = extraction.Nodes.Single(n => n.Id == "db:primary");
        Assert.Equal("Database", db.EntityType);
        Assert.Equal(GraphCategory.Environmental, db.Category);
    }

    [Fact]
    public void TwoMentionsOfOneThingBecomeOneNode()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("""
            Service:Orders -> dependsOn -> DB:Primary
              service:orders   -> uses -> cache
            SERVICE:ORDERS : Application (semantic)
            """, new GraphProvenance(ProvenanceKind.Extraction, "onboarding"), GraphScope.Default);

        Assert.Single(extraction.Nodes, n => n.Id == "service:orders");
        Assert.Equal("Application", extraction.Nodes.Single(n => n.Id == "service:orders").EntityType);
        Assert.Equal("db:primary", extraction.Edges[0].Object);
    }

    [Fact]
    public void AValidityWindowIsReadFromTheLine()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract(
            "service:orders -> ownedBy -> team:payments @2026-01-01T00:00:00Z..2026-07-01T00:00:00Z [onboarding v3]",
            new GraphProvenance(ProvenanceKind.Observation, "onboarding"), GraphScope.Default);

        var stamps = extraction.Edges.Single().Warrant.Stamps;
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), stamps.ValidFrom);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), stamps.ValidUntil);
        Assert.True(stamps.IsTemporal);
        Assert.True(stamps.HoldsAt(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(stamps.HoldsAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AnInstantIsReadAsAMomentWithNoWindow()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("agent:one -> experienced -> incident:two @2026-03-04T05:06:07Z",
            new GraphProvenance(ProvenanceKind.Observation, "run"), GraphScope.Default);

        var stamps = extraction.Edges.Single().Warrant.Stamps;
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), stamps.Observed);
        Assert.Null(stamps.ValidUntil);
        Assert.False(stamps.IsTemporal);
    }

    [Fact]
    public void AnExtractionCannotGrantItselfVerified()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("a -> caused -> b !verified",
            new GraphProvenance(ProvenanceKind.Extraction, "onboarding"), GraphScope.Default);

        // A corpus that could promote itself by saying so would make the whole ladder decorative.
        Assert.Equal(VerificationStatus.Claimed, extraction.Edges.Single().Warrant.Verification);
    }

    [Fact]
    public void AnAgentAssertionCannotGrantItselfObservedEither()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("a -> caused -> b !observed",
            new GraphProvenance(ProvenanceKind.Agent, "alice"), GraphScope.Default);

        Assert.Equal(VerificationStatus.Claimed, extraction.Edges.Single().Warrant.Verification);
    }

    [Fact]
    public void AnOracleCorpusMayCarryVerifiedGroundTruth()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("a -> caused -> b !verified",
            new GraphProvenance(ProvenanceKind.Oracle, "g08-fixture"), GraphScope.Default);

        Assert.Equal(VerificationStatus.Verified, extraction.Edges.Single().Warrant.Verification);
        Assert.True(extraction.Edges.Single().Warrant.Provenance.IsOracle);
    }

    [Fact]
    public void AnObservationMayCarryObservedButNotVerified()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        Assert.Equal(VerificationStatus.Observed, extractor.Extract("a -> caused -> b !observed",
            new GraphProvenance(ProvenanceKind.Observation, "world"), GraphScope.Default).Edges.Single().Warrant.Verification);
        Assert.Equal(VerificationStatus.Observed, extractor.Extract("a -> caused -> b !verified",
            new GraphProvenance(ProvenanceKind.Observation, "world"), GraphScope.Default).Edges.Single().Warrant.Verification);
    }

    [Fact]
    public void UnreadableLinesAreReportedRatherThanDropped()
    {
        var extractor = new LineExtractor(GraphSchema.Benchmark());

        var extraction = extractor.Extract("""
            a -> uses -> b
            this is not a statement about anything
            -> dangling ->
            """, new GraphProvenance(ProvenanceKind.Extraction, "onboarding"), GraphScope.Default);

        Assert.Single(extraction.Edges);
        Assert.Equal(2, extraction.Skipped.Count);
        Assert.Contains(extraction.Skipped, s => s.Contains("not a statement", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameCorpusExtractsIdenticallyEveryTime()
    {
        var provenance = new GraphProvenance(ProvenanceKind.Observation, "onboarding") { Corpus = "g01 v2" };
        var first = new LineExtractor(GraphSchema.Benchmark()).Extract(Corpus, provenance, GraphScope.Default);
        var second = new LineExtractor(GraphSchema.Benchmark()).Extract(Corpus, provenance, GraphScope.Default);

        // Reproducibility is a property of the extractor and not of the machine: no clock, no random, no ordering
        // that a dictionary happened to give it.
        Assert.Equal(first.Edges.Select(e => e.Id), second.Edges.Select(e => e.Id));
        Assert.Equal(first.Nodes.Select(n => n.Id), second.Nodes.Select(n => n.Id));
        Assert.Equal(first.Skipped, second.Skipped);
        Assert.Equal(first.Edges.Select(e => e.Warrant), second.Edges.Select(e => e.Warrant));
    }

    [Fact]
    public void ThePipelineStoresWhatItReadsAndCountsWhatItDid()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);

        var ingested = pipeline.Ingest(Graphs.Alice, Corpus, new GraphProvenance(ProvenanceKind.Observation, "onboarding"));

        Assert.Equal(5, ingested.Added);
        Assert.Equal(0, ingested.Revised);
        Assert.Equal(3, store.NodeCount);
        Assert.Equal(2, store.EdgeCount);
        Assert.Empty(ingested.Skipped);
        Assert.Empty(ingested.Refused);
        Assert.False(ingested.Lossy);
        Assert.True(ingested.Milliseconds >= 0);
        // And what it stored is retrievable through the graph it wrote into.
        Assert.Equal("team:payments", store.Open(Graphs.Alice).Adjacent("service:orders").Single().Object);
    }

    [Fact]
    public void ThePipelineNoticesAContradictionInTheCorpus()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);

        var ingested = pipeline.Ingest(Graphs.Alice, """
            service:orders -> ownedBy -> team:payments [v1] !observed
            service:orders -> ownedBy -> team:logistics [v2] !observed
            """, new GraphProvenance(ProvenanceKind.Observation, "onboarding"));

        Assert.Equal(1, ingested.Contradicted);
        Assert.Single(store.Open(Graphs.Alice).Contradictions());
    }

    [Fact]
    public void ThePipelineReportsWhatThePolicyRefused()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);
        // Knowledge scoped to a room the caller is not in: the pipeline must report the refusals, not swallow them.
        var elsewhere = new GraphScope("acme", "somewhere-else");

        var ingested = pipeline.Ingest(Graphs.Alice, Corpus, new GraphProvenance(ProvenanceKind.Observation, "onboarding"), elsewhere);

        Assert.Equal(0, ingested.Taken);
        Assert.Equal(5, ingested.Refused.Count);
        Assert.True(ingested.Lossy);
        Assert.Equal(0, store.NodeCount);
    }

    [Fact]
    public void ACorpusIsReadIntoTheScopeOfTheCallerByDefault()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);

        pipeline.Ingest(Graphs.Alice, "a -> uses -> b", new GraphProvenance(ProvenanceKind.Extraction, "onboarding"));

        Assert.All(store.Open(Graphs.Alice).Edges(), e => Assert.Equal(Graphs.Alice.Tenant, e.Warrant.Scope.Tenant));
        Assert.Empty(store.Open(Graphs.Outsider).Edges());
    }

    [Fact]
    public void ACorpusThatUsesASecretAsAnIdentifierIsRefusedRatherThanStored()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);

        // Identifiers are not redacted — a redacted id is a broken edge — so one shaped like a credential is refused.
        var ingested = pipeline.Ingest(Graphs.Alice, $"{SecretCanary.ApiKey} -> uses -> cache",
            new GraphProvenance(ProvenanceKind.Extraction, "onboarding"));

        Assert.True(ingested.Lossy);
        Assert.Equal(2, ingested.Refused.Count);
        Assert.All(ingested.Refused, r => Assert.Equal(InMemoryGraphStore.RefusedSecret, r.Reason));
        var json = store.Export(Graphs.Alice).ToJson();
        Assert.Empty(SecretCanary.FoundIn(json));
        Assert.DoesNotContain("canary", json, StringComparison.OrdinalIgnoreCase);
        // The innocent endpoint of the refused relationship still arrived; the one named after a key did not.
        Assert.NotNull(store.Open(Graphs.Alice).Node("cache"));
    }

    [Fact]
    public void ALongStructuralIdentifierIsNotMistakenForASecret()
    {
        var (store, _) = Empty();
        var pipeline = new KnowledgePipeline(store);

        var ingested = pipeline.Ingest(Graphs.Alice,
            "service-orders-v2-production-shard-01 -> dependsOn -> db-primary-east-2",
            new GraphProvenance(ProvenanceKind.Extraction, "onboarding"));

        Assert.False(ingested.Lossy);
        Assert.Equal(3, ingested.Added);
        Assert.Equal(2, store.NodeCount);
    }

    [Fact]
    public void ACoincidenceStaysTemporalAndCannotBeVerifiedIntoACause()
    {
        var (_, graph) = Empty();
        graph.Put(Graphs.Node("restart"));
        graph.Put(Graphs.Node("recovery"));
        graph.Put(Graphs.Edge("c", "restart", "coincidedWith", "recovery", GraphCategory.Causal,
            GraphWarrant.Plain with { Provenance = new GraphProvenance(ProvenanceKind.Agent, "alice") }));

        // Temporal coincidence is not a cause, and the schema is what says so — not the agent that noticed them
        // together, which is the whole of scenario G08.
        Assert.True(GraphSchema.Benchmark().Relation("coincidedWith")!.Temporal);
        Assert.Equal(MutationKind.Refused, graph.Verify("c", "they happened at the same tick", "bob").Kind);
        Assert.Equal(VerificationStatus.Claimed, graph.Edge("c")!.Warrant.Verification);
    }
}
