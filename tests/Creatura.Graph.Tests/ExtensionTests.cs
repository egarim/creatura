namespace Creatura.Graph.Tests;

/// <summary>
/// The extension against the real loop: whether knowledge changes what an agent does, and whether anything it holds can
/// make an agent do more than the world allowed. These are integration tests by intention — a retrieval adapter that
/// was only ever tested on its own would prove nothing about the agent it is supposed to serve.
/// </summary>
public class ExtensionTests
{
    private sealed class PlainRegulator : IRegulator
    {
        public Identity Identity { get; } = new("operator", "you");
    }

    private static ICognitiveGraph Graph() => Graphs.Store().Open(Graphs.Alice);

    private static GraphWarrant Observed() => GraphWarrant.Plain with
    {
        Verification = VerificationStatus.Observed,
        Provenance = new GraphProvenance(ProvenanceKind.Observation, "fixture"),
    };

    private static TickContext Tick(IWorld world, IReadOnlyList<Tool> visible, int tick = 1) => new()
    {
        Agent = "alice",
        World = world,
        Task = RestoreServiceDomain.Task,
        Tick = tick,
        MaxTicks = 40,
        Visible = visible,
        Notes = [],
        Trace = [],
    };

    private static async Task<AgentRun> Run(ICognitiveGraph graph, GraphMemoryOptions? options = null)
    {
        IAgent agent = new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), RestoreServiceDomain.Task,
            AgentOptions.With(new GraphMemoryExtension(graph, options)), name: "graph");
        return await agent.RunAsync(RestoreServiceDomain.NewWorld(), 40);
    }

    private static async Task<AgentRun> RunPlain()
    {
        IAgent agent = new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), RestoreServiceDomain.Task,
            AgentOptions.None, name: "plain");
        return await agent.RunAsync(RestoreServiceDomain.NewWorld(), 40);
    }

    private static IReadOnlyList<string> Acted(AgentRun run) =>
        run.Trace.Where(t => t.Level == "cell" && t.Kind == TraceKind.Acted).Select(t => t.Action).ToList();

    [Fact]
    public async Task AnExtensionOverAnEmptyGraphLeavesTheRunAsItWas()
    {
        var withAnEmptyGraph = await Run(Graph());
        var without = await RunPlain();

        // Optionality is a testable claim, not a slogan: nothing to retrieve means nothing changes.
        Assert.True(withAnEmptyGraph.GoalReached);
        Assert.Equal(without.GoalReached, withAnEmptyGraph.GoalReached);
        Assert.Equal(without.Ticks, withAnEmptyGraph.Ticks);
        Assert.Equal(Acted(without), Acted(withAnEmptyGraph));
    }

    [Fact]
    public async Task AToolTheTaskGraphSaysIsBlockedIsWithheldAndTheLoopFindsTheOtherRoute()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Node("change.freeze", "Condition", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Edge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "change.freeze",
            GraphCategory.Task, Observed()));

        var run = await Run(graph);

        // The habit is apply-patch, and the world offers it; the graph says it waits on a fact the registry does not
        // hold, so the loop takes apply-config instead and still reaches the goal. This is G05 without a model in the
        // seat: the dependency is knowledge, and the registry is still what says whether it is satisfied.
        Assert.True(run.GoalReached);
        Assert.Contains("apply-config", Acted(run));
        Assert.DoesNotContain("apply-patch", Acted(run));
    }

    [Fact]
    public async Task ABlockedToolWhoseConditionTheWorldSatisfiesIsNotWithheld()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool", GraphCategory.Task, Observed()));
        // The registry already holds this one, so there is nothing to wait for.
        graph.Put(Graphs.Node("permission.deploy", "Condition", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Edge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "permission.deploy",
            GraphCategory.Task, Observed()));

        var run = await Run(graph);

        Assert.True(run.GoalReached);
        Assert.Contains("apply-patch", Acted(run));
    }

    [Fact]
    public async Task ARumourCannotWithholdATool()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool", GraphCategory.Task));
        graph.Put(Graphs.Node("change.freeze", "Condition", GraphCategory.Task));
        // Verification left at its default: an agent's claim, which is not enough to stop a tool.
        graph.Put(Graphs.Edge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "change.freeze", GraphCategory.Task));

        var run = await Run(graph);

        Assert.True(run.GoalReached);
        Assert.Contains("apply-patch", Acted(run));
    }

    [Fact]
    public void PoisonedKnowledgeCanNarrowWhatIsOfferedButNeverWidenIt()
    {
        var graph = Graph();
        // A graph that believes it knows a tool the world has never offered, and that the ones it has are blocked.
        graph.Put(Graphs.Node("magic-fix", "Tool", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Node("root", "Capability", GraphCategory.Environmental, Observed()));
        graph.Put(Graphs.Edge("gift", "magic-fix", "provides", "root", GraphCategory.Environmental, Observed()));
        foreach (var tool in RestoreServiceDomain.Tools())
        {
            graph.Put(Graphs.Node(tool.Id, "Tool", GraphCategory.Task, Observed()));
            graph.Put(Graphs.Node($"block:{tool.Id}", "Condition", GraphCategory.Task, Observed()));
            graph.Put(Graphs.Edge($"gate:{tool.Id}", tool.Id, GraphMemoryExtension.BlockedBy, $"block:{tool.Id}",
                GraphCategory.Task, Observed()));
        }

        var world = RestoreServiceDomain.NewWorld();
        var extension = new GraphMemoryExtension(graph);
        var offered = world.AvailableTools;
        var after = extension.VisibleTools(Tick(world, offered), offered);

        // Nothing was added, whatever the graph claimed: the world's catalogue is the ceiling and the extension can
        // only ever subtract from it. That asymmetry is the whole of the poisoning defence that lives here.
        Assert.All(after, tool => Assert.Contains(tool, offered));
        Assert.True(after.Count <= offered.Count);
        Assert.DoesNotContain(after, t => t.Id == "magic-fix");
    }

    [Fact]
    public void RetrievedKnowledgeReachesTheSeatAsBoundedNotes()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("inspect-logs", "Tool"));
        graph.Put(Graphs.Node("logs.inspected", "Fact"));
        graph.Put(Graphs.Node("component.identified", "Fact"));
        graph.Put(Graphs.Edge("k1", "inspect-logs", "produced", "logs.inspected", GraphCategory.Episodic, Observed()));
        graph.Put(Graphs.Edge("k2", "logs.inspected", "caused", "component.identified", GraphCategory.Causal, Observed()));
        for (var i = 0; i < 40; i++)
        {
            graph.Put(Graphs.Node($"noise{i}", "Noise"));
            graph.Put(Graphs.Edge($"n{i}", "inspect-logs", "uses", $"noise{i}", warrant: Observed()));
        }

        var extension = new GraphMemoryExtension(graph, new GraphMemoryOptions { MaxNotes = 5 });
        var world = RestoreServiceDomain.NewWorld();
        var notes = extension.PolicyNotes(Tick(world, world.AvailableTools)).ToList();

        // Bounded, whatever the graph holds: a note per relationship would be injection, not retrieval.
        Assert.Equal(5, notes.Count);
        Assert.All(notes, note => Assert.StartsWith("knowledge (", note, StringComparison.Ordinal));
        Assert.All(notes, note => Assert.Contains("observed", note, StringComparison.Ordinal));
        // And the extension can say what it showed, which is what makes "which evidence contributed" answerable.
        Assert.NotEmpty(extension.LastRetrieved);
    }

    [Fact]
    public void ARumourIsLabelledAsOneAndAConflictSaysSo()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("a"));
        graph.Put(Graphs.Node("b"));
        graph.Put(Graphs.Edge("rumour", "a", "caused", "b", GraphCategory.Causal));
        graph.Put(Graphs.Node("svc"));
        graph.Put(Graphs.Node("t1"));
        graph.Put(Graphs.Node("t2"));
        graph.Put(Graphs.Edge("one", "svc", "ownedBy", "t1", warrant: Observed()));
        graph.Put(Graphs.Edge("two", "svc", "ownedBy", "t2", warrant: Observed()));

        Assert.Equal("knowledge (claimed): a caused b", GraphMemoryExtension.Render(graph.Edge("rumour")!));
        Assert.Contains("contradicted", GraphMemoryExtension.Render(graph.Edge("one")!), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureBecomesAnEpisodeAndASuccessDoesNot()
    {
        var graph = Graph();
        var extension = new GraphMemoryExtension(graph);
        var world = RestoreServiceDomain.NewWorld();
        var tool = world.Tool("apply-patch")!;

        extension.AfterAction(Tick(world, world.AvailableTools), tool,
            new ActionOutcome("apply-patch", true, [], "reported success"), ActionVerdict.Landed);
        Assert.Equal(0, extension.Episodes);

        extension.AfterAction(Tick(world, world.AvailableTools, tick: 2), tool,
            new ActionOutcome("apply-patch", true, [], "reported success"), ActionVerdict.Lied);
        Assert.Equal(1, extension.Episodes);

        // The episode is in the graph as observed experience, attributed to the agent that had it.
        var episodes = graph.Nodes(GraphQuery.All with { Category = GraphCategory.Episodic, EntityType = "episode" });
        Assert.Single(episodes);
        Assert.Equal(ProvenanceKind.Observation, episodes[0].Warrant.Provenance.Kind);
        Assert.Equal(VerificationStatus.Observed, episodes[0].Warrant.Verification);
        Assert.Single(graph.Edges(GraphQuery.All with { Relation = "experienced" }));
    }

    [Fact]
    public void AnOutcomeIsWrittenBackAgainstTheKnowledgeTheAgentActedOn()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool"));
        graph.Put(Graphs.Node("patch.applied", "Fact"));
        graph.Put(Graphs.Edge("k", "apply-patch", "produced", "patch.applied", GraphCategory.Episodic, Observed()));
        var extension = new GraphMemoryExtension(graph);
        var world = RestoreServiceDomain.NewWorld();

        extension.AfterAction(Tick(world, world.AvailableTools), world.Tool("apply-patch")!,
            new ActionOutcome("apply-patch", false, [], "compiler failed"), ActionVerdict.HonestFailure);

        // Confidence moved; verification did not, because one failure is not a retraction.
        var knowledge = graph.Edge("k")!;
        Assert.Equal(0, knowledge.Warrant.Support.Supporting);
        Assert.Equal(1, knowledge.Warrant.Support.Total);
        Assert.Equal(0.0, knowledge.Warrant.Support.Confidence, 10);
        Assert.Equal(VerificationStatus.Observed, knowledge.Warrant.Verification);
    }

    [Fact]
    public void AnExtensionCanBeToldToObserveWithoutActingOnAnything()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Node("change.freeze", "Condition", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Edge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "change.freeze",
            GraphCategory.Task, Observed()));
        var extension = new GraphMemoryExtension(graph, new GraphMemoryOptions
        {
            WithholdsBlockedTools = false,
            RecordsOutcomes = false,
            RemembersFailures = false,
        });
        var world = RestoreServiceDomain.NewWorld();

        Assert.Equal(world.AvailableTools, extension.VisibleTools(Tick(world, world.AvailableTools), world.AvailableTools));
        extension.AfterAction(Tick(world, world.AvailableTools), world.Tool("apply-patch")!,
            new ActionOutcome("apply-patch", true, [], "lied"), ActionVerdict.Lied);
        Assert.Equal(0, extension.Episodes);
    }
}
