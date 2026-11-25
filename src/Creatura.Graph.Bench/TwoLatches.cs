namespace Creatura.Graph.Bench;

/// <summary>
/// A world pair built so that memory is the only thing that can save you.
/// <para>
/// The existing catalogue worlds put everything an agent needs in the prompt or in the registry, which is why a
/// competent seat solves them unaided and why the pilot in <c>experiments/graph-model/pilot.md</c> could not
/// discriminate between the arms. These two are different in a way that is deliberate and narrow:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="ProbeWorld"/> is cheap, safe and informative. Trying either latch costs one tick and
/// reports what it found. Everything an agent could want to know is knowable here.</description></item>
/// <item><description><see cref="CommitWorld"/> is silent and irreversible. Neither choice has a precondition, neither
/// observation says which is right, and committing to the wrong one adds <c>latch.broken</c> to the registry, which
/// rewrites the right choice's precondition into something nothing can ever grant. There is no second
/// attempt.</description></item>
/// </list>
/// <para>
/// So the only route through <see cref="CommitWorld"/> is knowledge carried in from <see cref="ProbeWorld"/>. An arm
/// that cannot carry it is not stupid, it is amnesiac, and it fails at the rate it guesses — which is exactly the
/// thing worth measuring, and exactly what the toy worlds could not show.
/// </para>
/// </summary>
public static class TwoLatches
{
    /// <summary>The fact the wrong commitment leaves behind, and the one that makes the right choice impossible afterwards.</summary>
    public const string Broken = "latch.broken";

    /// <summary>The goal of the commitment world.</summary>
    public const string Open = "door.open";

    /// <summary>The goal of the probe world.</summary>
    public const string Surveyed = "survey.done";

    /// <summary>Which latch actually holds. Arbitrary on purpose: nothing in the commitment world hints at it, and no amount of reasoning finds it.</summary>
    public const string CorrectLatch = "right";

    /// <summary>What probing the wrong latch reports, as a statement the graph's own extractor can read.</summary>
    public const string LeftObservation = "commit-left -> blockedBy -> latch.rusted";

    /// <summary>What probing the right latch reports.</summary>
    public const string RightObservation = "commit-right -> clears -> latch.sound";
}

/// <summary>
/// The safe, informative half. Both latches can be probed, both probes report what they found, and nothing here can
/// be gotten wrong. The observation text is written in the corpus's own line notation so that an arm which ingests
/// observations stores a statement it can act on later, rather than a sentence it has to re-read.
/// </summary>
public sealed class ProbeWorld : CatalogueWorld
{
    public ProbeWorld() : base(Tools(), [])
    {
    }

    public static IReadOnlyList<Tool> Tools() =>
    [
        new("probe-left", [], ["left.probed"], "read"),
        new("probe-right", [], ["right.probed"], "read"),
        new("finish-survey", ["left.probed", "right.probed"], [TwoLatches.Surveyed], "close"),
    ];

    protected override string? Observation(Tool tool) => tool.Id switch
    {
        "probe-left" => TwoLatches.LeftObservation,
        "probe-right" => TwoLatches.RightObservation,
        _ => base.Observation(tool),
    };

    public static AgentTask Task { get; } = new("survey the latches",
        "Both latches probed and the survey closed.",
        facts => facts.Contains(TwoLatches.Surveyed),
        world => world.Has(TwoLatches.Surveyed) ? 1.0 : 0.0,
        world => world.Has(TwoLatches.Surveyed) ? 1.0 : 0.0,
        // No habit: nothing here is a tool an agent should reach for first.
        []);

    public override ProbeWorld Clone() => new();
}

/// <summary>
/// The silent, irreversible half. Two commitments, neither with a precondition, nothing in the world saying which is
/// right. The wrong one reports failure honestly and adds <see cref="TwoLatches.Broken"/> to the registry, and
/// <see cref="Shape"/> then rewrites the right one's precondition into a fact no tool grants — so a wrong guess does
/// not cost a tick, it costs the run.
/// </summary>
public sealed class CommitWorld : CatalogueWorld
{
    public CommitWorld() : base(Tools(), [], Trap())
    {
    }

    public static IReadOnlyList<Tool> Tools() =>
    [
        // "write", not "release": a release-kind tool is gated behind stage 4, and this world is two ticks long, so a
        // release gate would refuse both choices and the fixture would measure the stage ladder instead of memory.
        // The irreversibility comes from the fault rule below, which is the part that matters here.
        new("commit-left", [], [TwoLatches.Open], "write"),
        new("commit-right", [], [TwoLatches.Open], "write"),
    ];

    private static IReadOnlyList<FaultRule> Trap() =>
    [
        // Honest failure: it says so, and it grants nothing. The Adds is what makes it final.
        FaultRule.HonestFailure("commit-left", "the left latch shears off in your hand", adds: TwoLatches.Broken),
    ];

    /// <summary>
    /// Once the latch is broken the remaining commitment needs a fact nothing in this world can grant. Rewriting the
    /// precondition rather than revoking the tool keeps the failure visible: the agent can still try, and is still
    /// told why it cannot.
    /// </summary>
    protected override Tool Shape(Tool tool) =>
        Facts.Contains(TwoLatches.Broken) && tool.Id == "commit-right"
            ? new Tool(tool.Id, [.. tool.Requires, "latch.intact"], tool.Grants, tool.Kind)
            : base.Shape(tool);

    public static AgentTask Task { get; } = new("open the door",
        "The door is open, by committing to the latch that holds.",
        facts => facts.Contains(TwoLatches.Open),
        world => world.Has(TwoLatches.Open) ? 1.0 : 0.0,
        world => world.Has(TwoLatches.Open) ? 1.0 : 0.0,
        // No habit on purpose. A habit here would be a hint about which latch to take, and the fixture is worthless
        // the moment anything in the commitment world points at the answer.
        []);

    public override CommitWorld Clone() => new();
}
