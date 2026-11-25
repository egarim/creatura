namespace Creatura.Graph.Tests;

public class GovernanceTests
{
    private static readonly GraphScope Alpha = new("acme", "alpha");
    private static readonly GraphScope Beta = new("acme", "beta");
    private static readonly GraphCaller AcmeAlpha = new("alice", "acme", "alpha");
    private static readonly GraphCaller AcmeBeta = new("bob", "acme", "beta");
    private static readonly GraphCaller Other = new("mallory", "other", "alpha");

    private static GraphWarrant In(GraphScope scope, ProvenanceKind kind = ProvenanceKind.Agent, string source = "alice") =>
        GraphWarrant.Plain with { Scope = scope, Provenance = new GraphProvenance(kind, source) };

    [Fact]
    public void AnotherTenantCanNeitherReadNorWrite()
    {
        var store = Graphs.Store();
        var alpha = store.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("svc", "Application", warrant: In(Alpha)));
        alpha.Put(Graphs.Node("team", "Team", warrant: In(Alpha)));
        alpha.Put(Graphs.Edge("e1", "svc", "ownedBy", "team", warrant: In(Alpha)));

        var outsider = store.Open(Other);

        Assert.Null(outsider.Node("svc"));
        Assert.Empty(outsider.Nodes());
        Assert.Empty(outsider.Edges());
        Assert.Empty(outsider.Adjacent("svc"));
        Assert.Empty(outsider.Walk("svc").Steps);
        Assert.Equal(0, outsider.NodeCount);
        Assert.Equal(MutationKind.Refused, outsider.Put(Graphs.Node("svc", "Application", warrant: In(Alpha))).Kind);
        Assert.Equal(MutationKind.Refused, outsider.Delete("svc", "because").Kind);
        Assert.Equal(MutationKind.Refused, outsider.Verify("e1", "evidence", "mallory").Kind);
    }

    [Fact]
    public void ARefusedReadIsIndistinguishableFromAnAbsentOne()
    {
        var store = Graphs.Store();
        store.Open(AcmeAlpha).Put(Graphs.Node("theirs", "Application", warrant: In(Alpha)));
        var outsider = store.Open(Other);

        // The wall cannot be probed for what is behind it: another tenant's knowledge and nothing at all answer alike.
        Assert.Null(outsider.Node("theirs"));
        Assert.Null(outsider.Node("absent"));
        Assert.Equal(MutationKind.Refused, outsider.Verify("theirs", "evidence", "mallory").Kind);
        Assert.Equal(MutationKind.Refused, outsider.Verify("absent", "evidence", "mallory").Kind);
        Assert.Equal(MutationKind.Refused, outsider.Delete("theirs", "because").Kind);
        Assert.Equal(MutationKind.Refused, outsider.Delete("absent", "because").Kind);
    }

    [Fact]
    public void AnotherWorkspaceReadsOnlyWhatWasSharedWithIt()
    {
        var store = Graphs.Store();
        var alpha = store.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("private", "Application", warrant: In(Alpha)));
        alpha.Put(Graphs.Node("shared-with-the-room", "Application", warrant: In(Alpha with { Readers = ["beta"] })));
        alpha.Put(Graphs.Node("shared-with-bob", "Application", warrant: In(Alpha with { Readers = ["bob"] })));

        var beta = store.Open(AcmeBeta);

        Assert.Null(beta.Node("private"));
        Assert.NotNull(beta.Node("shared-with-the-room"));
        Assert.NotNull(beta.Node("shared-with-bob"));
        Assert.Equal(2, beta.NodeCount);
        // And the room that shared it still reads all three.
        Assert.Equal(3, alpha.NodeCount);
    }

    [Fact]
    public void KnowledgeWithAnOwnerIsOnlyRevisedByItsOwner()
    {
        var store = Graphs.Store();
        var alpha = store.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("owned", "Application", warrant: In(Alpha with { Owner = "alice" })));
        alpha.Put(Graphs.Node("unowned", "Application", warrant: In(Alpha)));
        var colleague = store.Open(new GraphCaller("carol", "acme", "alpha"));

        // Readable by the whole room, revisable only by its owner.
        Assert.NotNull(colleague.Node("owned"));
        Assert.Equal(MutationKind.Refused, colleague.Put(Graphs.Node("owned", "Service", warrant: In(Alpha with { Owner = "alice" }))).Kind);
        Assert.Equal(MutationKind.Refused, colleague.Delete("owned", "because").Kind);
        Assert.Equal("Application", store.Open(AcmeAlpha).Node("owned")!.EntityType);
        // Unowned knowledge is the room's to revise.
        Assert.Equal(MutationKind.Revised, colleague.Put(Graphs.Node("unowned", "Service", warrant: In(Alpha))).Kind);
        // And the owner may still revise its own.
        Assert.Equal(MutationKind.Revised, alpha.Put(Graphs.Node("owned", "Service", warrant: In(Alpha with { Owner = "alice" }))).Kind);
    }

    [Fact]
    public void WritingIntoAWorkspaceYouAreNotInIsRefused()
    {
        var store = Graphs.Store();

        var refused = store.Open(AcmeBeta).Put(Graphs.Node("plant", "Application", warrant: In(Alpha)));

        Assert.Equal(MutationKind.Refused, refused.Kind);
        Assert.Contains("may not write", refused.Reason, StringComparison.Ordinal);
        Assert.Null(store.Open(AcmeAlpha).Node("plant"));
    }

    [Fact]
    public void AnUnrestrictedPolicyIsTheOneThatLetsAnybodyIn()
    {
        var store = Graphs.Store(UnrestrictedAccessPolicy.Instance);
        store.Open(AcmeAlpha).Put(Graphs.Node("svc", "Application", warrant: In(Alpha)));

        Assert.NotNull(store.Open(Other).Node("svc"));
        Assert.Equal("unrestricted", store.Policy.Name);
        Assert.Equal("workspace", Graphs.Store().Policy.Name);
    }

    [Theory]
    [MemberData(nameof(Canaries))]
    public void NoSecretSurvivesIntoStorage(string canary)
    {
        var graph = Graphs.Store().Open(Graphs.Alice);
        graph.Put(Graphs.Node("n", "Application") with
        {
            Summary = $"the key is {canary}",
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["note"] = $"see {canary}" },
            Warrant = GraphWarrant.Plain with
            {
                Evidence = [$"found {canary}"],
                Provenance = new GraphProvenance(ProvenanceKind.Observation, "alice") { Evidence = $"log line {canary}" },
            },
        });
        graph.Put(Graphs.Node("other"));
        graph.Put(Graphs.Edge("e", "n", "uses", "other") with { Summary = $"because {canary}" });

        var node = graph.Node("n")!;
        var edge = graph.Edge("e")!;
        List<string?> kept =
        [
            node.Summary, node.Warrant.Provenance.Evidence, edge.Summary,
            .. node.Attributes.Values, .. node.Warrant.Evidence,
        ];
        var everything = string.Join("\n", kept.Where(t => t is not null));

        Assert.DoesNotContain(canary, everything, StringComparison.Ordinal);
        Assert.Empty(SecretCanary.FoundIn(everything));
        Assert.Contains(Creatura.Cells.Scrubber.Redacted, everything, StringComparison.Ordinal);
    }

    public static TheoryData<string> Canaries()
    {
        var data = new TheoryData<string>();
        foreach (var (_, value) in SecretCanary.All) data.Add(value);
        return data;
    }

    [Fact]
    public void NoSecretSurvivesIntoAnExport()
    {
        var store = Graphs.Store();
        var graph = store.Open(Graphs.Alice);
        var n = 0;
        foreach (var (_, canary) in SecretCanary.All)
        {
            graph.Put(Graphs.Node($"n{n++}", "Application") with
            {
                Summary = canary,
                Warrant = GraphWarrant.Plain with { Evidence = [canary] },
            });
        }

        var json = store.Export(Graphs.Alice).ToJson();

        Assert.Empty(SecretCanary.FoundIn(json));
        Assert.Equal(n, store.NodeCount);
    }

    [Fact]
    public void AnIdentifierIsLeftAloneEvenWhenItIsLong()
    {
        // A decision worth pinning: redaction is applied to free text and never to an id, an entity type or a relation
        // name, because those are what a traversal matches on and a redacted identifier is a broken edge. The cost is
        // that an id has to be structural and never lifted from free text; docs/cognitive-graph/security.md records
        // that as a known limitation rather than hiding it.
        var graph = Graphs.Store().Open(Graphs.Alice);
        const string id = "service-orders-v2-production-shard-01-east";
        graph.Put(Graphs.Node(id, "Application"));

        Assert.Equal(id, graph.Node(id)!.Id);
    }

    [Fact]
    public void ASnapshotRoundTripsThroughJson()
    {
        var store = Graphs.Store();
        var graph = store.Open(Graphs.Alice);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        graph.Put(Graphs.Node("svc", "Application") with { Summary = "the orders service" });
        graph.Put(Graphs.Node("team", "Team"));
        graph.Put(Graphs.Edge("e1", "svc", "ownedBy", "team", warrant: GraphWarrant.Plain with
        {
            Stamps = new GraphStamp { Observed = january, ValidFrom = january },
            Verification = VerificationStatus.Observed,
            Provenance = new GraphProvenance(ProvenanceKind.Observation, "alice") { Corpus = "g03 v1" },
            Support = new GraphSupport(3, 4),
        }));

        var snapshot = GraphSnapshot.Parse(store.Export(Graphs.Alice).ToJson());

        Assert.Equal(GraphSnapshot.FormatName, snapshot.Format);
        Assert.Equal(GraphSchema.Current, snapshot.Schema);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Edges);
        var edge = snapshot.Edges[0];
        Assert.Equal(VerificationStatus.Observed, edge.Warrant.Verification);
        Assert.Equal(january, edge.Warrant.Stamps.ValidFrom);
        Assert.Equal("g03 v1", edge.Warrant.Provenance.Corpus);
        Assert.Equal(3, edge.Warrant.Support.Supporting);
        Assert.Equal(4, edge.Warrant.Support.Total);
        Assert.Equal(0.75, edge.Warrant.Support.Confidence, 10);
        // The relation's meaning travels with the document, so a reader does not need the schema that produced it.
        Assert.Contains(snapshot.Relations, r => r.Name == "ownedBy" && r.Exclusive);
    }

    [Fact]
    public void ASnapshotFromANewerSchemaIsRefusedRatherThanMisread()
    {
        var newer = "{\"format\":\"creatura.graph/1\",\"schema\":99,\"nodes\":[],\"edges\":[]}";
        var wrongFormat = "{\"format\":\"creatura.graph/9\",\"schema\":1,\"nodes\":[],\"edges\":[]}";

        Assert.Throws<FormatException>(() => GraphSnapshot.Parse(newer));
        Assert.Throws<FormatException>(() => GraphSnapshot.Parse(wrongFormat));
        // Malformed JSON is the serializer's exception, not a FormatException: the same split AgentRecord.Parse makes.
        Assert.ThrowsAny<Exception>(() => GraphSnapshot.Parse("not json at all"));
    }

    [Fact]
    public void AnExportCarriesOnlyWhatTheCallerMayReadAndNeverAHalfEdge()
    {
        var store = Graphs.Store();
        var alpha = store.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("svc", "Application", warrant: In(Alpha)));
        alpha.Put(Graphs.Node("team", "Team", warrant: In(Alpha)));
        store.Open(AcmeBeta).Put(Graphs.Node("theirs", "Application", warrant: In(Beta)));
        alpha.Put(Graphs.Edge("kept", "svc", "ownedBy", "team", warrant: In(Alpha)));
        alpha.Put(Graphs.Edge("dropped", "svc", "uses", "theirs", warrant: In(Alpha)));

        var snapshot = store.Export(AcmeAlpha);

        Assert.Equal(2, snapshot.Nodes.Count);
        // The relationship to a node the caller may not read is left out: a half edge in a snapshot is a claim about
        // something the reader was never allowed to see.
        Assert.Single(snapshot.Edges);
        Assert.Equal("kept", snapshot.Edges[0].Id);
        Assert.Equal(AcmeAlpha.ToString(), snapshot.ExportedBy);
        Assert.Equal(Alpha, snapshot.Scope);
    }

    [Fact]
    public void KnowledgeHandedToAnotherWorkspaceArrivesRescopedAndSaysHowItCame()
    {
        var source = Graphs.Store();
        var alpha = source.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("finding", "Cause", GraphCategory.Causal, In(Alpha, ProvenanceKind.Observation) with { Verification = VerificationStatus.Observed }));
        alpha.Put(Graphs.Node("incident", "Incident", GraphCategory.Episodic, In(Alpha, ProvenanceKind.Observation)));
        alpha.Put(Graphs.Edge("c1", "incident", "caused", "finding", GraphCategory.Causal,
            In(Alpha, ProvenanceKind.Observation) with { Verification = VerificationStatus.Observed }));

        // Another store, another agent, another room: the handoff G06 and G15 turn on.
        var destination = Graphs.Store();
        var imported = destination.Import(AcmeBeta, source.Export(AcmeAlpha));
        var beta = destination.Open(AcmeBeta);

        Assert.Equal(3, imported.Taken);
        Assert.Empty(imported.Refused);
        var edge = beta.Edge("c1")!;
        // Who found it travels with it, so the receiving agent does not redo the work.
        Assert.Equal("alice", edge.Warrant.Provenance.Source);
        Assert.Equal(ProvenanceKind.Observation, edge.Warrant.Provenance.Kind);
        Assert.Equal(VerificationStatus.Observed, edge.Warrant.Verification);
        // But it arrived by import, which is how an experiment tells its own oracle graph from somebody else's claim.
        Assert.Equal(ProvenanceKind.Import, edge.Warrant.Provenance.ArrivedAs);
        Assert.False(edge.Warrant.Provenance.IsOracle);
        // And it now lives in the importer's room under the importer's ownership: a document cannot grant itself a tenant.
        Assert.Equal("acme", edge.Warrant.Scope.Tenant);
        Assert.Equal("beta", edge.Warrant.Scope.Workspace);
        Assert.Equal("bob", edge.Warrant.Scope.Owner);
        Assert.Null(destination.Open(Other).Edge("c1"));
    }

    [Fact]
    public void AnImportCannotResurrectWhatWasDeleted()
    {
        var store = Graphs.Store();
        var alpha = store.Open(AcmeAlpha);
        alpha.Put(Graphs.Node("forgotten", "Application", warrant: In(Alpha)));
        var snapshot = store.Export(AcmeAlpha);
        alpha.Delete("forgotten", "the owner asked");

        var reimported = store.Import(AcmeAlpha, snapshot);

        // Deletion survives the round trip, which is the only thing that makes it a deletion rather than a hiding.
        Assert.Equal(0, reimported.Taken);
        Assert.Single(reimported.Refused);
        Assert.True(reimported.Lossy);
        Assert.Null(alpha.Node("forgotten"));
    }

    [Fact]
    public void TheAuditTrailNamesWhoDidWhatAndWhy()
    {
        var store = Graphs.Store();
        var graph = store.Open(Graphs.Alice);
        graph.Put(Graphs.Node("n", "Application"));
        graph.Delete("n", "the owner asked");

        Assert.Equal(2, store.Audit.Count);
        Assert.Equal(MutationKind.Added, store.Audit[0].Kind);
        Assert.Equal(MutationKind.Deleted, store.Audit[1].Kind);
        Assert.Equal("the owner asked", store.Audit[1].Reason);
        Assert.Contains("alice", store.Audit[1].Text, StringComparison.Ordinal);
        Assert.Empty(SecretCanary.FoundIn(string.Join("\n", store.Audit.Select(a => a.Text))));
    }
}
