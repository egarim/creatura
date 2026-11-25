namespace Creatura.Graph.Bench;

/// <summary>
/// The commitment half of <see cref="TwoLatches"/> with the one route memory already had closed off.
/// <para>
/// <see cref="CommitWorld"/> is won by a constraint: a <c>blockedBy</c> edge lets
/// <see cref="GraphMemoryExtension.VisibleTools"/> withhold the fatal tool, so the seat is never offered the wrong
/// choice and never has to understand anything. That is a real result and it is measured — 6 of 6 against 0 of 6 —
/// but it says nothing about whether retrieved knowledge can change a decision, because no decision is left to make.
/// </para>
/// <para>
/// This world takes the constraint away. Both commitments stay visible whatever the graph holds, because nothing in
/// it uses the <c>blockedBy</c> convention. The knowledge that saves you is still there and still arbitrary — which
/// latch has rusted through is not deducible from anything in the world — but the only way to get it is to ask, with
/// <c>recall</c>, in your own words. The answer comes back as that action's observation, which the loop writes into
/// the seat's history, so it is still being read on every tick after the one it was asked on.
/// </para>
/// <para>
/// A tool that needs a payload is out of the planner's reach — <c>PlannerPolicy</c> filters
/// <see cref="Tool.NeedsPayload"/> because breadth-first search has no generator — so this fixture cannot be run
/// offline. It is a model-seat fixture only, and <c>AskedLatchesTests</c> pins that fact rather than letting a green
/// deterministic test imply that asking was measured.
/// </para>
/// </summary>
public static class AskedLatches
{
    /// <summary>The tool that asks. It grants nothing: an answer is not a fact about the world, it is a line the seat reads.</summary>
    public const string Recall = "recall";

    /// <summary>What the left latch is known to do, in the relation the withholding convention does not use.</summary>
    public const string LeftKnowledge = "commit-left -> shears -> latch.rusted";

    /// <summary>What the right latch is known to do.</summary>
    public const string RightKnowledge = "commit-right -> holds -> latch.sound";

    /// <summary>
    /// The graph an asking arm carries. Deliberately no <c>blockedBy</c> edge: that convention is what lets
    /// <see cref="GraphMemoryExtension"/> withhold a tool, and a fixture the constraint can win measures the
    /// constraint again.
    /// </summary>
    public static ICognitiveGraph Knowledge(GraphCaller caller)
    {
        var graph = new InMemoryGraphStore(GraphSchema.Benchmark()).Open(caller);
        var known = GraphWarrant.Plain with
        {
            Verification = VerificationStatus.Observed,
            Provenance = new GraphProvenance(ProvenanceKind.Observation, "prior-run"),
            Scope = caller.Scope,
        };
        var said = new LineExtractor(graph.Schema).Extract($"{LeftKnowledge}\n{RightKnowledge}",
            known.Provenance, caller.Scope);
        foreach (var node in said.Nodes) graph.Put(node with { Warrant = known });
        foreach (var edge in said.Edges) graph.Put(edge with { Warrant = known });
        return graph;
    }

    /// <summary>
    /// Answers a free-text question from a graph, in the rendering a seat reads.
    /// <para>
    /// The format is load-bearing. <see cref="GraphMemoryExtension.Render"/> writes
    /// <c>knowledge (observed): subject relation object</c>, and <see cref="LineExtractor"/>'s grammar wants
    /// <c>subject -&gt; relation -&gt; object</c>, so it skips the rendered form. That is the whole defence against
    /// an answer promoting itself: <see cref="GraphMemoryExtension.AfterAction"/> writes an observation back into the
    /// graph at the <c>Observed</c> status a landed action earns, and <c>recall</c> always lands, so an answer the
    /// extractor could read would be re-ingested as fresh evidence for itself. Asking must not be evidence.
    /// </para>
    /// <para>
    /// This is not <see cref="GraphRetrieval"/>, which serves the benchmark's answerer and hands back the corpus's own
    /// statement text so that one shared answerer can read every arm alike. A seat is not that answerer: it reads
    /// rendered knowledge with the warrant on it, and the rendered form is the one the extractor cannot parse.
    /// </para>
    /// </summary>
    public static Func<string, string?> AnsweringFrom(ICognitiveGraph graph, int budget = 6, int depth = 2) => ask =>
    {
        var options = new GraphTraversalOptions
        {
            MaxDepth = depth, MaxNodes = 128, Limit = GraphBounds.MaxResultLimit,
        };
        var found = new List<GraphEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in Seeds(graph, ask))
        {
            foreach (var step in graph.Walk(seed, options).Steps)
            {
                if (step.Via is null || !seen.Add(step.Via.Id)) continue;
                found.Add(step.Via);
                if (found.Count >= budget) return Render(found);
            }

            // A question about one entity also wants the relationships into it, which a walk reaches only when the
            // schema happens to give the relation an inverse.
            foreach (var edge in graph.Adjacent(seed, GraphQuery.All with { Limit = GraphBounds.MaxResultLimit }))
            {
                if (!seen.Add(edge.Id)) continue;
                found.Add(edge);
                if (found.Count >= budget) return Render(found);
            }
        }

