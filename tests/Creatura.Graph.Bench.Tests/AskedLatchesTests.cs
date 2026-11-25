using Creatura;
using Creatura.Graph;

namespace Creatura.Graph.Bench.Tests;

/// <summary>
/// The asking fixture: <see cref="TwoLatches"/>' commitment world with the <c>blockedBy</c> route removed, so the only
/// way through is to ask and then act on the answer. What is asserted here is everything that can be asserted without
/// a model — the plumbing, the invariants, and the fact that the planner cannot reach any of it. Whether asking
/// changes what a seat does is the model-seat matrix's question, and these tests are careful not to imply they
/// answered it.
/// </summary>
public class AskedLatchesTests
{
    private sealed class PlainRegulator : IRegulator
    {
        public Identity Identity { get; } = new("operator", "you");
    }

    private static ReferenceLoop Loop(AgentTask task, AgentOptions options, string name) =>
        new(new PlainRegulator(), new PlannerPolicy(), task, options, name: name);

    private static ICognitiveGraph Known() => AskedLatches.Knowledge(new GraphCaller("bach"));

    private static AskedWorld Asking(ICognitiveGraph graph) => new(AskedLatches.AnsweringFrom(graph));

    private static TickContext Tick(IWorld world) => new()
    {
        Agent = "bach",
        World = world,
        Task = AskedWorld.Task,
        Tick = 1,
        MaxTicks = 20,
        Visible = world.AvailableTools,
        Notes = [],
        Trace = [],
    };

    [Fact]
    public void TheWorldAnswersWhatWasAsked()
    {
        var world = Asking(Known());

        var asked = world.Act(AskedLatches.Recall, "which latch holds");

        // The answer arrives as the action's observation, which is the channel that works: the loop writes it into
        // the seat's history through Remembered, and history is read on every tick after it. Notes are not.
        Assert.True(asked.Succeeded);
        Assert.NotNull(asked.Observation);
        Assert.Contains("commit-left shears latch.rusted", asked.Observation, StringComparison.Ordinal);
        Assert.Contains("commit-right holds latch.sound", asked.Observation, StringComparison.Ordinal);
        // Asking grants nothing: an answer is not a fact about the world.
        Assert.Empty(asked.Granted);
        Assert.False(world.Has(TwoLatches.Open));
    }

    [Fact]
    public void AQuestionThatNamesNothingIsAnsweredWithNothing()
    {
        var world = Asking(Known());

        var asked = world.Act(AskedLatches.Recall, "the weather in Ceiba");

        Assert.True(asked.Succeeded);
        Assert.Equal(AskedWorld.NothingRecorded, asked.Observation);
    }

    [Fact]
    public void AnArmWithNoMemoryMayAskAndHasNothingToBeTold()
    {
        var world = new AskedWorld();

        Assert.Equal(AskedWorld.NothingRecorded, world.Act(AskedLatches.Recall, "which latch holds").Observation);
    }

    [Fact]
    public void WithholdingCannotWinThisFixture()
    {
        var graph = Known();
        var extension = new GraphMemoryExtension(graph);
        var world = Asking(graph);

        // The load-bearing difference from CommitWorld. Nothing here uses the blockedBy convention, so the extension
        // has no reason to hold anything back and the fatal tool stays on offer. If this ever fails the fixture has
        // quietly become TwoLatches again, and a win would say nothing about asking.
        Assert.Empty(graph.Adjacent("commit-left", GraphQuery.All with { Relation = GraphMemoryExtension.BlockedBy }));
        Assert.Empty(graph.Adjacent("commit-right", GraphQuery.All with { Relation = GraphMemoryExtension.BlockedBy }));
        Assert.Equal(world.AvailableTools, extension.VisibleTools(Tick(world), world.AvailableTools));
        Assert.Contains(world.AvailableTools, t => t.Id == "commit-left");
        Assert.Contains(world.AvailableTools, t => t.Id == "commit-right");
    }

    [Fact]
    public void AskingIsNotEvidence()
    {
        var graph = Known();
        var answer = Asking(graph).Act(AskedLatches.Recall, "which latch holds").Observation!;

        // AfterAction writes a landed action's observation back into the graph at Observed, and recall always lands.
        // So an answer the extractor could parse would be re-ingested as fresh evidence for itself, and asking a
        // question enough times would make it true. The rendered form is deliberately outside the extractor's
        // grammar: asking must not be evidence.
        var said = new LineExtractor(graph.Schema).Extract(answer,
            new GraphProvenance(ProvenanceKind.Observation, "bach"), graph.Caller.Scope);

        Assert.Empty(said.Edges);
        Assert.Empty(said.Nodes);
        Assert.NotEmpty(said.Skipped);
    }

    [Fact]
    public void AWrongCommitmentStillCannotBeUndone()
    {
        var world = Asking(Known());

        var wrong = world.Act("commit-left");

        Assert.False(wrong.Succeeded);
        Assert.False(world.Has(TwoLatches.Open));
        Assert.True(world.Has(TwoLatches.Broken), "the registry has to show the latch actually broken, not merely a refusal");
        var right = world.Act("commit-right");
        Assert.False(right.Succeeded);
        Assert.False(world.Has(TwoLatches.Open));
    }

    [Fact]
    public async Task ThePlannerCannotAskSoThisFixtureMeasuresNothingOffline()
    {
        var graph = Known();
        var world = Asking(graph);
        var run = await Loop(AskedWorld.Task, AgentOptions.With(new GraphMemoryExtension(graph)), "commit")
            .RunAsync(world, 20);

        // Pinned so that nobody reads a green deterministic test as a measured result. PlannerPolicy filters
        // NeedsPayload because breadth-first search has no generator, so recall is not in the planner's option space
        // at all: it guesses, and guessing loses. Every arm fails this fixture offline, which is exactly why the only
        // honest measurement of asking is the model-seat matrix.
        Assert.False(run.GoalReached);
        Assert.True(world.Has(TwoLatches.Broken), "the planner should have guessed left and broken the latch");
        Assert.DoesNotContain(run.Trace, t => t.Action == AskedLatches.Recall);
    }
}
