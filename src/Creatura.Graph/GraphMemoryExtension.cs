namespace Creatura.Graph;

/// <summary>When the extension is allowed to speak. Retrieval is not free, and handing an agent knowledge it did not ask for is not neutral either: it is noise the model has to read past on every tick.</summary>
public enum GraphMemoryTrigger
{
    /// <summary>Retrieve every tick. What the extension originally did, and the reason its context cost is paid whether or not it is needed.</summary>
    Always,

    /// <summary>
    /// Retrieve only under pressure: when freedom has fallen below a threshold, or when the policy has found no
    /// route. Freedom falls as stress rises against the control layer's set point, so a low freedom is the loop's own
    /// report that things are not going well — which is when looking something up is worth what it costs. Withholding
    /// a blocked tool is NOT gated: that is a constraint, it is cheap, and gating it would let an agent walk into a
    /// trap while it still felt confident.
    /// </summary>
    OnDemand,
}

/// <summary>What the graph extension is allowed to cost an agent per tick. Every number here is a bound on what goes into a prompt, which is the difference between retrieval and injection.</summary>
public sealed record GraphMemoryOptions
{
    public static readonly GraphMemoryOptions Default = new();

    /// <summary>Whether the extension retrieves every tick or only when the agent is under pressure.</summary>
    public GraphMemoryTrigger Trigger { get; init; } = GraphMemoryTrigger.Always;

    /// <summary>The freedom below which an agent counts as under pressure and a retrieval is worth its cost.</summary>
    public double PressureBelowFreedom { get; init; } = 0.35;

    /// <summary>How many lines the seat reads. A note per fact in the graph is not retrieval.</summary>
    public int MaxNotes { get; init; } = 8;

    /// <summary>How far out from a seed the retrieval walks.</summary>
    public int Depth { get; init; } = 2;

    /// <summary>How many nodes one walk may visit.</summary>
    public int MaxNodes { get; init; } = 64;

    /// <summary>Whether the graph may withhold a tool the task knowledge says is still blocked. Narrowing only: see <see cref="GraphMemoryExtension"/>.</summary>
    public bool WithholdsBlockedTools { get; init; } = true;

    /// <summary>Whether an action's outcome is written back against the knowledge the agent acted on.</summary>
    public bool RecordsOutcomes { get; init; } = true;

    /// <summary>Whether a failure is kept as an episode. Successes are not, by default: a graph of everything that went right is a graph nobody can search.</summary>
    public bool RemembersFailures { get; init; } = true;

    /// <summary>
    /// Whether what the world reports back is read into the graph as knowledge. Without this the episodic memory is
    /// write-only — episodes are recorded and never consulted — and nothing carries from one run to the next, which
    /// is the one thing accumulated experience is supposed to do.
    /// </summary>
    public bool IngestsObservations { get; init; } = true;

    /// <summary>The weakest knowledge worth showing a seat.</summary>
    public VerificationStatus MinVerification { get; init; } = VerificationStatus.Claimed;

    /// <summary>Whether superseded knowledge may be shown. Off, because showing an answer that has been replaced is how a stale belief gets reinforced.</summary>
    public bool ShowsSuperseded { get; init; }
}

/// <summary>
/// The graph as an agent uses it: bounded context on the way in, an observation on the way out. It is an
/// <see cref="IAgentExtension"/> and nothing else, so an agent that does not load it is byte for byte the agent that
/// always existed — the same contract <c>CriticExtension</c> states for itself, and the reason the four experimental
/// arms are four extension lists rather than four frameworks.
/// <para>
/// One asymmetry carries the whole of the poisoning defence that lives here rather than in the store: graph knowledge
/// may <em>narrow</em> what an agent is offered and never <em>widen</em> it. A poisoned graph can make an agent do
/// less. It cannot make an agent do something the world did not already allow, because nothing this extension returns
/// adds a tool, grants a fact, or lifts a gate — the control layer still checks every effect against the registry, and
/// retrieved text is a note the seat reads, not an instruction anybody obeys.
/// </para>
/// </summary>
public sealed class GraphMemoryExtension : IAgentExtension
{
    /// <summary>
    /// The convention that lets task knowledge withhold a tool: an edge <c>&lt;tool id&gt; -&gt; blockedBy -&gt;
    /// &lt;fact id&gt;</c> says the tool waits on that fact, and the world's registry is what says whether the fact
    /// holds. The graph is not asked whether the tool may run — it is asked what the tool waits for, and the world
    /// still answers.
    /// </summary>
    public const string BlockedBy = "blockedBy";

