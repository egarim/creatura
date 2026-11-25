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

    private static async Task<AgentRun> Run(IAgentExtension extension)
    {
        IAgent agent = new ReferenceLoop(new PlainRegulator(), new PlannerPolicy(), RestoreServiceDomain.Task,
            AgentOptions.With(extension), name: "graph");
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

    private static ICognitiveGraph Seeded()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("inspect-logs", "Tool", warrant: Observed()));
        graph.Put(Graphs.Node("logs.inspected", "Fact", warrant: Observed()));
        graph.Put(Graphs.Edge("k1", "inspect-logs", "produced", "logs.inspected", GraphCategory.Episodic, Observed()));
        return graph;
    }

    private static TickContext AtFreedom(IWorld world, double freedom)
    {
        var context = Tick(world, world.AvailableTools);
        context.Freedom = freedom;
        return context;
    }

    [Fact]
    public void AnOnDemandExtensionStaysQuietWhileTheAgentIsCoping()
    {
        var extension = new GraphMemoryExtension(Seeded(), new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand });
        var world = RestoreServiceDomain.NewWorld();

        // Full freedom: nothing is going wrong, so nothing is worth the tokens.
        Assert.Empty(extension.PolicyNotes(AtFreedom(world, 1.0)));
        Assert.Empty(extension.LastRetrieved);
        // Freedom has fallen under the threshold: now it looks.
        Assert.NotEmpty(extension.PolicyNotes(AtFreedom(world, 0.1)));
        Assert.NotEmpty(extension.LastRetrieved);
    }

    [Fact]
    public void AnAlwaysOnExtensionSpeaksWhetherOrNotItIsNeeded()
    {
        var extension = new GraphMemoryExtension(Seeded());
        var world = RestoreServiceDomain.NewWorld();

        Assert.Equal(GraphMemoryTrigger.Always, extension.Options.Trigger);
        Assert.NotEmpty(extension.PolicyNotes(AtFreedom(world, 1.0)));
        Assert.NotEmpty(extension.PolicyNotes(AtFreedom(world, 0.1)));
    }

    [Fact]
    public void WithholdingABlockedToolIsNotGatedByPressure()
    {
        var graph = Graph();
        graph.Put(Graphs.Node("apply-patch", "Tool", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Node("change.freeze", "Condition", GraphCategory.Task, Observed()));
        graph.Put(Graphs.Edge("gate", "apply-patch", GraphMemoryExtension.BlockedBy, "change.freeze",
            GraphCategory.Task, Observed()));
        var extension = new GraphMemoryExtension(graph, new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand });
        var world = RestoreServiceDomain.NewWorld();

        // A calm agent still is not offered the blocked tool. Gating the constraint on stress would let an agent walk
        // into a trap for as long as it felt confident, which is exactly when traps get walked into.
        Assert.Empty(extension.PolicyNotes(AtFreedom(world, 1.0)));
        Assert.DoesNotContain(world.Tool("apply-patch")!,
            extension.VisibleTools(AtFreedom(world, 1.0), world.AvailableTools));
    }

    [Fact]
    public void NoRouteWritesWhatItFindsIntoTheNotesAndProposesNoTool()
    {
        var extension = new GraphMemoryExtension(Seeded(), new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand });
        var context = AtFreedom(RestoreServiceDomain.NewWorld(), 1.0);

        // No route is the one moment retrieval is always worth it, whatever the freedom reading says — and a hand-off
        // may follow, so there might be no next tick to put the notes in.
        Assert.Null(extension.NoRoute(context));
        Assert.NotEmpty(context.Notes);
        Assert.All(context.Notes, note => Assert.StartsWith("knowledge (", note, StringComparison.Ordinal));
    }

    [Fact]
    public void AnAlwaysOnExtensionDoesNotSpeakTwiceWhenThereIsNoRoute()
    {
        var extension = new GraphMemoryExtension(Seeded());
        var context = AtFreedom(RestoreServiceDomain.NewWorld(), 1.0);

        // It already retrieved for this tick through PolicyNotes; NoRoute is the on-demand arm's hook, not a second
        // helping of the same knowledge.
        Assert.Null(extension.NoRoute(context));
        Assert.Empty(context.Notes);
    }

    [Fact]
    public async Task TheDemandGateNeverClosesBecauseTheRealLoopRunsAtTheFreedomFloor()
    {
        var always = new GraphMemoryExtension(Seeded());
        var demand = new GraphMemoryExtension(Seeded(), new GraphMemoryOptions { Trigger = GraphMemoryTrigger.OnDemand });

        var withAlways = await Run(always);
        var withDemand = await Run(demand);

        // A defect, pinned so it cannot be mistaken for a working gate. Measured rather than assumed: on a task the
        // planner completes in six ticks, freedom never rises above Plasticity's floor of 0.2, and the threshold is
        // 0.35. The gate is therefore open on every tick, OnDemand is indistinguishable from Always, and that — not a
        // fixture that cannot separate them — is why the two arms tied in the model-seat matrix. Freedom does not
        // discriminate calm from stressed here, so it cannot be the trigger; NoRoute can, because it only fires when
        // the policy has genuinely found no path.
        Assert.True(withAlways.GoalReached);
        Assert.True(withDemand.GoalReached);
        Assert.All(withDemand.Trace, t => Assert.True(t.Freedom <= GraphMemoryOptions.Default.PressureBelowFreedom,
            $"freedom {t.Freedom:0.00} cleared the threshold, so the gate can close and this test is now stale"));
        Assert.True(always.NotesEmitted > 0, "the counter should have something to count");
        Assert.Equal(always.NotesEmitted, demand.NotesEmitted);
        Assert.Equal(always.NoteCharacters, demand.NoteCharacters);
        Assert.Equal(always.Retrievals, demand.Retrievals);
    }
}
