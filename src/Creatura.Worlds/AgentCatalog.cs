using Creatura.Orienting;

namespace Creatura.Worlds;

/// <summary>What an agent is on the bench: a design under test, or a control it is compared against.</summary>
public enum AgentRole
{
    Design,
    Control,
}

/// <summary>Builds an agent. The seed matters only to an agent that draws on a random source (the random walker).</summary>
public delegate IAgent AgentFactory(IPolicy policy, AgentTask task, AgentOptions options, int seed);

public sealed record AgentEntry(string Name, AgentRole Role, AgentFactory Make);

/// <summary>
/// The agents by the names the runners, the pages and the goldens use. The five designs are
/// <see cref="ReferenceLoop"/>s over their regulators with the research facades' names, so a trace from here is the
/// facade's trace row for row; the controls are plain agents, never a configuration of the control layer.
/// </summary>
public static class AgentCatalog
{
    private static readonly object Gate = new();
    private static readonly List<AgentEntry> Order = [];
    private static readonly Dictionary<string, AgentEntry> ByName = new(StringComparer.Ordinal);

    /// <summary>The colony's layer: the designs' options with its rule that no route lifts the quarantine at once.</summary>
    public static readonly ControlLayerOptions ColonyLayer = ControlLayerOptions.Design with { ClearQuarantineOnNoRoute = true };

    /// <summary>The map-keeper's layer: the report-first three-way judge, honest failures habituated, failures never reset.</summary>
    public static readonly ControlLayerOptions MapKeeperLayer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ReportFirst };

    /// <summary>The orienting agent's layer: the designs' options with the three-way judge, so an honest failure is a punishment of its own weight.</summary>
    public static readonly ControlLayerOptions OrientingLayer = ControlLayerOptions.Design with { JudgeRule = JudgeRule.ThreeWay };

    static AgentCatalog()
    {
        Register("levin", AgentRole.Design, (p, t, o, _) => new ReferenceLoop(new ColonyRegulator(Identity.Levin, leak: 0.5, ErrorChannel.Tissue), p, t, o, ColonyLayer, "Levin colony"));
        Register("bach", AgentRole.Design, (p, t, o, _) => new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), p, t, o, ControlLayerOptions.Design, "Bach agent"));
        Register("hybrid", AgentRole.Design, (p, t, o, _) => new ReferenceLoop(new HybridRegulator(Identity.Hybrid, Identity.Cell, stage: 3), p, t, o, ControlLayerOptions.Design, "Hybrid colony"));
        Register("peterson", AgentRole.Design, (p, t, o, _) => new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper, Posture.MapKeeper, maxDepth: 4, unknownCooldown: 6), p, t, o, MapKeeperLayer, "Peterson map-keeper"));
        Register("orienting", AgentRole.Design, (p, t, o, _) => new ReferenceLoop(new OrientingRegulator(Identity.Orienting), p, t, o, OrientingLayer, "orienting agent"));
        // The adversary: the map-keeper's stack with the lie allowed (Figure 58). A control, not a design.
        Register("adversary", AgentRole.Control, (p, t, o, _) => new ReferenceLoop(new MapKeeperRegulator(Identity.MapKeeper, Posture.Adversary, maxDepth: 4, unknownCooldown: 6), p, t, o, MapKeeperLayer, "adversary"));
        Register("careless", AgentRole.Control, (_, t, _, _) => new CarelessAgent(t));
        Register("random", AgentRole.Control, (_, t, _, seed) => new RandomWalker(t, seed));
        Register("oracle", AgentRole.Control, (_, t, _, _) => new OracleAgent(t));
        Register("behaviourist", AgentRole.Control, (_, t, _, _) => new BehaviouristAgent(t));
    }

    /// <summary>Adds or replaces an agent under its name.</summary>
    public static void Register(string name, AgentRole role, AgentFactory make)
    {
        lock (Gate)
        {
            if (ByName.TryGetValue(name, out var old)) Order.Remove(old);
            var entry = new AgentEntry(name, role, make);
            ByName[name] = entry;
            Order.Add(entry);
        }
    }

    public static AgentEntry? TryFind(string name)
    {
        lock (Gate) return ByName.GetValueOrDefault(name);
    }

    public static AgentEntry Find(string name) => TryFind(name) ?? throw new ArgumentException($"unknown agent '{name}'; known: {string.Join(", ", Names)}");

    /// <summary>The agent by name. The planner sits in the seat, the toy task is the task and no extension is loaded unless told otherwise.</summary>
    public static IAgent Make(string name, IPolicy? policy = null, AgentTask? task = null, AgentOptions? options = null, int seed = 1) =>
        Find(name).Make(policy ?? new PlannerPolicy(), task ?? RestoreServiceDomain.Task, options ?? AgentOptions.None, seed);

    public static AgentRole RoleOf(string name) => Find(name).Role;

    public static IReadOnlyList<AgentEntry> All
    {
        get { lock (Gate) return Order.ToList(); }
    }

    public static IReadOnlyList<string> Names => All.Select(e => e.Name).ToList();

    public static IReadOnlyList<string> Designs => All.Where(e => e.Role == AgentRole.Design).Select(e => e.Name).ToList();

    public static IReadOnlyList<string> Controls => All.Where(e => e.Role == AgentRole.Control).Select(e => e.Name).ToList();
}