        return found.Count == 0 ? AskedWorld.NothingRecorded : Render(found);
    };

    /// <summary>
    /// The nodes the question's own words name, falling back to a substring match when nothing names one exactly — a
    /// seat asks in prose ("which latch holds?") and the graph is keyed by id ("latch.sound").
    /// </summary>
    private static IReadOnlyList<string> Seeds(ICognitiveGraph graph, string ask)
    {
        var words = Arms.Tokens(ask).Select(GraphText.Normalize).Where(w => w.Length > 2).Distinct(StringComparer.Ordinal).ToList();
        var named = words.Where(w => graph.Node(w) is not null).ToList();
        if (named.Count > 0) return named;
        var all = graph.Nodes(GraphQuery.All with { Limit = GraphBounds.MaxResultLimit }).Select(n => n.Id).ToList();
        return [.. words.SelectMany(w => all.Where(id => id.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)];
    }

    private static string Render(IEnumerable<GraphEdge> edges) =>
        string.Join("\n", edges.OrderBy(e => e.Id, StringComparer.Ordinal).Select(GraphMemoryExtension.Render));
}

/// <summary>
/// The asking world: two commitments and a question. Neither commitment has a precondition and nothing in the world
/// says which one holds, so an arm that cannot ask guesses — and a wrong guess adds <see cref="TwoLatches.Broken"/>,
/// which <see cref="Shape"/> turns into a precondition nothing can grant. The irreversibility is
/// <see cref="CommitWorld"/>'s, unchanged; what is new is that the way out runs through a payload.
/// </summary>
public sealed class AskedWorld : CatalogueWorld
{
    /// <summary>What an empty graph says. Arm A gets this: it may ask, and there is nothing to be told.</summary>
    public const string NothingRecorded = "nothing recorded";

    private readonly Func<string, string?> _recall;

    public AskedWorld(Func<string, string?>? recall = null) : base(Tools(), [], Trap()) =>
        _recall = recall ?? (_ => NothingRecorded);

    public static IReadOnlyList<Tool> Tools() =>
    [
        new(AskedLatches.Recall, [], [], "read")
        {
            NeedsPayload = true,
            PayloadHint = "what you want to know, in your own words; the answer is whatever was recorded before now",
        },
        // "write", not "release", for the reason TwoLatches gives: a release-kind tool is gated behind stage 4 and a
        // two-tick world never reaches it, so both choices would be refused and the fixture would measure the stage
        // ladder instead of asking.
        new("commit-left", [], [TwoLatches.Open], "write"),
        new("commit-right", [], [TwoLatches.Open], "write"),
    ];

    private static IReadOnlyList<FaultRule> Trap() =>
    [
        FaultRule.HonestFailure("commit-left", "the left latch shears off in your hand", adds: TwoLatches.Broken),
    ];

    protected override string? Observation(Tool tool, string? payload) =>
        tool.Id == AskedLatches.Recall
            ? _recall(string.IsNullOrWhiteSpace(payload) ? "" : payload.Trim())
            : base.Observation(tool, payload);

    /// <summary>
    /// Once the latch is broken the remaining commitment needs a fact nothing in this world can grant. Rewriting the
    /// precondition rather than revoking the tool keeps the failure visible: the agent can still try, and is still
    /// told why it cannot.
    /// </summary>
    protected override Tool Shape(Tool tool) =>
        Facts.Contains(TwoLatches.Broken) && tool.Id == "commit-right"
            ? tool with { Requires = [.. tool.Requires, "latch.intact"] }
            : base.Shape(tool);

    public static AgentTask Task { get; } = new("open the door",
        "The door is open. Nothing here says which latch holds; asking does.",
        facts => facts.Contains(TwoLatches.Open),
        world => world.Has(TwoLatches.Open) ? 1.0 : 0.0,
        world => world.Has(TwoLatches.Open) ? 1.0 : 0.0,
        // No habit, for the reason TwoLatches gives: a habit here is a hint about which latch to take, and the
        // fixture is worthless the moment anything in the world points at the answer.
        []);

    public override AskedWorld Clone() => new(_recall);
}