    private readonly ICognitiveGraph _graph;
    private readonly GraphMemoryOptions _options;
    private readonly LineExtractor _extractor;
    private int _episodes;

    public GraphMemoryExtension(ICognitiveGraph graph, GraphMemoryOptions? options = null)
    {
        _graph = graph;
        _options = options ?? GraphMemoryOptions.Default;
        _extractor = new LineExtractor(graph.Schema);
    }

    public string Name => "graph";

    public ICognitiveGraph Graph => _graph;

    public GraphMemoryOptions Options => _options;

    /// <summary>How many episodes this run has written, so a battery can price the memory it is carrying.</summary>
    public int Episodes => _episodes;

    public IReadOnlyList<GraphEdge> LastRetrieved { get; private set; } = [];

    /// <summary>
    /// What the seat reads this tick: the relationships nearest the task's own words and the facts the world already
    /// holds, walked out a bounded distance and rendered one line each. Marked as knowledge and not as instruction,
    /// because that is what it is.
    /// </summary>
    public IEnumerable<string> PolicyNotes(TickContext context)
    {
        if (!UnderPressure(context))
        {
            LastRetrieved = [];
            return [];
        }

        var retrieved = Retrieve(context);
        LastRetrieved = retrieved;
        return retrieved.Take(_options.MaxNotes).Select(Render).ToList();
    }

    /// <summary>Whether this tick is one where looking something up is worth what it costs.</summary>
    private bool UnderPressure(TickContext context) =>
        _options.Trigger == GraphMemoryTrigger.Always || context.Freedom < _options.PressureBelowFreedom;

    /// <summary>
    /// The policy has found no route: this is the moment memory exists for. Retrieves what the graph holds about the
    /// situation and writes it straight into the notes, because a hand-off may follow and there would be no next tick
    /// to put it in. Proposes no tool — the extension may narrow what an agent is offered and never widen it, and a
    /// suggestion is a widening.
    /// </summary>
    public PolicySuggestion? NoRoute(TickContext context)
    {
        if (_options.Trigger != GraphMemoryTrigger.OnDemand) return null;
        foreach (var note in Retrieve(context).Take(_options.MaxNotes).Select(Render))
            if (!context.Notes.Contains(note, StringComparer.Ordinal)) context.Notes.Add(note);
        return null;
    }

