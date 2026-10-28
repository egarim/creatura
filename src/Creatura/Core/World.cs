namespace Creatura;

/// <summary>A tool: preconditions in, effects out. The world never tells you which tools are lies.</summary>
public sealed record Tool(string Id, IReadOnlyList<string> Requires, IReadOnlyList<string> Grants, string Kind)
{
    /// <summary>True when the policy must supply a payload (e.g. the code to write) with the call.</summary>
    public bool NeedsPayload { get; init; }

    /// <summary>Extra guidance for a policy that has to produce a payload.</summary>
    public string? PayloadHint { get; init; }
}

/// <summary>What the world reports back — which is not the same as what actually happened.</summary>
public sealed record ActionOutcome(string Tool, bool Succeeded, IReadOnlyList<string> Granted, string? Observation);

/// <summary>
/// The world as read: the fact registry (the only truth), the tool catalogue, and the one honest success
/// check. Everything that measures a world (a task's progress, a set point, the quarantine's visible set, the
/// control layer) codes against this; only the loop that acts needs <see cref="IWorld"/>.
/// </summary>
public interface IWorldView
{
    IReadOnlySet<string> Facts { get; }

    IReadOnlyList<Tool> AvailableTools { get; }

    IReadOnlyList<Tool> AllTools { get; }

    bool Has(string fact);

    Tool? Tool(string id);

    bool EffectsLanded(Tool tool);

    /// <summary>
    /// The three-way verdict on one action, in report-first order: a reported failure is an honest failure, whatever
    /// the registry holds; a reported success is landed when the registry holds what the tool promised, and a lie
    /// otherwise. The landed-first order the host has always used reads <see cref="EffectsLanded"/> before the report;
    /// the control layer keeps that under <see cref="JudgeRule.ThreeWay"/> and uses this under <see cref="JudgeRule.ReportFirst"/>.
    /// </summary>
    ActionVerdict Verdict(Tool tool, ActionOutcome outcome) =>
        !outcome.Succeeded ? ActionVerdict.HonestFailure
        : EffectsLanded(tool) ? ActionVerdict.Landed
        : ActionVerdict.Lied;
}

/// <summary>
/// The surface every agent codes against: the view, plus acting on it. <see cref="World"/> is the toy
/// service-restore domain; <c>BiologicalAgents.CodeWorld.CodeWorld</c> is a real one that edits files and
/// runs the compiler.
/// </summary>
public interface IWorld : IWorldView
{
    int Ticks { get; }

    ActionOutcome Act(string toolId, string? payload = null);
}

/// <summary>
/// A world that wants to know when each tick of the loop begins: a shared world with several machines in it uses it to
/// give each machine's loop its turn (docs/design-machine-vessels.md). The loop calls it first thing in every tick,
/// before anything else happens; a world that does not implement it is untouched.
/// </summary>
public interface ITickedWorld : IWorld
{
    void BeginTick(int tick);
}

/// <summary>A world that can give an independent copy of itself, for a habitat to isolate (<see cref="IHabitat.Isolate"/>).</summary>
public interface ICloneableWorld : IWorld
{
    /// <summary>A copy with the same facts, tools and barrier state; acting on it leaves this world untouched.</summary>
    IWorld Clone();

    /// <summary>The world's copy when it can give one; a world that cannot is said so, not silently snapshotted.</summary>
    static IWorld CloneOf(IWorld world) => world is ICloneableWorld c
        ? c.Clone()
        : throw new NotSupportedException($"{world.GetType().Name} cannot be copied: derive it from CatalogueWorld or implement ICloneableWorld");
}

/// <summary>
/// A tiny deterministic world: a set of facts, a set of tools, and injected faults.
/// Faults are the barriers the designs are evaluated against. Tools are kept in id order.
/// </summary>
public sealed class World : CatalogueWorld
{
    public World(IEnumerable<Tool> tools, IEnumerable<string> facts)
        : base(tools.DistinctBy(t => t.Id).OrderBy(t => t.Id, StringComparer.Ordinal), facts)
    {
    }

    /// <summary>
    /// The nastiest barrier: the tool reports success and grants nothing, once.
    /// An agent that trusts self-report never notices; one that checks postconditions does.
    /// </summary>
    public void MakeLiar(string toolId)
    {
        if (!Faults.Any(f => f.Kind == FaultKind.Lie && f.Tool == toolId)) Inject(FaultRule.LieOnce(toolId));
    }
}