    /// <summary>
    /// Withholds the tools the task graph says are still blocked. Subtractive only: the list that comes in is the list
    /// that goes out minus the ones the graph has a reason to hold, so no knowledge can introduce a tool the world did
    /// not offer.
    /// </summary>
    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        if (!_options.WithholdsBlockedTools) return visible;
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in visible)
        {
            var id = GraphText.Normalize(tool.Id);
            foreach (var block in _graph.Adjacent(id, new GraphQuery { Relation = BlockedBy, MinVerification = VerificationStatus.Observed }))
            {
                // The object is a fact id in the world's own vocabulary, which is not normalized: the registry is the
                // only truth about whether it holds, and it is asked by its own name.
                if (!context.World.Has(block.Object)) held.Add(tool.Id);
            }
        }

        return held.Count == 0 ? visible : visible.Where(t => !held.Contains(t.Id)).ToList();
    }

    /// <summary>
    /// What the world showed, written back. An outcome moves the confidence of the knowledge the agent acted on, and a
    /// failure becomes an episode — because the mistake is what the next run needs and the success is what it could
    /// have worked out anyway (hypothesis H02).
    /// </summary>
    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        // What the world said, read as knowledge. This is the only route by which one run can inform the next, and it
        // is deliberately narrow: a world that reports a well-formed statement contributes it, and a world that
        // reports prose contributes nothing, because prose is not a claim about the world that a graph can hold.
        if (_options.IngestsObservations && outcome.Observation is { Length: > 0 } reported)
        {
            // What the world confirmed landed is observed; what it did not is a claim. That distinction is the whole
            // of the verification ladder, and it is the reason a withheld tool needs evidence rather than a rumour.
            var earned = verdict == ActionVerdict.Landed ? VerificationStatus.Observed : VerificationStatus.Claimed;
            var said = _extractor.Extract(reported,
                new GraphProvenance(ProvenanceKind.Observation, context.Agent) { Corpus = $"{tool.Id}@{context.Tick}" },
                _graph.Caller.Scope);
            foreach (var node in said.Nodes)
                _graph.Put(node with { Warrant = node.Warrant with { Verification = earned } });
            foreach (var edge in said.Edges)
                _graph.Put(edge with { Warrant = edge.Warrant with { Verification = earned } });
        }

        var id = GraphText.Normalize(tool.Id);
        if (_options.RecordsOutcomes)
            foreach (var known in _graph.Adjacent(id, new GraphQuery { MinVerification = VerificationStatus.Hypothesis }))
                _graph.RecordOutcome(known.Id, verdict == ActionVerdict.Landed);

        if (!_options.RemembersFailures || verdict == ActionVerdict.Landed) return;
        var tick = context.Tick;
        var agent = GraphText.Normalize(context.Agent);
        var episode = $"episode:{agent}:{id}:{tick}";
        _graph.Put(new GraphNode(episode, "episode", GraphCategory.Episodic)
        {
            Summary = GraphRedaction.Summary($"{tool.Id} at tick {tick}: {verdict}"),
            Warrant = GraphWarrant.Plain with
            {
                Stamps = GraphStamp.At(DateTimeOffset.UtcNow),
                Verification = VerificationStatus.Observed,
                Provenance = new GraphProvenance(ProvenanceKind.Observation, context.Agent),
                Scope = _graph.Caller.Scope,
            },
        });
        _graph.Put(new GraphEdge($"{agent}-experienced-{episode}", agent, "experienced", episode, GraphCategory.Episodic)
        {
            Warrant = GraphWarrant.Plain with
            {
                Verification = VerificationStatus.Observed,
                Provenance = new GraphProvenance(ProvenanceKind.Observation, context.Agent),
                Scope = _graph.Caller.Scope,
            },
        });
        _episodes++;
    }

    /// <summary>
    /// The relationships worth showing, nearest first. Seeded by the task's own words and by the facts the world
    /// already holds, because those are the two things an agent is actually asking about, and walked out no further
    /// than <see cref="GraphMemoryOptions.Depth"/>.
    /// </summary>
    public IReadOnlyList<GraphEdge> Retrieve(TickContext context)
    {
        var query = new GraphQuery { MinVerification = _options.MinVerification, Limit = GraphBounds.MaxResultLimit };
        var seeds = new List<string>();
        foreach (var fact in context.World.Facts.OrderBy(f => f, StringComparer.Ordinal))
            seeds.Add(GraphText.Normalize(fact));
        foreach (var word in Words(context.Task.Name + " " + context.Task.GoalText))
            seeds.Add(word);
        foreach (var tool in context.Visible.OrderBy(t => t.Id, StringComparer.Ordinal))
            seeds.Add(GraphText.Normalize(tool.Id));

        var options = new GraphTraversalOptions
        {
            MaxDepth = _options.Depth,
            MaxNodes = _options.MaxNodes,
            Limit = GraphBounds.DefaultResultLimit,
            MinVerification = _options.MinVerification,
        };

        var found = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        foreach (var seed in seeds.Distinct(StringComparer.Ordinal))
        {
            if (_graph.Node(seed) is null) continue;
            foreach (var step in _graph.Walk(seed, options).Steps)
            {
                if (step.Via is null) continue;
                found.TryAdd(step.Via.Id, step.Via);
                if (found.Count >= query.Limit) return Order(found.Values);
            }
        }

        return Order(found.Values);
    }

    /// <summary>Nearest first is not available across seeds, so the order is the only stable one there is: by id.</summary>
    private static List<GraphEdge> Order(IEnumerable<GraphEdge> edges) =>
        [.. edges.OrderBy(e => e.Id, StringComparer.Ordinal)];

    private static IEnumerable<string> Words(string text) =>
        text.Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(GraphText.Normalize)
            .Where(w => w.Length > 2);

    /// <summary>One line, saying how well the knowledge is held. A seat that cannot tell a rumour from a fact cannot use either.</summary>
    public static string Render(GraphEdge edge)
    {
        var mark = edge.Warrant.Verification switch
        {
            VerificationStatus.Verified => "verified",
            VerificationStatus.Observed => "observed",
            VerificationStatus.Inferred => "inferred",
            VerificationStatus.Hypothesis => "hypothesis",
            _ => "claimed",
        };
        var conflict = edge.Warrant.State == GraphState.Contradicted ? ", contradicted" : "";
        var until = edge.Warrant.Stamps.ValidUntil is { } end ? $", until {end:O}" : "";
        return $"knowledge ({mark}{conflict}{until}): {edge.Subject} {edge.Relation} {edge.Object}";
    }
}
